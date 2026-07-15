package com.bob.android.transfer

import android.content.ContentResolver
import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import com.bob.android.network.BobHttpsConnection
import com.bob.android.network.BobTransferAccepted
import com.bob.android.network.BobTransferCompleted
import com.bob.android.network.BobTransferDigest
import com.bob.android.network.BobTransferFailed
import com.bob.android.network.BobTransferKind
import com.bob.android.network.BobTransferOffer
import com.bob.android.network.BobTransferProgress
import com.bob.android.network.BobTransferTerminalAck
import com.bob.android.network.BobTransferTerminalState
import com.bob.android.network.BobWebSocketSession
import java.io.ByteArrayOutputStream
import java.io.FileNotFoundException
import java.io.IOException
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.text.Normalizer
import java.time.Instant
import java.util.Base64
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import okhttp3.Call
import okhttp3.MediaType
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.Response
import okio.BufferedSink
import org.json.JSONObject

enum class OnlineTransferDirection {
    Outgoing,
    Incoming,
}

enum class OnlineTransferState {
    Queued,
    Offered,
    Accepted,
    Transferring,
    Verifying,
    Completed,
    Failed,
}

data class OnlineTransferItem(
    val transferId: String,
    val direction: OnlineTransferDirection,
    val kind: BobTransferKind,
    val name: String,
    val size: Long?,
    val mediaType: String,
    val state: OnlineTransferState,
    val bytesTransferred: Long,
    val storedName: String?,
    val localContentUri: Uri?,
    val errorMessage: String?,
    val createdAt: Instant,
)

/** Online-only transfer worker. Durable/offline recovery belongs to a later repository slice. */
class OnlineTransferCoordinator(
    context: Context,
    private val scope: CoroutineScope,
) {
    private val resolver = context.applicationContext.contentResolver
    private val lock = Any()
    private val incomingOfferMutex = Mutex()
    private val records = linkedMapOf<UUID, TransferRecord>()
    private val mutableItems = MutableStateFlow<List<OnlineTransferItem>>(emptyList())
    val items: StateFlow<List<OnlineTransferItem>> = mutableItems.asStateFlow()

    private var transport: ActiveTransport? = null

    fun attach(connection: BobHttpsConnection, session: BobWebSocketSession) {
        synchronized(lock) {
            transport = ActiveTransport(
                connection = connection,
                session = session,
                contentClient = connection.client.newBuilder()
                    .readTimeout(0, TimeUnit.MILLISECONDS)
                    .writeTimeout(0, TimeUnit.MILLISECONDS)
                    .callTimeout(0, TimeUnit.MILLISECONDS)
                    .build(),
            )
        }
        drainOutgoing()
    }

    fun detach(session: BobWebSocketSession? = null) {
        val cleanup = mutableListOf<Uri>()
        synchronized(lock) {
            val current = transport
            if (session != null && current?.session !== session) return
            transport = null
            records.values.forEach { record ->
                record.call?.cancel()
                record.call = null
                if (!record.item.state.isTerminal()) {
                    record.pendingUri?.let(cleanup::add)
                    record.pendingUri = null
                    record.item = record.item.copy(
                        state = OnlineTransferState.Failed,
                        errorMessage = "Connection interrupted.",
                    )
                }
            }
            publishLocked()
        }
        cleanup.forEach { uri -> runCatching { resolver.delete(uri, null, null) } }
    }

    suspend fun queueOutgoing(uris: List<Uri>, kind: BobTransferKind) {
        for (uri in uris.distinct()) {
            val transferId = UUID.randomUUID()
            val createdAt = Instant.now()
            val metadata = runCatching {
                withContext(Dispatchers.IO) { readSourceMetadata(uri, kind) }
            }
            val record = metadata.fold(
                onSuccess = { source ->
                    TransferRecord(
                        item = OnlineTransferItem(
                            transferId = transferId.toString(),
                            direction = OnlineTransferDirection.Outgoing,
                            kind = kind,
                            name = source.name,
                            size = source.size,
                            mediaType = source.mediaType,
                            state = OnlineTransferState.Queued,
                            bytesTransferred = 0L,
                            storedName = null,
                            localContentUri = uri,
                            errorMessage = null,
                            createdAt = createdAt,
                        ),
                        sourceUri = uri,
                    )
                },
                onFailure = { error ->
                    TransferRecord(
                        item = OnlineTransferItem(
                            transferId = transferId.toString(),
                            direction = OnlineTransferDirection.Outgoing,
                            kind = kind,
                            name = "Unavailable content",
                            size = null,
                            mediaType = DEFAULT_MEDIA_TYPE,
                            state = OnlineTransferState.Failed,
                            bytesTransferred = 0L,
                            storedName = null,
                            localContentUri = null,
                            errorMessage = safeFailureMessage(error, "Could not read selected content."),
                            createdAt = createdAt,
                        ),
                    )
                },
            )
            synchronized(lock) {
                records[transferId] = record
                publishLocked()
            }
        }
        drainOutgoing()
    }

    fun onOffer(offer: BobTransferOffer) {
        scope.launch { acceptIncoming(offer) }
    }

    fun onAccepted(accepted: BobTransferAccepted) {
        val start = synchronized(lock) {
            val record = records[accepted.transferId] ?: return
            if (record.item.direction != OnlineTransferDirection.Outgoing ||
                record.item.state != OnlineTransferState.Offered
            ) {
                return
            }
            record.item = record.item.copy(
                state = OnlineTransferState.Accepted,
                storedName = accepted.plannedName,
            )
            publishLocked()
            record
        }
        scope.launch { upload(start) }
    }

    fun onDigest(digest: BobTransferDigest) {
        var conflict = false
        synchronized(lock) {
            val record = records[digest.transferId] ?: return
            if (record.item.direction != OnlineTransferDirection.Incoming || record.item.state.isTerminal()) {
                return
            }
            val previous = record.remoteDigest
            if (previous != null && !previous.sameDigestPayload(digest)) {
                conflict = true
            } else {
                record.remoteDigest = digest
            }
        }
        if (conflict) {
            failTransfer(digest.transferId, "invalid_metadata", "Conflicting transfer digest.")
        } else {
            maybePublishIncoming(digest.transferId)
        }
    }

    fun onCompleted(completed: BobTransferCompleted) {
        var duplicateSession: BobWebSocketSession? = null
        val result = synchronized(lock) {
            val record = records[completed.transferId] ?: return
            if (record.item.direction != OnlineTransferDirection.Outgoing) {
                return
            }
            if (record.item.state == OnlineTransferState.Completed) {
                duplicateSession = transport?.session
                return@synchronized true to duplicateSession
            }
            val valid = record.localBytes == completed.bytes &&
                record.localSha256 == completed.sha256
            if (!valid) {
                record.item = record.item.copy(
                    state = OnlineTransferState.Failed,
                    errorMessage = "Computer returned conflicting completion metadata.",
                )
                publishLocked()
                false to transport?.session
            } else {
                record.item = record.item.copy(
                    state = OnlineTransferState.Completed,
                    bytesTransferred = completed.bytes,
                    storedName = completed.storedName,
                    errorMessage = null,
                )
                record.terminalAckPending = false
                publishLocked()
                true to transport?.session
            }
        }
        if (result.first) {
            (duplicateSession ?: result.second)?.acknowledgeTransferTerminal(
                completed.transferId,
                BobTransferTerminalState.Completed,
            )
            drainOutgoing()
        } else {
            result.second?.close(4400, "invalid_metadata")
        }
    }

    fun onFailed(failed: BobTransferFailed) {
        val cleanup: Uri?
        val session: BobWebSocketSession?
        var completedWins = false
        synchronized(lock) {
            val record = records[failed.transferId] ?: return
            session = transport?.session
            if (record.item.state == OnlineTransferState.Completed) {
                completedWins = true
                cleanup = null
            } else {
                cleanup = record.pendingUri
                record.pendingUri = null
                record.call?.cancel()
                record.call = null
                record.item = record.item.copy(
                    state = OnlineTransferState.Failed,
                    errorMessage = failed.message.ifBlank { failed.code },
                )
                record.terminalAckPending = false
                publishLocked()
            }
        }
        if (completedWins) {
            session?.acknowledgeTransferTerminal(failed.transferId, BobTransferTerminalState.Completed)
            return
        }
        cleanup?.let { uri -> scope.launch(Dispatchers.IO) { runCatching { resolver.delete(uri, null, null) } } }
        session?.acknowledgeTransferTerminal(failed.transferId, BobTransferTerminalState.Failed)
        drainOutgoing()
    }

    fun onTerminalAck(acknowledgement: BobTransferTerminalAck) {
        synchronized(lock) {
            val record = records[acknowledgement.transferId] ?: return
            val matches = when (acknowledgement.state) {
                BobTransferTerminalState.Completed -> record.item.state == OnlineTransferState.Completed
                BobTransferTerminalState.Failed -> record.item.state == OnlineTransferState.Failed
                BobTransferTerminalState.Canceled -> false
            }
            if (matches) record.terminalAckPending = false
        }
    }

    fun onProgress(progress: BobTransferProgress) {
        synchronized(lock) {
            val record = records[progress.transferId] ?: return
            if (record.item.direction != OnlineTransferDirection.Outgoing || record.item.state.isTerminal()) {
                return
            }
            record.item = record.item.copy(bytesTransferred = progress.bytes)
            publishLocked()
        }
    }

    private suspend fun acceptIncoming(offer: BobTransferOffer) {
        lateinit var currentTransport: ActiveTransport
        lateinit var pending: PendingMedia
        lateinit var record: TransferRecord
        incomingOfferMutex.lock()
        try {
            currentTransport = synchronized(lock) { transport } ?: return
            val duplicate = synchronized(lock) { records[offer.transferId] }
            if (duplicate != null) {
                val same = duplicate.item.direction == OnlineTransferDirection.Incoming &&
                    duplicate.item.kind == offer.kind &&
                    duplicate.item.name == offer.name &&
                    duplicate.item.size == offer.size &&
                    duplicate.item.mediaType == offer.mediaType
                if (!same) {
                    currentTransport.session.sendTransferFailed(
                        offer.transferId,
                        "invalid_metadata",
                        "The transfer ID conflicts with an existing transfer.",
                        retryable = false,
                    )
                    return
                }
                when (duplicate.item.state) {
                    OnlineTransferState.Completed -> currentTransport.session.sendTransferCompleted(
                        transferId = offer.transferId,
                        bytes = duplicate.localBytes ?: duplicate.item.bytesTransferred,
                        sha256 = duplicate.localSha256.orEmpty(),
                        storedName = duplicate.item.storedName ?: duplicate.item.name,
                    )
                    OnlineTransferState.Failed -> currentTransport.session.sendTransferFailed(
                        offer.transferId,
                        "io_error",
                        duplicate.item.errorMessage ?: "Transfer failed.",
                    )
                    else -> duplicate.item.storedName?.let { plannedName ->
                        currentTransport.session.acceptTransfer(
                            offer.transferId,
                            plannedName,
                            offer.envelopeId,
                        )
                    }
                }
                return
            }

            val directionBusy = synchronized(lock) {
                records.values.any { activeRecord ->
                    activeRecord.item.direction == OnlineTransferDirection.Incoming &&
                        !activeRecord.item.state.isTerminal()
                }
            }
            if (directionBusy) {
                currentTransport.session.sendTransferFailed(
                    offer.transferId,
                    "direction_busy",
                    "Another computer-to-phone transfer is active.",
                )
                return
            }

            pending = try {
                withContext(Dispatchers.IO) { createPendingMedia(offer) }
            } catch (error: Exception) {
                currentTransport.session.sendTransferFailed(
                    offer.transferId,
                    error.toProtocolCode(),
                    safeFailureMessage(error, "Could not create the Android destination."),
                )
                return
            }

            record = TransferRecord(
                item = OnlineTransferItem(
                    transferId = offer.transferId.toString(),
                    direction = OnlineTransferDirection.Incoming,
                    kind = offer.kind,
                    name = offer.name,
                    size = offer.size,
                    mediaType = offer.mediaType,
                    state = OnlineTransferState.Accepted,
                    bytesTransferred = 0L,
                    storedName = pending.storedName,
                    localContentUri = null,
                    errorMessage = null,
                    createdAt = offer.createdAt,
                ),
                pendingUri = pending.uri,
                offerEnvelopeId = offer.envelopeId,
            )
            synchronized(lock) {
                records[offer.transferId] = record
                publishLocked()
            }
        } finally {
            incomingOfferMutex.unlock()
        }

        if (currentTransport.session.acceptTransfer(
                offer.transferId,
                pending.storedName,
                offer.envelopeId,
            ) == null
        ) {
            failTransfer(offer.transferId, "network_interrupted", "Could not accept the transfer.")
            return
        }
        download(record)
    }

    private fun drainOutgoing() {
        val next = synchronized(lock) {
            val currentTransport = transport ?: return
            val active = records.values.any { record ->
                record.item.direction == OnlineTransferDirection.Outgoing &&
                    !record.item.state.isTerminal() &&
                    record.item.state != OnlineTransferState.Queued
            }
            if (active) return
            val record = records.values.firstOrNull { item ->
                item.item.direction == OnlineTransferDirection.Outgoing &&
                    item.item.state == OnlineTransferState.Queued
            } ?: return
            record.item = record.item.copy(state = OnlineTransferState.Offered)
            publishLocked()
            record to currentTransport
        }
        val record = next.first
        val transferId = UUID.fromString(record.item.transferId)
        val envelopeId = next.second.session.offerTransfer(
            transferId = transferId,
            kind = record.item.kind,
            name = record.item.name,
            size = record.item.size,
            mediaType = record.item.mediaType,
            createdAt = record.item.createdAt,
        )
        if (envelopeId == null) {
            failTransfer(transferId, "network_interrupted", "Could not offer the transfer.")
        } else {
            synchronized(lock) { records[transferId]?.offerEnvelopeId = envelopeId }
        }
    }

    private suspend fun upload(record: TransferRecord) {
        val transferId = UUID.fromString(record.item.transferId)
        val sourceUri = record.sourceUri
            ?: return failTransfer(transferId, "source_missing", "Selected content is no longer available.")
        val currentTransport = synchronized(lock) {
            val active = transport ?: return
            val current = records[transferId] ?: return
            current.item = current.item.copy(state = OnlineTransferState.Transferring)
            publishLocked()
            active
        }
        try {
            withContext(Dispatchers.IO) {
                val body = ContentResolverRequestBody(
                    resolver = resolver,
                    uri = sourceUri,
                    mediaType = record.item.mediaType.toMediaTypeOrNull(),
                    declaredSize = record.item.size,
                    onProgress = { bytes -> updateProgress(transferId, bytes) },
                )
                val request = Request.Builder()
                    .url(currentTransport.connection.endpoint.transferContentUrl(transferId))
                    .put(body)
                    .header("Content-Type", record.item.mediaType)
                    .header("X-Bob-Transfer-Id", transferId.toString())
                    .build()
                val call = currentTransport.contentClient.newCall(request)
                synchronized(lock) { records[transferId]?.call = call }
                call.execute().use { response ->
                    if (response.code != 202) {
                        throw response.toTransferIoException(
                            "Computer rejected the upload (HTTP ${response.code}).",
                        )
                    }
                    validateUploadAcceptedResponse(
                        response,
                        transferId,
                        body.bytesWritten,
                    )
                }
                val bytes = body.bytesWritten
                val sha256 = body.sha256
                if (record.item.size != null && bytes != record.item.size) {
                    throw TransferIoException("size_mismatch", "Selected content size changed while reading.")
                }
                synchronized(lock) {
                    val current = records[transferId] ?: return@withContext
                    current.call = null
                    current.localBytes = bytes
                    current.localSha256 = sha256
                    current.item = current.item.copy(
                        state = OnlineTransferState.Verifying,
                        bytesTransferred = bytes,
                    )
                    publishLocked()
                }
                if (currentTransport.session.sendTransferDigest(transferId, sha256, bytes) == null) {
                    throw TransferIoException("network_interrupted", "Could not send transfer digest.")
                }
            }
        } catch (error: Exception) {
            failTransfer(
                transferId,
                error.toProtocolCode(),
                safeFailureMessage(error, "Upload failed."),
            )
        }
    }

    private suspend fun download(record: TransferRecord) {
        val transferId = UUID.fromString(record.item.transferId)
        val pendingUri = record.pendingUri ?: return
        val currentTransport = synchronized(lock) {
            val active = transport ?: return
            val current = records[transferId] ?: return
            current.item = current.item.copy(state = OnlineTransferState.Transferring)
            publishLocked()
            active
        }
        try {
            withContext(Dispatchers.IO) {
                val request = Request.Builder()
                    .url(currentTransport.connection.endpoint.transferContentUrl(transferId))
                    .get()
                    .header("X-Bob-Transfer-Id", transferId.toString())
                    .build()
                val call = currentTransport.contentClient.newCall(request)
                synchronized(lock) { records[transferId]?.call = call }
                call.execute().use { response ->
                    if (response.code != 200) {
                        throw response.toTransferIoException(
                            "Computer could not provide the content (HTTP ${response.code}).",
                        )
                    }
                    val headerId = response.header("X-Bob-Transfer-Id")
                    if (headerId != transferId.toString()) {
                        throw TransferIoException("invalid_metadata", "Content response transfer ID did not match.")
                    }
                    val digest = MessageDigest.getInstance("SHA-256")
                    var bytes = 0L
                    val input = response.body.byteStream()
                    val output = resolver.openOutputStream(pendingUri, "w")
                        ?: throw IOException("MediaStore output stream is unavailable.")
                    input.use { source ->
                        output.use { destination ->
                            val buffer = ByteArray(BUFFER_BYTES)
                            var lastProgressAt = 0L
                            while (true) {
                                val count = source.read(buffer)
                                if (count == -1) break
                                destination.write(buffer, 0, count)
                                digest.update(buffer, 0, count)
                                bytes += count
                                val now = System.nanoTime()
                                if (now - lastProgressAt >= PROGRESS_INTERVAL_NANOS) {
                                    updateProgress(transferId, bytes)
                                    currentTransport.session.sendTransferProgress(
                                        transferId,
                                        bytes,
                                        record.item.size,
                                    )
                                    lastProgressAt = now
                                }
                            }
                            destination.flush()
                        }
                    }
                    if (record.item.size != null && bytes != record.item.size) {
                        throw TransferIoException("size_mismatch", "Downloaded byte count did not match the offer.")
                    }
                    val sha256 = digest.digest().base64Url()
                    synchronized(lock) {
                        val current = records[transferId] ?: return@withContext
                        current.call = null
                        current.localBytes = bytes
                        current.localSha256 = sha256
                        current.item = current.item.copy(
                            state = OnlineTransferState.Verifying,
                            bytesTransferred = bytes,
                        )
                        publishLocked()
                    }
                }
            }
            maybePublishIncoming(transferId)
        } catch (error: Exception) {
            failTransfer(
                transferId,
                error.toProtocolCode(),
                safeFailureMessage(error, "Download failed."),
            )
        }
    }

    private fun maybePublishIncoming(transferId: UUID) {
        val ready = synchronized(lock) {
            val record = records[transferId] ?: return
            val digest = record.remoteDigest ?: return
            val localBytes = record.localBytes ?: return
            val localSha256 = record.localSha256 ?: return
            if (record.item.state != OnlineTransferState.Verifying || record.publishStarted) return
            record.publishStarted = true
            Verification(record, digest, localBytes, localSha256, transport)
        }
        val expectedSize = ready.record.item.size
        val valid = ready.digest.algorithm == "sha-256" &&
            ready.digest.bytes == ready.localBytes &&
            (expectedSize == null || ready.digest.bytes == expectedSize) &&
            constantTimeEquals(ready.digest.value, ready.localSha256)
        if (!valid) {
            val failureCode = when {
                ready.digest.algorithm != "sha-256" -> "invalid_metadata"
                ready.digest.bytes != ready.localBytes ||
                    (expectedSize != null && ready.digest.bytes != expectedSize) -> "size_mismatch"
                else -> "checksum_mismatch"
            }
            failTransfer(
                transferId,
                failureCode,
                "Transfer integrity verification failed.",
            )
            return
        }

        scope.launch(Dispatchers.IO) {
            val pendingUri = ready.record.pendingUri ?: return@launch
            try {
                val updated = resolver.update(
                    pendingUri,
                    ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) },
                    null,
                    null,
                )
                if (updated != 1) throw IOException("MediaStore did not publish the pending item.")
                val completed = synchronized(lock) {
                    val record = records[transferId] ?: return@synchronized null
                    record.pendingUri = null
                    record.item = record.item.copy(
                        state = OnlineTransferState.Completed,
                        bytesTransferred = ready.localBytes,
                        localContentUri = pendingUri,
                        errorMessage = null,
                    )
                    record.terminalAckPending = true
                    publishLocked()
                    record.item
                } ?: return@launch
                ready.transport?.session?.sendTransferCompleted(
                    transferId = transferId,
                    bytes = ready.localBytes,
                    sha256 = ready.localSha256,
                    storedName = completed.storedName ?: completed.name,
                )
            } catch (error: Exception) {
                failTransfer(
                    transferId,
                    error.toProtocolCode(),
                    safeFailureMessage(error, "Could not publish received content."),
                )
            }
        }
    }

    private fun failTransfer(
        transferId: UUID,
        code: String,
        message: String,
    ) {
        val pending: Uri?
        val session: BobWebSocketSession?
        synchronized(lock) {
            val record = records[transferId] ?: return
            if (record.item.state.isTerminal()) return
            record.call?.cancel()
            record.call = null
            pending = record.pendingUri
            record.pendingUri = null
            record.item = record.item.copy(
                state = OnlineTransferState.Failed,
                errorMessage = message,
            )
            record.terminalAckPending = true
            session = transport?.session
            publishLocked()
        }
        pending?.let { uri -> scope.launch(Dispatchers.IO) { runCatching { resolver.delete(uri, null, null) } } }
        session?.sendTransferFailed(transferId, code, message, retryable = true)
        drainOutgoing()
    }

    private fun updateProgress(transferId: UUID, bytes: Long) {
        synchronized(lock) {
            val record = records[transferId] ?: return
            if (record.item.state.isTerminal()) return
            record.item = record.item.copy(bytesTransferred = bytes)
            publishLocked()
        }
    }

    private fun readSourceMetadata(uri: Uri, kind: BobTransferKind): SourceMetadata {
        var name: String? = null
        var size: Long? = null
        resolver.query(
            uri,
            arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE),
            null,
            null,
            null,
        )?.use { cursor ->
            if (cursor.moveToFirst()) {
                val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (nameIndex >= 0 && !cursor.isNull(nameIndex)) name = cursor.getString(nameIndex)
                val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) {
                    size = cursor.getLong(sizeIndex).takeIf { it >= 0L }
                }
            }
        }
        val safeName = name
            ?.substringAfterLast('/')
            ?.substringAfterLast('\\')
            ?.takeIf(String::isNotBlank)
            ?: if (kind == BobTransferKind.Image) "image" else "unnamed"
        val mediaType = resolver.getType(uri)
            ?.takeIf(String::isNotBlank)
            ?: DEFAULT_MEDIA_TYPE
        resolver.openAssetFileDescriptor(uri, "r")?.use { descriptor ->
            if (size == null && descriptor.length >= 0L) size = descriptor.length
        }
        return SourceMetadata(safeName, size, mediaType)
    }

    private fun createPendingMedia(offer: BobTransferOffer): PendingMedia {
        val collection = when (offer.kind) {
            BobTransferKind.Image -> MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            BobTransferKind.File -> MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
        }
        val relativePath = when (offer.kind) {
            BobTransferKind.Image -> "${Environment.DIRECTORY_PICTURES}/BOB/"
            BobTransferKind.File -> "${Environment.DIRECTORY_DOWNLOADS}/BOB/"
        }
        val safeName = sanitizeBobFileName(offer.name)
        val availableName = chooseAvailableName(collection, relativePath, safeName)
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, availableName)
            put(MediaStore.MediaColumns.MIME_TYPE, offer.mediaType)
            put(MediaStore.MediaColumns.RELATIVE_PATH, relativePath)
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri = resolver.insert(collection, values)
            ?: throw IOException("MediaStore refused to create a pending item.")
        val actualName = resolver.query(
            uri,
            arrayOf(MediaStore.MediaColumns.DISPLAY_NAME),
            null,
            null,
            null,
        )?.use { cursor ->
            if (cursor.moveToFirst()) cursor.getString(0) else null
        }?.takeIf(String::isNotBlank) ?: availableName
        return PendingMedia(uri, actualName)
    }

    private fun chooseAvailableName(collection: Uri, relativePath: String, requested: String): String {
        repeat(MAX_NAME_ATTEMPTS) { suffix ->
            val candidate = if (suffix == 0) requested else requested.withCollisionSuffix(suffix)
            val exists = runCatching {
                resolver.query(
                    collection,
                    arrayOf(MediaStore.MediaColumns._ID),
                    "${MediaStore.MediaColumns.RELATIVE_PATH}=? AND ${MediaStore.MediaColumns.DISPLAY_NAME}=?",
                    arrayOf(relativePath, candidate),
                    null,
                )?.use { it.moveToFirst() } ?: false
            }.getOrDefault(false)
            if (!exists) return candidate
        }
        return "${UUID.randomUUID()}-${requested}".truncateUtf8(MAX_NAME_BYTES)
    }

    private fun publishLocked() {
        mutableItems.value = records.values
            .map(TransferRecord::item)
            .sortedBy(OnlineTransferItem::createdAt)
    }

    private data class ActiveTransport(
        val connection: BobHttpsConnection,
        val session: BobWebSocketSession,
        val contentClient: OkHttpClient,
    )

    private data class TransferRecord(
        var item: OnlineTransferItem,
        val sourceUri: Uri? = null,
        var pendingUri: Uri? = null,
        var offerEnvelopeId: UUID? = null,
        var localBytes: Long? = null,
        var localSha256: String? = null,
        var remoteDigest: BobTransferDigest? = null,
        var terminalAckPending: Boolean = false,
        var publishStarted: Boolean = false,
        var call: Call? = null,
    )

    private data class SourceMetadata(val name: String, val size: Long?, val mediaType: String)
    private data class PendingMedia(val uri: Uri, val storedName: String)
    private data class Verification(
        val record: TransferRecord,
        val digest: BobTransferDigest,
        val localBytes: Long,
        val localSha256: String,
        val transport: ActiveTransport?,
    )

    private companion object {
        const val DEFAULT_MEDIA_TYPE = "application/octet-stream"
        const val BUFFER_BYTES = 64 * 1024
        const val MAX_NAME_BYTES = 200
        const val MAX_NAME_ATTEMPTS = 1_000
        const val PROGRESS_INTERVAL_NANOS = 250_000_000L
    }
}

private fun validateUploadAcceptedResponse(
    response: Response,
    expectedTransferId: UUID,
    expectedBytes: Long,
) {
    val responseBytes = readBoundedResponseBody(response)
    val json = try {
        JSONObject(String(responseBytes, StandardCharsets.UTF_8))
    } catch (error: Exception) {
        throw TransferIoException("invalid_metadata", "Computer returned an invalid upload response.")
    }
    val responseTransferId = runCatching {
        UUID.fromString(json.getString("transferId"))
    }.getOrNull()
    if (responseTransferId != expectedTransferId ||
        json.optLong("bytesReceived", -1L) != expectedBytes ||
        json.optString("state") != "verifying"
    ) {
        throw TransferIoException(
            "invalid_metadata",
            "Computer returned conflicting upload metadata.",
        )
    }
}

private fun Response.toTransferIoException(fallbackMessage: String): TransferIoException {
    val fallbackCode = when (code) {
        400 -> "invalid_metadata"
        404 -> "transfer_not_found"
        409 -> "invalid_state"
        422 -> "size_mismatch"
        507 -> "insufficient_storage"
        in 500..599 -> "internal_error"
        else -> "network_interrupted"
    }
    val protocolError = runCatching {
        JSONObject(String(readBoundedResponseBody(this), StandardCharsets.UTF_8))
            .getJSONObject("error")
    }.getOrNull()
    val protocolCode = protocolError
        ?.optString("code")
        ?.takeIf(String::isNotBlank)
        ?: fallbackCode
    val protocolMessage = protocolError
        ?.optString("message")
        ?.takeIf(String::isNotBlank)
        ?.truncateUtf8(4 * 1024)
        ?: fallbackMessage
    return TransferIoException(protocolCode, protocolMessage)
}

private fun readBoundedResponseBody(response: Response): ByteArray {
    val input = response.body.byteStream()
    val output = ByteArrayOutputStream()
    val buffer = ByteArray(8 * 1024)
    input.use { source ->
        while (true) {
            val count = source.read(buffer)
            if (count == -1) break
            if (output.size() + count > 64 * 1024) {
                throw TransferIoException(
                    "invalid_metadata",
                    "Computer returned an oversized HTTP response.",
                )
            }
            output.write(buffer, 0, count)
        }
    }
    return output.toByteArray()
}

private class ContentResolverRequestBody(
    private val resolver: ContentResolver,
    private val uri: Uri,
    private val mediaType: MediaType?,
    private val declaredSize: Long?,
    private val onProgress: (Long) -> Unit,
) : RequestBody() {
    @Volatile
    var bytesWritten: Long = 0L
        private set

    @Volatile
    var sha256: String = ""
        private set

    override fun contentType(): MediaType? = mediaType

    override fun contentLength(): Long = declaredSize ?: -1L

    override fun writeTo(sink: BufferedSink) {
        val digest = MessageDigest.getInstance("SHA-256")
        val input = resolver.openInputStream(uri)
            ?: throw FileNotFoundException("Selected content stream is unavailable.")
        var lastProgressAt = 0L
        input.use { source ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val count = source.read(buffer)
                if (count == -1) break
                sink.write(buffer, 0, count)
                digest.update(buffer, 0, count)
                bytesWritten += count
                val now = System.nanoTime()
                if (now - lastProgressAt >= 250_000_000L) {
                    onProgress(bytesWritten)
                    lastProgressAt = now
                }
            }
        }
        onProgress(bytesWritten)
        sha256 = digest.digest().base64Url()
        if (declaredSize != null && declaredSize != bytesWritten) {
            throw TransferIoException("size_mismatch", "Selected content size changed while reading.")
        }
    }
}

private class TransferIoException(
    val code: String,
    message: String,
) : IOException(message)

private fun OnlineTransferState.isTerminal(): Boolean =
    this == OnlineTransferState.Completed || this == OnlineTransferState.Failed

private fun Exception.toProtocolCode(): String = when (this) {
    is TransferIoException -> code
    is FileNotFoundException -> "source_missing"
    is SecurityException -> "permission_denied"
    is IOException -> "network_interrupted"
    else -> "io_error"
}

private fun safeFailureMessage(error: Throwable, fallback: String): String =
    error.message?.takeIf(String::isNotBlank)?.truncateUtf8(4 * 1024) ?: fallback

private fun ByteArray.base64Url(): String = Base64.getUrlEncoder().withoutPadding().encodeToString(this)

private fun constantTimeEquals(first: String, second: String): Boolean = MessageDigest.isEqual(
    first.toByteArray(StandardCharsets.US_ASCII),
    second.toByteArray(StandardCharsets.US_ASCII),
)

internal fun sanitizeBobFileName(raw: String): String {
    val normalized = Normalizer.normalize(raw, Normalizer.Form.NFC)
    val cleaned = buildString(normalized.length) {
        normalized.codePoints().forEach { codePoint ->
            val invalid = codePoint == 0 || codePoint < 0x20 ||
                codePoint == 0x7F || codePoint.toChar() in "/\\<>:\"|?*"
            if (!invalid) appendCodePoint(codePoint)
        }
    }.trimEnd(' ', '.')
    var result = cleaned.ifBlank { "unnamed" }
    val base = result.substringBefore('.').trimEnd(' ', '.').uppercase()
    if (base in WINDOWS_RESERVED_NAMES ||
        base.matches(Regex("COM[1-9]")) ||
        base.matches(Regex("LPT[1-9]"))
    ) {
        result = "_$result"
    }
    return result.truncateUtf8(200)
}

private fun String.withCollisionSuffix(suffix: Int): String {
    val dot = lastIndexOf('.').takeIf { it > 0 }
    val base = if (dot == null) this else substring(0, dot)
    val extension = if (dot == null) "" else substring(dot)
    val marker = " ($suffix)"
    val maxBaseBytes = (200 - marker.toByteArray(Charsets.UTF_8).size -
        extension.toByteArray(Charsets.UTF_8).size).coerceAtLeast(1)
    return (base.truncateUtf8(maxBaseBytes) + marker + extension).truncateUtf8(200)
}

private fun String.truncateUtf8(maxBytes: Int): String {
    if (toByteArray(Charsets.UTF_8).size <= maxBytes) return this
    val output = StringBuilder()
    var used = 0
    codePoints().forEach { codePoint ->
        val text = String(Character.toChars(codePoint))
        val bytes = text.toByteArray(Charsets.UTF_8).size
        if (used + bytes <= maxBytes) {
            output.append(text)
            used += bytes
        }
    }
    return output.toString().ifBlank { "unnamed" }
}

private val WINDOWS_RESERVED_NAMES = setOf("CON", "PRN", "AUX", "NUL")

private fun BobTransferDigest.sameDigestPayload(other: BobTransferDigest): Boolean =
    transferId == other.transferId &&
        algorithm == other.algorithm &&
        value == other.value &&
        bytes == other.bytes
