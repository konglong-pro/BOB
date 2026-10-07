package com.bob.android.transfer

import android.content.ContentResolver
import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import com.bob.android.network.BobHttpsConnection
import com.bob.android.network.BobSessionPhase
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

import com.bob.android.network.BobRemoteError
import com.bob.android.network.BobTransferCancel
import com.bob.android.network.BobTransferCanceled
import com.bob.android.persistence.AtomicTransferJournal
import com.bob.android.persistence.TransferJournalException
import java.time.Duration
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.isActive
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray

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
    Canceled,
}

internal const val SENDER_CANCEL_WAIT_MILLIS = 60_000L

/** The cancel request sender's wait, independent of content direction and byte progress. */
internal class SenderCancelWindow(private val startedAtNanos: Long = System.nanoTime()) {
    @Volatile private var closed = false

    fun permitsBody(nowNanos: Long = System.nanoTime()): Boolean =
        !closed && nowNanos - startedAtNanos < TimeUnit.MILLISECONDS.toNanos(SENDER_CANCEL_WAIT_MILLIS)

    fun remainingMillis(nowNanos: Long = System.nanoTime()): Long {
        if (closed) return 0L
        val remaining = (TimeUnit.MILLISECONDS.toNanos(SENDER_CANCEL_WAIT_MILLIS) -
            (nowNanos - startedAtNanos)).coerceAtLeast(0L)
        return (remaining + 999_999L) / 1_000_000L
    }

    fun close() { closed = true }
}

internal fun canStreamTransferBody(
    state: OnlineTransferState,
    cancelPending: Boolean,
    cancelWindow: SenderCancelWindow?,
    nowNanos: Long = System.nanoTime(),
): Boolean = state == OnlineTransferState.Transferring ||
    (state == OnlineTransferState.Canceled && cancelPending && cancelWindow?.permitsBody(nowNanos) == true)

internal fun canPublishIncomingBody(state: OnlineTransferState, cancelPending: Boolean): Boolean =
    state == OnlineTransferState.Verifying && !cancelPending

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
    val recordedAt: Instant = createdAt,
    val serverId: String? = null,
    val retryable: Boolean = true,
    val isSendingCancellation: Boolean = false,
)

/** Each direction has one stream; only coordination metadata is journaled. */
class OnlineTransferCoordinator(
    context: Context,
    private val scope: CoroutineScope,
    private val onStorageFailure: (TransferJournalException) -> Unit = {},
) {
    private val resolver = context.applicationContext.contentResolver
    private val packageName = context.applicationContext.packageName
    private val journal = AtomicTransferJournal(context)
    private val lock = Any()
    private val incomingOfferMutex = Mutex()
    private val records = linkedMapOf<UUID, TransferRecord>()
    private val mutableItems = MutableStateFlow<List<OnlineTransferItem>>(emptyList())
    val items: StateFlow<List<OnlineTransferItem>> = mutableItems.asStateFlow()
    private var transport: ActiveTransport? = null
    private val initialized = scope.async(Dispatchers.IO) { restoreJournal() }

    fun attach(connection: BobHttpsConnection, session: BobWebSocketSession, snapshot: List<String> = emptyList()): Deferred<Unit> {
        val active = ActiveTransport(
            connection,
            session,
            connection.client.newBuilder()
                .readTimeout(BODY_IDLE_TIMEOUT_SECONDS, TimeUnit.SECONDS)
                .writeTimeout(BODY_IDLE_TIMEOUT_SECONDS, TimeUnit.SECONDS)
                .callTimeout(0, TimeUnit.MILLISECONDS)
                .build(),
        )
        synchronized(lock) { transport = active }
        return scope.async(Dispatchers.IO) {
            try {
                initialized.await()
                if (synchronized(lock) { transport !== active }) throw CancellationException("Session was replaced.")
                reconcileSnapshot(active, snapshot)
                active.ready.complete(Unit)
            } catch (error: Exception) {
                active.ready.completeExceptionally(error)
                if (error is TransferJournalException) reportStorageFailure(active, error)
                throw error
            }
            val replay = synchronized(lock) {
                records.values.filter { it.serverId == active.serverId }.toList()
            }
            replay.forEach { record ->
                val id = UUID.fromString(record.item.transferId)
                if (record.item.state == OnlineTransferState.Verifying) {
                    armTimeout(record)
                    if (record.item.direction == OnlineTransferDirection.Outgoing) {
                        replayDigest(record)
                    } else {
                        maybePublishIncoming(id)
                    }
                } else if (record.cancelPending) {
                    sendCancelRequest(record)
                } else if (record.terminalAckPending) {
                    replayTerminal(record)
                }
            }
            drainOutgoing()
        }
    }

    fun detach(session: BobWebSocketSession? = null) {
        val interrupted = synchronized(lock) {
            val active = transport ?: return
            if (session != null && active.session !== session) return
            transport = null
            if (!active.ready.isCompleted) active.ready.cancel(CancellationException("Session disconnected before recovery finished."))
            records.values.filter { record ->
                record.owner === active && (record.awaitingCancelResponse() ||
                    (!record.item.state.isTerminal() &&
                        record.item.state != OnlineTransferState.Verifying &&
                        record.item.state != OnlineTransferState.Queued))
            }.map { record ->
                record.call?.cancel()
                val handles = takeBodyHandlesLocked(record)
                if (!record.awaitingCancelResponse()) {
                    record.item = record.item.copy(state = OnlineTransferState.Failed,
                        errorMessage = "Connection interrupted. Retry this file from the beginning.")
                    record.failureCode = "network_interrupted"
                    record.failureIsLocal = true
                    record.retryable = true
                    record.terminalAt = Instant.now()
                    record.terminalAckPending = true
                }
                record.owner = null
                record.timeout?.cancel()
                record to handles
            }.also { publishLocked(persist = false) }
        }
        launchMutation {
            interrupted.forEach { (record, handles) ->
                // These handles were captured before another session could attach;
                // delayed provider cleanup cannot close a newly replayed cancel.
                closeBodyHandles(handles)
                deletePending(record)
            }
            synchronized(lock) { publishLocked() }
        }
    }

    suspend fun queueOutgoing(uris: List<Uri>, kind: BobTransferKind, serverId: UUID, retryOf: UUID? = null) {
        initialized.await()
        require(serverId != UUID(0L, 0L)) { "An authenticated server identity is required." }
        for (uri in uris.distinct()) {
            currentCoroutineContext().ensureActive()
            val id = UUID.randomUUID()
            val createdAt = Instant.now()
            val source = try {
                withContext(Dispatchers.IO) { readSourceMetadata(uri, kind) }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                withContext(Dispatchers.IO) {
                    synchronized(lock) {
                        records[id] = TransferRecord(
                            serverId, OnlineTransferItem(
                                id.toString(), OnlineTransferDirection.Outgoing, kind,
                                "Unavailable content", null, DEFAULT_MEDIA_TYPE,
                                OnlineTransferState.Failed, 0L, null, null,
                                safeFailureMessage(error, "Could not read selected content."),
                                createdAt, createdAt, serverId.toString(),
                            ), retryOf = retryOf, failureCode = error.toProtocolCode(),
                            retryable = true, terminalAt = createdAt, failureIsLocal = true,
                        )
                        publishLocked()
                    }
                }
                continue
            }
            withContext(Dispatchers.IO) {
                synchronized(lock) {
                    records[id] = TransferRecord(
                        serverId, OnlineTransferItem(
                            id.toString(), OnlineTransferDirection.Outgoing, kind, source.name,
                            source.size, source.mediaType, OnlineTransferState.Queued, 0L,
                            null, uri, null, createdAt, createdAt, serverId.toString(),
                        ), sourceUri = uri, retryOf = retryOf,
                    )
                    publishLocked()
                }
            }
        }
        drainOutgoing()
    }

    fun retry(transferId: UUID) {
        launchMutation {
            val previous = synchronized(lock) { records[transferId] } ?: return@launchMutation
            val uri = previous.sourceUri ?: return@launchMutation
            if (previous.item.direction != OnlineTransferDirection.Outgoing ||
                previous.item.state !in setOf(OnlineTransferState.Failed, OnlineTransferState.Canceled) ||
                !previous.retryable || previous.cancelPending
            ) return@launchMutation
            val currentServer = synchronized(lock) { transport?.serverId }
            if (currentServer != previous.serverId) return@launchMutation
            queueOutgoing(listOf(uri), previous.item.kind, previous.serverId, transferId)
        }
    }

    fun cancel(transferId: UUID) {
        launchMutation { cancelRecord(transferId, remote = false) }
    }

    fun abortActive(message: String, preserveVerifying: Boolean = false) {
        launchMutation {
            val active = synchronized(lock) {
                records.values.filter { !it.item.state.isTerminal() &&
                    it.item.state != OnlineTransferState.Queued &&
                    (!preserveVerifying || it.item.state != OnlineTransferState.Verifying) }.toList()
            }
            active.forEach { record ->
                failTransfer(UUID.fromString(record.item.transferId), "network_interrupted", message)
            }
        }
    }

    fun onOffer(offer: BobTransferOffer) {
        val owner = synchronized(lock) { transport } ?: return
        launchMutation {
            owner.ready.await()
            acceptIncoming(offer, owner)
        }
    }

    fun onAccepted(accepted: BobTransferAccepted) {
        launchSessionMutation {
            val record = synchronized(lock) {
                val current = records[accepted.transferId] ?: return@synchronized null
                if (current.item.direction != OnlineTransferDirection.Outgoing ||
                    current.item.state != OnlineTransferState.Offered ||
                    current.owner !== transport
                ) return@synchronized null
                if (accepted.replyTo != null && accepted.replyTo != current.offerEnvelopeId) {
                    throw TransferIoException("invalid_metadata", "Acceptance did not match the offer.")
                }
                current.item = current.item.copy(state = OnlineTransferState.Accepted,
                    storedName = accepted.plannedName)
                publishLocked()
                current
            } ?: return@launchSessionMutation
            upload(record)
        }
    }

    fun onDigest(digest: BobTransferDigest) {
        launchSessionMutation {
            val conflict = synchronized(lock) {
                val record = records[digest.transferId] ?: return@synchronized false
                if (record.item.direction != OnlineTransferDirection.Incoming) return@synchronized false
                val previous = record.remoteDigest
                if (previous != null && !previous.sameDigestPayload(digest)) {
                    true
                } else {
                    record.remoteDigest = digest
                    publishLocked()
                    false
                }
            }
            if (conflict) {
                failTransfer(digest.transferId, "invalid_metadata", "Conflicting transfer digest.", false)
            } else {
                val record = synchronized(lock) { records[digest.transferId] }
                if (record?.item?.state?.isTerminal() == true) replayTerminal(record)
                else maybePublishIncoming(digest.transferId)
            }
        }
    }

    fun onCompleted(completed: BobTransferCompleted) {
        launchSessionMutation {
            val record = synchronized(lock) { records[completed.transferId] } ?: return@launchSessionMutation
            if (record.item.direction != OnlineTransferDirection.Outgoing) return@launchSessionMutation
            synchronized(record.finalizationLock) {
                synchronized(lock) {
                    if (!canReceiveCompleted(record.item.state)) {
                        throw TransferIoException("invalid_state", "Completed conflicts with the saved transfer state.")
                    }
                    val valid = record.localBytes == completed.bytes &&
                        record.localSha256 == completed.sha256 &&
                        (record.item.state != OnlineTransferState.Completed ||
                            (record.item.storedName == completed.storedName &&
                                record.terminalAt == completed.completedAt))
                    if (!valid) {
                        currentSession(record)?.close(4400, "invalid_metadata")
                        return@launchSessionMutation
                    }
                    record.item = record.item.copy(state = OnlineTransferState.Completed,
                        bytesTransferred = completed.bytes, storedName = completed.storedName, errorMessage = null)
                    record.terminalAt = completed.completedAt
                    record.terminalAckPending = false
                    record.cancelPending = false
                    record.failureCode = null
                    record.failureIsLocal = false
                    record.remoteFailure = null
                    record.timeout?.cancel()
                    publishLocked()
                }
                closeStreams(record)
            }
            currentSession(record)?.acknowledgeTransferTerminal(completed.transferId, BobTransferTerminalState.Completed)
            drainOutgoing()
        }
    }

    fun onFailed(failed: BobTransferFailed) {
        launchSessionMutation {
            val record = synchronized(lock) { records[failed.transferId] } ?: return@launchSessionMutation
            synchronized(record.finalizationLock) {
                if (record.item.state == OnlineTransferState.Failed) {
                    // Both peers can independently fail the same interrupted stream. Their
                    // diagnostics differ; each keeps its own original payload and remembers
                    // the first payload received from the other peer for later deduplication.
                    record.remoteFailure = rememberPeerFailure(record.failureIsLocal, record.remoteFailure,
                        record.failurePayload(), failed)
                } else if (record.item.state.isTerminal() && !record.cancelPending) {
                    throw TransferIoException("invalid_state", "Failure conflicts with the saved terminal state.")
                } else {
                    record.remoteFailure = failed
                    setFailed(record, failed.code, failed.message.ifBlank { failed.code }, failed.retryable,
                        notifyPeer = false)
                }
            }
            synchronized(lock) { publishLocked() }
            currentSession(record)?.acknowledgeTransferTerminal(failed.transferId, BobTransferTerminalState.Failed)
            drainOutgoing()
        }
    }

    fun onCancel(cancel: BobTransferCancel) {
        launchSessionMutation { cancelRecord(cancel.transferId, remote = true) }
    }

    fun onCanceled(canceled: BobTransferCanceled) {
        launchSessionMutation {
            val record = synchronized(lock) { records[canceled.transferId] } ?: return@launchSessionMutation
            synchronized(record.finalizationLock) {
                if (record.item.state == OnlineTransferState.Completed &&
                    record.item.direction == OnlineTransferDirection.Incoming) {
                    replayTerminal(record)
                    return@launchSessionMutation
                } else if (record.item.state in setOf(OnlineTransferState.Completed, OnlineTransferState.Failed)) {
                    throw TransferIoException("invalid_state", "Cancellation conflicts with the saved terminal state.")
                } else if (record.cancelPending || !record.item.state.isTerminal()) {
                    terminateCanceled(record, canceled.canceledAt, notifyPeer = false)
                } else if (record.item.state == OnlineTransferState.Canceled &&
                    record.terminalAt != canceled.canceledAt
                ) {
                    currentSession(record)?.close(4400, "invalid_metadata")
                    return@launchSessionMutation
                }
            }
            synchronized(lock) { publishLocked() }
            currentSession(record)?.acknowledgeTransferTerminal(
                canceled.transferId, BobTransferTerminalState.Canceled,
            )
            drainOutgoing()
        }
    }

    fun onTerminalAck(acknowledgement: BobTransferTerminalAck) {
        launchSessionMutation {
            synchronized(lock) {
                val record = records[acknowledgement.transferId] ?: return@synchronized
                if (record.item.state.isTerminal() &&
                    record.item.state.terminalState() == acknowledgement.state
                ) {
                    record.terminalAckPending = false
                    publishLocked()
                }
            }
        }
    }

    fun onProgress(progress: BobTransferProgress) {
        synchronized(lock) {
            val record = records[progress.transferId] ?: return
            if (record.item.direction != OnlineTransferDirection.Outgoing || record.item.state.isTerminal()) return
            if (progress.total != record.item.size ||
                (record.item.size != null && progress.bytes > record.item.size!!)
            ) {
                currentSession(record)?.close(4400, "invalid_metadata")
                return
            }
            record.item = record.item.copy(bytesTransferred = maxOf(record.item.bytesTransferred, progress.bytes))
            publishLocked(persist = false)
        }
    }

    fun onRemoteError(error: BobRemoteError) {
        launchSessionMutation {
            val id = synchronized(lock) {
                error.transferId?.takeIf(records::containsKey) ?: records.entries.firstOrNull {
                    error.replyTo != null && it.value.offerEnvelopeId == error.replyTo
                }?.key
            } ?: return@launchSessionMutation
            failTransfer(id, error.code, error.message.ifBlank { error.code }, error.retryable)
        }
    }


    private suspend fun acceptIncoming(offer: BobTransferOffer, active: ActiveTransport) {
        incomingOfferMutex.withLock {
            if (synchronized(lock) { transport !== active }) return
            val duplicate = synchronized(lock) { records[offer.transferId] }
            if (duplicate != null) {
                val same = duplicate.serverId == active.serverId &&
                    duplicate.item.direction == OnlineTransferDirection.Incoming &&
                    duplicate.item.kind == offer.kind && duplicate.item.name == offer.name &&
                    duplicate.item.size == offer.size && duplicate.item.mediaType == offer.mediaType &&
                    duplicate.item.createdAt == offer.createdAt && duplicate.retryOf == offer.retryOf
                if (!same) {
                    active.session.sendTransferFailed(offer.transferId, "invalid_metadata",
                        "The transfer ID conflicts with an existing transfer.", false)
                } else if (duplicate.item.state.isTerminal()) {
                    replayTerminal(duplicate)
                } else {
                    duplicate.item.storedName?.let { name ->
                        active.session.acceptTransfer(offer.transferId, name, offer.envelopeId)
                    }
                }
                return
            }
            // Canonical and the next offer arrive in WSS order, but their IO jobs
            // can run in the opposite order. Await only this session's pending
            // request, without taking the previous record's finalization lock.
            while (true) {
                if (active.session.state.phase != BobSessionPhase.Connected) return
                val remaining = synchronized(lock) {
                    if (transport !== active) return
                    records.values.filter { it.owner === active && it.serverId == active.serverId &&
                        it.item.direction == OnlineTransferDirection.Incoming && it.cancelPending }
                        .minOfOrNull { it.senderCancelWindow?.remainingMillis() ?: 0L }
                } ?: break
                if (remaining == 0L) return
                delay(minOf(25L, remaining))
            }
            val record = synchronized(lock) {
                if (records.values.any { it.serverId == active.serverId &&
                        it.item.direction == OnlineTransferDirection.Incoming &&
                        (!it.item.state.isTerminal() || (it.cancelPending && it.owner === active)) }
                ) {
                    active.session.sendTransferFailed(offer.transferId, "direction_busy",
                        "Another computer-to-phone transfer is active.")
                    return
                }
                val localTime = Instant.now()
                TransferRecord(active.serverId, OnlineTransferItem(
                    offer.transferId.toString(), OnlineTransferDirection.Incoming, offer.kind,
                    offer.name, offer.size, offer.mediaType, OnlineTransferState.Offered, 0L,
                    null, null, null, offer.createdAt, localTime, active.serverId.toString(),
                ), retryOf = offer.retryOf, offerEnvelopeId = offer.envelopeId, owner = active).also {
                    records[offer.transferId] = it
                    publishLocked()
                }
            }
            armTimeout(record)
            val pending = try {
                createPendingMedia(offer)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                failTransfer(offer.transferId, error.toProtocolCode(),
                    safeFailureMessage(error, "Could not create the Android destination."))
                return
            }
            val accepted = synchronized(lock) {
                if (transport !== active || record.item.state.isTerminal()) {
                    false
                } else {
                    record.pendingUri = pending.uri
                    record.item = record.item.copy(state = OnlineTransferState.Accepted,
                        storedName = pending.storedName)
                    publishLocked()
                    true
                }
            }
            if (!accepted) {
                deleteBobPending(pending.uri, offer.kind)
                return
            }
            if (active.session.acceptTransfer(offer.transferId, pending.storedName, offer.envelopeId) == null) {
                failTransfer(offer.transferId, "network_interrupted", "Could not accept the transfer.")
                return
            }
            launchMutation { download(record) }
        }
    }

    private fun drainOutgoing() {
        launchMutation {
            val next = synchronized(lock) {
                val active = transport ?: return@synchronized null
                if (!active.ready.isCompleted || active.ready.isCancelled ||
                    active.session.state.phase != BobSessionPhase.Connected) return@synchronized null
                if (records.values.any { it.serverId == active.serverId &&
                        it.item.direction == OnlineTransferDirection.Outgoing &&
                        (it.cancelPending || (!it.item.state.isTerminal() &&
                            it.item.state != OnlineTransferState.Queued)) }
                ) return@synchronized null
                val record = records.values.firstOrNull {
                    it.serverId == active.serverId && it.item.direction == OnlineTransferDirection.Outgoing &&
                        it.item.state == OnlineTransferState.Queued
                } ?: return@synchronized null
                record.owner = active
                record.item = record.item.copy(state = OnlineTransferState.Offered)
                publishLocked()
                record to active
            } ?: return@launchMutation
            val record = next.first
            val id = UUID.fromString(record.item.transferId)
            val envelopeId = synchronized(lock) {
                if (record.owner !== transport || record.item.state != OnlineTransferState.Offered) {
                    return@launchMutation
                }
                next.second.session.offerTransfer(
                    id, record.item.kind, record.item.name, record.item.size, record.item.mediaType,
                    record.item.createdAt, record.retryOf,
                ).also { record.offerEnvelopeId = it }
            }
            if (envelopeId == null) {
                failTransfer(id, "network_interrupted", "Could not offer the transfer.")
            } else {
                synchronized(lock) {
                    if (!record.item.state.isTerminal()) record.offerEnvelopeId = envelopeId
                }
                armTimeout(record)
            }
        }
    }

    private suspend fun upload(record: TransferRecord) {
        val id = UUID.fromString(record.item.transferId)
        val sourceUri = record.sourceUri ?: return failTransfer(id, "source_missing",
            "Selected content is no longer available.")
        val active = beginBody(record) ?: return
        try {
            val body = ContentResolverRequestBody(
                resolver, sourceUri, record.item.mediaType.toMediaTypeOrNull(), record.item.size,
                onProgress = { bytes -> updateProgress(record, active, bytes) },
                ensureActive = { ensureBodyCurrent(record, active) },
                onStreamOpened = { stream -> registerInput(record, active, stream) },
            )
            val request = Request.Builder()
                .url(active.connection.endpoint.transferContentUrl(id))
                .put(body).header("Content-Type", record.item.mediaType)
                .header("X-Bob-Transfer-Id", id.toString()).build()
            val call = active.contentClient.newCall(request)
            registerCall(record, active, call)
            call.execute().use { response ->
                if (response.code != 202) throw response.toTransferIoException(
                    "Computer rejected the upload (HTTP " + response.code + ").")
                validateUploadAcceptedResponse(response, id, body.bytesWritten)
            }
            if (finishBody(record, active, body.bytesWritten, body.sha256)) {
                replayDigest(record)
            }
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            failIfBodyCurrent(record, active, error, "Upload failed.")
        } finally {
            synchronized(lock) {
                if (record.owner === active) {
                    record.call = null
                    record.input = null
                    record.output = null
                }
            }
        }
    }

    private suspend fun download(record: TransferRecord) {
        val id = UUID.fromString(record.item.transferId)
        val pendingUri = synchronized(lock) { record.pendingUri } ?: return
        val active = beginBody(record) ?: return
        try {
            val request = Request.Builder().url(active.connection.endpoint.transferContentUrl(id))
                .get().header("X-Bob-Transfer-Id", id.toString()).build()
            val call = active.contentClient.newCall(request)
            registerCall(record, active, call)
            val downloaded = call.execute().use { response ->
                if (response.code != 200) throw response.toTransferIoException(
                    "Computer could not provide the content (HTTP " + response.code + ").")
                if (response.header("X-Bob-Transfer-Id") != id.toString()) {
                    throw TransferIoException("invalid_metadata", "Content response transfer ID did not match.")
                }
                val responseLength = response.body.contentLength()
                if (record.item.size != null && responseLength >= 0L && responseLength != record.item.size) {
                    throw TransferIoException("size_mismatch", "Content length did not match the accepted offer.")
                }
                val input = response.body.byteStream()
                registerInput(record, active, input)
                input.use { source ->
                    val output = resolver.openOutputStream(pendingUri, "w")
                        ?: throw IOException("MediaStore output stream is unavailable.")
                    registerOutput(record, active, output)
                    output.use { destination ->
                        val result = copyTransferStream(
                            source, record.item.size,
                            writeChunk = { buffer, count -> destination.write(buffer, 0, count) },
                            ensureActive = { ensureBodyCurrent(record, active) },
                            onProgress = { bytes ->
                                updateProgress(record, active, bytes)
                                active.session.sendTransferProgress(id, bytes, record.item.size)
                            },
                        )
                        destination.flush()
                        result
                    }
                }
            }
            if (finishBody(record, active, downloaded.bytes, downloaded.sha256)) {
                maybePublishIncoming(id)
            }
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            failIfBodyCurrent(record, active, error, "Download failed.")
        } finally {
            synchronized(lock) {
                if (record.owner === active) {
                    record.call = null
                    record.input = null
                    record.output = null
                }
            }
        }
    }

    private fun beginBody(record: TransferRecord): ActiveTransport? = synchronized(lock) {
        val active = transport ?: return@synchronized null
        if (record.owner !== active || record.item.state != OnlineTransferState.Accepted) return@synchronized null
        record.lastActivityNanos = System.nanoTime()
        record.item = record.item.copy(state = OnlineTransferState.Transferring)
        publishLocked()
        armTimeout(record)
        active
    }

    private fun finishBody(record: TransferRecord, active: ActiveTransport, bytes: Long, sha256: String): Boolean {
        val current = synchronized(lock) {
            if (record.owner !== active || transport !== active ||
                !record.canStreamBody()
            ) return@synchronized false
            record.call = null
            record.localBytes = bytes
            record.localSha256 = sha256
            // Either body can reach EOF while its local cancel is awaiting control.
            // Keep the final digest, but never turn that pending request into verifying,
            // send a new digest, or publish the received MediaStore content.
            if (record.awaitingCancelResponse()) {
                record.item = record.item.copy(bytesTransferred = bytes)
                publishLocked()
                return@synchronized false
            }
            record.verifyingSince = Instant.now()
            record.item = record.item.copy(state = OnlineTransferState.Verifying, bytesTransferred = bytes)
            publishLocked()
            true
        }
        if (current) armTimeout(record)
        return current
    }

    private fun ensureBodyCurrent(record: TransferRecord, active: ActiveTransport) {
        synchronized(lock) {
            if (transport !== active || record.owner !== active ||
                !record.canStreamBody()
            ) throw TransferIoException("network_interrupted", "Transfer was interrupted.")
        }
    }

    private fun registerCall(record: TransferRecord, active: ActiveTransport, call: Call) {
        synchronized(lock) {
            try {
                ensureBodyCurrent(record, active)
                record.call = call
            } catch (error: Exception) {
                call.cancel()
                throw error
            }
        }
    }

    private fun registerInput(record: TransferRecord, active: ActiveTransport, input: java.io.InputStream) {
        synchronized(lock) {
            try {
                ensureBodyCurrent(record, active)
                record.input = input
            } catch (error: Exception) {
                input.close()
                throw error
            }
        }
    }

    private fun registerOutput(record: TransferRecord, active: ActiveTransport, output: java.io.OutputStream) {
        synchronized(lock) {
            try {
                ensureBodyCurrent(record, active)
                record.output = output
            } catch (error: Exception) {
                output.close()
                throw error
            }
        }
    }

    private fun updateProgress(record: TransferRecord, active: ActiveTransport, bytes: Long) {
        synchronized(lock) {
            if (transport !== active || record.owner !== active ||
                !record.canStreamBody()
            ) return
            record.lastActivityNanos = System.nanoTime()
            record.item = record.item.copy(bytesTransferred = maxOf(record.item.bytesTransferred, bytes))
            publishLocked(persist = false)
        }
    }

    private fun failIfBodyCurrent(record: TransferRecord, active: ActiveTransport, error: Exception, fallback: String) {
        val current = synchronized(lock) {
            record.owner === active && !record.item.state.isTerminal()
        }
        if (current) failTransfer(UUID.fromString(record.item.transferId),
            error.toProtocolCode(), safeFailureMessage(error, fallback))
    }


    private fun maybePublishIncoming(transferId: UUID) {
        launchMutation {
            val record = synchronized(lock) { records[transferId] } ?: return@launchMutation
            synchronized(record.finalizationLock) {
                val ready = synchronized(lock) {
                    if (!canPublishIncomingBody(record.item.state, record.cancelPending) || record.publishStarted) {
                        return@synchronized null
                    }
                    val digest = record.remoteDigest ?: return@synchronized null
                    val bytes = record.localBytes ?: return@synchronized null
                    val sha256 = record.localSha256 ?: return@synchronized null
                    val uri = record.pendingUri ?: return@synchronized null
                    record.publishStarted = true
                    Verification(digest, bytes, sha256, uri)
                } ?: return@launchMutation
                val expectedSize = record.item.size
                val invalidCode = when {
                    ready.digest.algorithm != "sha-256" -> "invalid_metadata"
                    ready.digest.bytes != ready.bytes ||
                        (expectedSize != null && ready.bytes != expectedSize) -> "size_mismatch"
                    !constantTimeEquals(ready.digest.value, ready.sha256) -> "checksum_mismatch"
                    else -> null
                }
                if (invalidCode != null) {
                    setFailed(record, invalidCode, "Transfer integrity verification failed.", true, true)
                    return@launchMutation
                }
                try {
                    val media = queryBobMediaRow(ready.uri, record.item.kind)
                        ?: throw IOException("The received MediaStore item is unavailable.")
                    if (!media.isPending) {
                        verifyPublishedContent(record, ready.uri)
                    }
                    // Persist the publication intent and original terminal timestamp first.
                    // A crash after IS_PENDING becomes 0 can then recover the same completed
                    // payload without exposing a second file or deleting the published one.
                    synchronized(lock) {
                        record.publicationAt = record.publicationAt ?: Instant.now()
                        publishLocked()
                    }
                    val updated = if (media.isPending) resolver.update(ready.uri,
                        ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) },
                        pendingRowSelection(), pendingRowArgs(record.item.kind)) else 1
                    if (updated != 1) throw IOException("MediaStore did not publish the pending item.")
                    val published = try {
                        queryBobMediaRow(ready.uri, record.item.kind)?.takeIf { !it.isPending }
                            ?: throw IOException("Could not read the published item.")
                    } catch (error: Exception) {
                        // The provider update crossed the publication boundary. Leave
                        // the committed verifying intent for recovery rather than fail it.
                        throw TransferJournalException("Received content was published, but its final metadata could not be saved. Reopen BOB to recover it.", error)
                    }
                    synchronized(lock) {
                        record.pendingUri = null
                        record.item = record.item.copy(state = OnlineTransferState.Completed,
                            bytesTransferred = ready.bytes, storedName = published.storedName,
                            localContentUri = ready.uri, errorMessage = null)
                        record.terminalAt = requireNotNull(record.publicationAt)
                        record.terminalAckPending = true
                        record.timeout?.cancel()
                        publishLocked()
                    }
                    replayTerminal(record)
                } catch (error: TransferJournalException) {
                    throw error
                } catch (error: Exception) {
                    // Publication is the cancel/complete boundary. A successful provider update
                    // must never be turned into failure because the subsequent journal write failed.
                    if (record.item.state == OnlineTransferState.Completed) throw error
                    setFailed(record, error.toProtocolCode(),
                        safeFailureMessage(error, "Could not publish received content."), true, true)
                } finally {
                    synchronized(lock) { record.publishStarted = false }
                }
            }
        }
    }

    private fun failTransfer(id: UUID, code: String, message: String, retryable: Boolean = code.isRetryable()) {
        val record = synchronized(lock) { records[id] } ?: return
        synchronized(record.finalizationLock) {
            if (record.item.state.isTerminal()) return
            setFailed(record, code, message, retryable, notifyPeer = true)
        }
    }

    private fun setFailed(record: TransferRecord, code: String, message: String,
                          retryable: Boolean, notifyPeer: Boolean) {
        synchronized(lock) {
            if (record.item.state == OnlineTransferState.Completed) return
            record.call?.cancel()
            record.item = record.item.copy(state = OnlineTransferState.Failed, errorMessage = message,
                retryable = retryable)
            record.failureCode = code
            record.failureIsLocal = notifyPeer
            if (notifyPeer) record.remoteFailure = null
            record.retryable = retryable
            record.terminalAt = record.terminalAt ?: Instant.now()
            record.terminalAckPending = notifyPeer
            record.cancelPending = false
            record.publicationAt = null
            record.timeout?.cancel()
        }
        closeStreams(record)
        deletePending(record)
        synchronized(lock) { publishLocked() }
        if (notifyPeer) replayTerminal(record)
        drainOutgoing()
    }

    private fun cancelRecord(id: UUID, remote: Boolean) {
        val record = synchronized(lock) { records[id] } ?: return
        synchronized(record.finalizationLock) {
            if (record.item.state == OnlineTransferState.Completed) {
                if (record.item.direction == OnlineTransferDirection.Incoming) replayTerminal(record)
                else currentSession(record)?.acknowledgeTransferTerminal(id, BobTransferTerminalState.Completed)
                return
            }
            if (record.item.state.isTerminal()) {
                if (remote && record.item.state == OnlineTransferState.Canceled && record.cancelPending) {
                    // Crossing cancel requests must settle on one immutable canceledAt.
                    // The content receiver owns cleanup and chooses the canonical result.
                    if (record.item.direction == OnlineTransferDirection.Incoming) {
                        terminateCanceled(record, requireNotNull(record.terminalAt), notifyPeer = true)
                    } else {
                        sendCancelRequest(record)
                    }
                    return
                }
                if (remote) replayTerminal(record)
                return
            }
            val wasOffered = record.item.state != OnlineTransferState.Queued
            terminateCanceled(record, Instant.now(), notifyPeer = remote && wasOffered,
                waitForPeer = !remote && wasOffered)
            if (!remote && wasOffered) sendCancelRequest(record)
        }
        drainOutgoing()
    }

    private fun terminateCanceled(record: TransferRecord, time: Instant, notifyPeer: Boolean,
                                  waitForPeer: Boolean = false) {
        val keepBody = waitForPeer
        synchronized(lock) {
            // WSS and HTTPS are independent TCP streams. Closing a locally-canceled
            // body here can reach the peer before transfer.cancel and fix an unintended
            // failed result, regardless of who sends the content. Wait for canonical
            // control (or bounded lost-session cleanup) before releasing either body.
            if (keepBody) record.senderCancelWindow = SenderCancelWindow()
            else record.call?.cancel()
            record.item = record.item.copy(state = OnlineTransferState.Canceled, errorMessage = null)
            record.failureCode = "canceled"
            record.failureIsLocal = false
            record.remoteFailure = null
            record.terminalAt = time
            record.terminalAckPending = notifyPeer
            record.cancelPending = waitForPeer
            record.publicationAt = null
            record.timeout?.cancel()
        }
        if (!keepBody) {
            closeStreams(record)
            deletePending(record)
        }
        synchronized(lock) { publishLocked() }
        if (notifyPeer) replayTerminal(record)
    }

    private fun closeStreams(record: TransferRecord) {
        val handles = synchronized(lock) {
            takeBodyHandlesLocked(record).also { publishLocked(persist = false) }
        }
        closeBodyHandles(handles)
    }

    private fun takeBodyHandlesLocked(record: TransferRecord): BodyHandles {
        record.senderCancelWindow?.close()
        record.senderCancelWindow = null
        return BodyHandles(record.call, record.input, record.output).also {
            record.call = null
            record.input = null
            record.output = null
        }
    }

    private fun closeBodyHandles(handles: BodyHandles) {
        handles.call?.cancel()
        runCatching { handles.input?.close() }
        runCatching { handles.output?.close() }
    }

    private fun deletePending(record: TransferRecord) {
        val uri = synchronized(lock) { record.pendingUri } ?: return
        val removed = deleteBobPending(uri, record.item.kind)
        if (removed) synchronized(lock) { if (record.pendingUri == uri) record.pendingUri = null }
    }

    private fun deleteBobPending(uri: Uri, kind: BobTransferKind): Boolean = runCatching {
        val media = queryBobMediaRow(uri, kind) ?: return@runCatching true
        if (!media.isPending) return@runCatching false
        // Query checks ownership, collection and path. Repeat all mutable predicates in
        // the delete itself so an already-published item can never be removed by a race.
        resolver.delete(uri, pendingRowSelection(), pendingRowArgs(kind)) == 1
    }.getOrDefault(false)

    private fun queryBobMediaRow(uri: Uri, kind: BobTransferKind): OwnedMediaRow? {
        if (!isBobMediaUri(uri.toString(), kind)) {
            throw IOException("The saved destination is outside BOB MediaStore collections.")
        }
        val cursor = resolver.query(uri, arrayOf(MediaStore.MediaColumns.IS_PENDING,
            MediaStore.MediaColumns.RELATIVE_PATH, MediaStore.MediaColumns.OWNER_PACKAGE_NAME,
            MediaStore.MediaColumns.DISPLAY_NAME), null, null, null)
            ?: throw IOException("Could not read the saved MediaStore destination.")
        return cursor.use {
            if (!cursor.moveToFirst()) return@use null
            if (!isOwnedBobMediaRow(kind, cursor.getString(1), cursor.getString(2), packageName)) {
                throw IOException("The saved MediaStore destination does not belong to BOB.")
            }
            val pending = cursor.getInt(0)
            if (pending !in 0..1) throw IOException("The saved MediaStore destination has invalid publication state.")
            val storedName = cursor.getString(3)?.takeIf(String::isNotBlank)
                ?: throw IOException("The saved MediaStore destination has no file name.")
            OwnedMediaRow(pending == 1, storedName)
        }
    }

    private fun pendingRowSelection(): String =
        "${MediaStore.MediaColumns.IS_PENDING}=1 AND ${MediaStore.MediaColumns.RELATIVE_PATH}=? AND ${MediaStore.MediaColumns.OWNER_PACKAGE_NAME}=?"

    private fun pendingRowArgs(kind: BobTransferKind): Array<String> = arrayOf(
        if (kind == BobTransferKind.Image) "${Environment.DIRECTORY_PICTURES}/BOB/" else "${Environment.DIRECTORY_DOWNLOADS}/BOB/",
        packageName,
    )

    private fun verifyPublishedContent(record: TransferRecord, uri: Uri) {
        val digest = record.remoteDigest ?: throw IOException("Published content has no saved peer digest.")
        val bytes = record.localBytes ?: throw IOException("Published content has no saved byte count.")
        val sha256 = record.localSha256 ?: throw IOException("Published content has no saved SHA-256.")
        if (digest.algorithm != "sha-256" || digest.bytes != bytes || !constantTimeEquals(digest.value, sha256)) {
            throw IOException("Published content does not have matching saved verification metadata.")
        }
        val result = resolver.openInputStream(uri)?.use { source ->
            copyTransferStream(source, bytes, writeChunk = { _, _ -> })
        } ?: throw IOException("Published content could not be read for recovery.")
        if (result.bytes != bytes || !constantTimeEquals(result.sha256, sha256)) {
            throw IOException("Published content does not match its saved SHA-256.")
        }
    }

    private fun currentSession(record: TransferRecord): BobWebSocketSession? = synchronized(lock) {
        transport?.takeIf { it.serverId == record.serverId }?.session
    }

    private fun replayDigest(record: TransferRecord) {
        val bytes = record.localBytes ?: return
        val sha256 = record.localSha256 ?: return
        val session = currentSession(record) ?: return
        if (session.sendTransferDigest(UUID.fromString(record.item.transferId), sha256, bytes) == null) {
            session.close(1000, "digest_replay_pending")
        }
    }

    private fun replayTerminal(record: TransferRecord) {
        val session = currentSession(record) ?: return
        if (record.cancelPending) {
            sendCancelRequest(record)
            return
        }
        synchronized(lock) { publishLocked() }
        val id = UUID.fromString(record.item.transferId)
        when (record.item.state) {
            OnlineTransferState.Completed -> session.sendTransferCompleted(id,
                record.localBytes ?: record.item.bytesTransferred, record.localSha256.orEmpty(),
                record.item.storedName ?: record.item.name, record.terminalAt ?: Instant.now())
            OnlineTransferState.Failed -> session.sendTransferFailed(id, record.failureCode ?: "io_error",
                record.item.errorMessage ?: "Transfer failed.", record.retryable)
            OnlineTransferState.Canceled -> session.sendTransferCanceled(id, record.terminalAt ?: Instant.now())
            else -> Unit
        }
    }

    private fun sendCancelRequest(record: TransferRecord) {
        synchronized(record.finalizationLock) {
            val active = synchronized(lock) {
                transport?.takeIf { it.serverId == record.serverId && record.cancelPending }
            } ?: return
            val window = if (record.awaitingCancelResponse()) synchronized(lock) {
                // Repeated/crossing cancel requests never extend the same session's wait.
                // A disconnected request remains in the journal and gets a new bounded
                // control window when it is replayed on a subsequent connection.
                if (record.owner !== active || record.senderCancelWindow == null) {
                    record.senderCancelWindow?.close()
                    record.senderCancelWindow = SenderCancelWindow()
                    record.owner = active
                }
                requireNotNull(record.senderCancelWindow).also { current ->
                    if (record.timeout == null || !record.timeout!!.isActive) {
                        record.timeout = scope.launch(Dispatchers.IO) {
                            while (isActive) {
                                val remaining = synchronized(lock) {
                                    if (transport !== active || record.owner !== active ||
                                        !record.awaitingCancelResponse() || record.senderCancelWindow !== current
                                    ) return@launch
                                    val now = System.nanoTime()
                                    val cancelRemaining = current.remainingMillis(now)
                                    val bodyRemaining = if (record.call != null || record.input != null || record.output != null) {
                                        val idleNanos = (TimeUnit.SECONDS.toNanos(BODY_IDLE_TIMEOUT_SECONDS) -
                                            (now - record.lastActivityNanos)).coerceAtLeast(0L)
                                        (idleNanos + 999_999L) / 1_000_000L
                                    } else cancelRemaining
                                    minOf(cancelRemaining, bodyRemaining)
                                }
                                if (remaining == 0L) {
                                    expireSenderCancel(record, active, current)
                                    return@launch
                                }
                                delay(remaining)
                            }
                        }
                    }
                    publishLocked(persist = false)
                }
            } else null
            if (active.session.cancelTransfer(UUID.fromString(record.item.transferId)) == null && window != null) {
                expireSenderCancel(record, active, window)
            }
        }
    }

    private fun expireSenderCancel(record: TransferRecord, active: ActiveTransport, window: SenderCancelWindow) {
        val handles = synchronized(record.finalizationLock) {
            val captured = synchronized(lock) {
                if (transport !== active || record.owner !== active ||
                    !record.awaitingCancelResponse() || record.senderCancelWindow !== window
                ) return@synchronized null
                val captured = takeBodyHandlesLocked(record)
                record.owner = null
                record.timeout?.cancel()
                publishLocked(persist = false)
                captured
            } ?: return
            // Do not manufacture a different terminal result after requesting cancel.
            // Close control before aborting content, then replay the same cancel once
            // the next snapshot establishes the receiver's saved canonical result.
            active.session.close(1000, "cancel_confirmation_pending")
            captured
        }
        // Provider close can take time. It owns only captured old handles and must
        // not hold the record's finalization lock across the next session's replay.
        closeBodyHandles(handles)
        deletePending(record)
        // Deadline jobs run outside launchMutation; keep any storage failure on
        // the coordinator's diagnosed failure path instead of an uncaught IO job.
        launchMutation { synchronized(lock) { publishLocked() } }
    }

    private fun TransferRecord.awaitingCancelResponse(): Boolean = cancelPending

    private fun TransferRecord.canStreamBody(): Boolean = canStreamTransferBody(
        item.state, cancelPending, senderCancelWindow,
    )

    private fun armTimeout(record: TransferRecord) {
        synchronized(lock) {
            // An offer/HTTP completion worker may reach this after local cancel
            // armed its own response deadline. Never replace that deadline with
            // an already-terminal body watchdog.
            if (record.item.state.isTerminal()) return
            record.timeout?.cancel()
            record.lastActivityNanos = System.nanoTime()
            record.timeout = scope.launch(Dispatchers.IO) {
                if (record.item.state == OnlineTransferState.Verifying) {
                    val elapsed = Duration.between(record.verifyingSince ?: Instant.now(), Instant.now())
                        .toMillis().coerceAtLeast(0L)
                    delay((DIGEST_WAIT_MILLIS - elapsed).coerceAtLeast(0L))
                    if (record.item.state == OnlineTransferState.Verifying) {
                        failTransfer(UUID.fromString(record.item.transferId), "digest_timeout",
                            "Transfer digest confirmation did not arrive within 24 hours.")
                    }
                } else {
                    while (isActive && !record.item.state.isTerminal() &&
                        record.item.state != OnlineTransferState.Verifying
                    ) {
                        delay(5_000L)
                        val expired = synchronized(lock) {
                            System.nanoTime() - record.lastActivityNanos >=
                                TimeUnit.SECONDS.toNanos(BODY_IDLE_TIMEOUT_SECONDS)
                        }
                        if (expired) {
                            failTransfer(UUID.fromString(record.item.transferId), "network_interrupted",
                                "No transfer progress for 60 seconds. Retry this file.")
                            return@launch
                        }
                    }
                }
            }
        }
    }

    private fun launchMutation(block: suspend () -> Unit) {
        scope.launch(Dispatchers.IO) {
            try {
                initialized.await()
                block()
            } catch (error: CancellationException) {
                throw error
            } catch (error: TransferJournalException) {
                synchronized(lock) { transport }?.let { reportStorageFailure(it, error) }
            } catch (error: TransferIoException) {
                synchronized(lock) { transport?.session }?.close(4400, error.code)
            } catch (_: Exception) {
                synchronized(lock) { transport?.session }?.close(4400, "invalid_metadata")
            }
        }
    }

    private fun launchSessionMutation(block: suspend () -> Unit) {
        val owner = synchronized(lock) { transport } ?: return
        launchMutation {
            owner.ready.await()
            if (synchronized(lock) { transport !== owner }) return@launchMutation
            block()
        }
    }

    private fun reportStorageFailure(active: ActiveTransport, error: TransferJournalException) {
        if (synchronized(lock) { transport !== active }) return
        onStorageFailure(error)
        active.session.close(4500, "persistence_failed")
    }

    private fun TransferRecord.failurePayload(): BobTransferFailed = BobTransferFailed(
        UUID.randomUUID(), UUID.fromString(item.transferId), requireNotNull(failureCode),
        requireNotNull(item.errorMessage), retryable,
    )

    private fun reconcileSnapshot(active: ActiveTransport, snapshot: List<String>) {
        try {
            reconcileSnapshotEntries(active, snapshot)
        } catch (error: TransferJournalException) {
            throw error
        } catch (error: TransferIoException) {
            throw error
        } catch (_: Exception) {
            throw TransferIoException("invalid_metadata", "Computer sent invalid snapshot metadata.")
        }
    }

    private fun reconcileSnapshotEntries(active: ActiveTransport, snapshot: List<String>) {
        for (encoded in snapshot) {
            if (synchronized(lock) { transport !== active }) return
            val json = JSONObject(encoded)
            val id = parseJournalUuid(json.requiredString("transferId"))
            val direction = when (json.requiredString("direction")) {
                "client_to_server" -> OnlineTransferDirection.Outgoing
                "server_to_client" -> OnlineTransferDirection.Incoming
                else -> throw TransferIoException("invalid_metadata", "Unknown snapshot transfer direction.")
            }
            val kind = BobTransferKind.fromWire(json.requiredString("kind"))
            val size = json.nullableLong("size")
            val bytes = json.requiredLong("bytesTransferred")
            if ((size != null && (size < 0L || bytes > size)) || bytes < 0L) {
                throw TransferIoException("invalid_metadata", "Invalid snapshot transfer byte count.")
            }
            val name = json.requiredString("name")
            val mediaType = json.requiredString("mediaType")
            val createdAt = Instant.parse(json.requiredString("createdAt"))
            val retryOf = json.nullableString("retryOf")?.let(::parseJournalUuid)
            val existing = synchronized(lock) { records[id] }
            if (existing != null) {
                if (existing.serverId != active.serverId || existing.item.direction != direction ||
                    existing.item.kind != kind || existing.item.name != name || existing.item.size != size ||
                    existing.item.mediaType != mediaType || existing.item.createdAt != createdAt ||
                    existing.retryOf != retryOf
                ) {
                    throw TransferIoException("invalid_metadata", "Snapshot metadata conflicts with saved history.")
                }
                continue
            }
            val state = when (json.requiredString("state")) {
                "completed" -> OnlineTransferState.Completed
                "failed" -> OnlineTransferState.Failed
                "canceled" -> OnlineTransferState.Canceled
                "verifying", "accepted", "transferring" -> OnlineTransferState.Failed
                "queued", "offered" -> continue // The server reoffers these after the final page.
                else -> throw TransferIoException("invalid_metadata", "Unknown snapshot transfer state.")
            }
            val lostBody = json.requiredString("state") in setOf("verifying", "accepted", "transferring")
            val error = json.optJSONObject("error")
            val failureCode = when {
                lostBody -> "network_interrupted"
                state == OnlineTransferState.Failed -> error?.requiredString("code")
                    ?.takeIf(String::isNotBlank) ?: throw TransferIoException("invalid_metadata", "Snapshot failure payload is missing.")
                state == OnlineTransferState.Canceled -> "canceled"
                else -> null
            }
            val message = if (lostBody) "Local content is unavailable. Restart this transfer from its source."
                else if (state == OnlineTransferState.Failed) error?.requiredString("message")?.ifBlank { failureCode.orEmpty() }
                else null
            val record = TransferRecord(active.serverId, OnlineTransferItem(
                id.toString(), direction, kind, name, size, mediaType, state, bytes,
                json.nullableString("storedName"), null, message, createdAt, Instant.now(), active.serverId.toString(),
            ), retryOf = retryOf, localBytes = if (state == OnlineTransferState.Completed) bytes else null,
                localSha256 = if (state == OnlineTransferState.Completed) json.nullableString("sha256") else null,
                terminalAt = json.nullableString("terminalAt")?.let(Instant::parse) ?: Instant.now(),
                failureCode = failureCode,
                retryable = if (lostBody) true else error?.optBoolean("retryable", true) ?: true,
                terminalAckPending = lostBody,
                failureIsLocal = lostBody,
            )
            if (state == OnlineTransferState.Failed && !lostBody) {
                record.remoteFailure = BobTransferFailed(UUID.randomUUID(), id, requireNotNull(failureCode),
                    requireNotNull(error).requiredString("message"), error.requiredBoolean("retryable"))
            }
            validateTransferJournal(listOf(record.journalFacts()))
            synchronized(lock) { records[id] = record; publishLocked() }
            if (lostBody) replayTerminal(record)
            else active.session.acknowledgeTransferTerminal(id, state.terminalState())
        }
    }

    private fun publishLocked(persist: Boolean = true) {
        if (persist) journal.write(JSONArray().apply {
            records.values.forEach { record -> put(record.toJson()) }
        })
        mutableItems.value = records.values.map { record -> record.item.copy(
            retryable = record.retryable && !record.cancelPending,
            isSendingCancellation = record.awaitingCancelResponse() && record.owner === transport &&
                record.senderCancelWindow?.permitsBody() == true,
        ) }
            .sortedBy { it.recordedAt }
    }

    private fun restoreJournal() {
        val restored = try {
            val loaded = journal.read()
            // Parse and validate every entry first. A malformed later entry must not
            // cause a valid earlier entry's pending content to be deleted.
            List(loaded.length()) { index -> recordFromJson(loaded.getJSONObject(index)) }
                .also { validateTransferJournal(it.map { record -> record.journalFacts() }) }
        } catch (error: TransferJournalException) {
            throw error
        } catch (error: Exception) {
            throw TransferJournalException("Saved file transfers are invalid. The original journal and content were kept.", error)
        }
        // Inspect all destinations without mutating MediaStore or publishing in-memory
        // history. Recovery must fail closed if any saved URI belongs to another app.
        try {
            restored.forEach { record ->
                val uri = record.pendingUri ?: return@forEach
                val media = queryBobMediaRow(uri, record.item.kind)
                if (media?.isPending == false && record.item.state != OnlineTransferState.Verifying) {
                    throw IOException("An unpublished saved transfer points to already-published content.")
                }
                if (record.item.state == OnlineTransferState.Verifying) {
                    if (media == null) {
                        record.item = record.item.copy(state = OnlineTransferState.Failed,
                            errorMessage = "Received content is no longer available. Retry from the computer.")
                        record.failureCode = "io_error"
                        record.failureIsLocal = true
                        record.terminalAt = Instant.now()
                        record.terminalAckPending = true
                        record.publicationAt = null
                    } else if (!media.isPending) {
                        verifyPublishedContent(record, uri)
                        record.publicationAt = record.publicationAt ?: Instant.now()
                        record.terminalAt = record.publicationAt
                        record.pendingUri = null
                        record.item = record.item.copy(state = OnlineTransferState.Completed,
                            bytesTransferred = requireNotNull(record.localBytes), storedName = media.storedName,
                            localContentUri = uri, errorMessage = null)
                        record.terminalAckPending = true
                    }
                }
            }
        } catch (error: Exception) {
            throw TransferJournalException("Could not safely recover saved file transfers. The journal and existing content were kept.", error)
        }
        for (record in restored) {
            val state = record.item.state
            if (!state.isTerminal() && state != OnlineTransferState.Verifying) {
                record.item = record.item.copy(state = OnlineTransferState.Failed,
                    errorMessage = if (record.item.direction == OnlineTransferDirection.Outgoing) {
                        "BOB restarted. Select this content again to restart the transfer."
                    } else {
                        "BOB restarted before the content finished. Retry from the computer."
                    })
                record.failureCode = "network_interrupted"
                record.failureIsLocal = true
                record.terminalAt = Instant.now()
                record.terminalAckPending = state != OnlineTransferState.Queued
                record.retryable = record.item.direction == OnlineTransferDirection.Incoming
            }
        }
        // Commit recovery before cleanup; a failed write leaves both content and the
        // original snapshot intact. Corrupt snapshots never reach this point.
        journal.write(JSONArray().apply { restored.forEach { put(it.toJson()) } })
        synchronized(lock) {
            restored.forEach { records[UUID.fromString(it.item.transferId)] = it }
            publishLocked(persist = false)
        }
        restored.filter { it.item.state in setOf(OnlineTransferState.Failed, OnlineTransferState.Canceled) }
            .forEach(::deletePending)
        synchronized(lock) { publishLocked() }
        val keepPending = records.values.mapNotNull { it.pendingUri }.toSet()
        cleanupOrphanPending(keepPending)
        records.values.filter { it.item.state == OnlineTransferState.Verifying }.forEach(::armTimeout)
    }

    private fun TransferRecord.toJson(): JSONObject = JSONObject()
        .put("serverId", serverId.toString()).put("transferId", item.transferId)
        .put("direction", item.direction.name).put("kind", item.kind.wireValue)
        .put("name", item.name).put("size", item.size ?: JSONObject.NULL)
        .put("mediaType", item.mediaType).put("state", item.state.name)
        .put("bytes", item.bytesTransferred).put("storedName", item.storedName ?: JSONObject.NULL)
        .put("localUri", item.localContentUri?.toString() ?: JSONObject.NULL)
        .put("createdAt", item.createdAt.toString()).put("recordedAt", item.recordedAt.toString())
        .put("error", item.errorMessage ?: JSONObject.NULL)
        .put("sourceUri", sourceUri?.toString() ?: JSONObject.NULL)
        .put("pendingUri", pendingUri?.toString() ?: JSONObject.NULL)
        .put("retryOf", retryOf?.toString() ?: JSONObject.NULL)
        .put("localBytes", localBytes ?: JSONObject.NULL).put("localSha256", localSha256 ?: JSONObject.NULL)
        .put("remoteDigest", remoteDigest?.let { digest -> JSONObject()
            .put("algorithm", digest.algorithm).put("value", digest.value).put("bytes", digest.bytes)
        } ?: JSONObject.NULL)
        .put("verifyingSince", verifyingSince?.toString() ?: JSONObject.NULL)
        .put("terminalAt", terminalAt?.toString() ?: JSONObject.NULL)
        .put("terminalAckPending", terminalAckPending).put("retryable", retryable)
        .put("cancelPending", cancelPending)
        .put("failureCode", failureCode ?: JSONObject.NULL)
        .put("failureIsLocal", failureIsLocal)
        .put("remoteFailure", remoteFailure?.let { failure -> JSONObject()
            .put("code", failure.code).put("message", failure.message).put("retryable", failure.retryable)
        } ?: JSONObject.NULL)
        .put("publicationAt", publicationAt?.toString() ?: JSONObject.NULL)

    private fun recordFromJson(json: JSONObject): TransferRecord {
        val id = parseJournalUuid(json.requiredString("transferId"))
        val serverId = parseJournalUuid(json.requiredString("serverId"))
        val direction = OnlineTransferDirection.valueOf(json.requiredString("direction"))
        val state = OnlineTransferState.valueOf(json.requiredString("state"))
        val terminalAckPending = json.requiredBoolean("terminalAckPending")
        val record = TransferRecord(
            serverId, OnlineTransferItem(
                id.toString(), direction, BobTransferKind.fromWire(json.requiredString("kind")),
                json.requiredString("name"), json.nullableLong("size"), json.requiredString("mediaType"),
                state, json.requiredLong("bytes"),
                json.nullableString("storedName"), json.nullableString("localUri")?.let(Uri::parse),
                json.nullableString("error"), Instant.parse(json.requiredString("createdAt")),
                Instant.parse(json.requiredString("recordedAt")), serverId.toString(),
            ),
            sourceUri = json.nullableString("sourceUri")?.let(Uri::parse),
            pendingUri = json.nullableString("pendingUri")?.let(Uri::parse),
            retryOf = json.nullableString("retryOf")?.let(::parseJournalUuid),
            localBytes = json.nullableLong("localBytes"),
            localSha256 = json.nullableString("localSha256"),
            verifyingSince = json.nullableString("verifyingSince")?.let(Instant::parse),
            terminalAt = json.nullableString("terminalAt")?.let(Instant::parse),
            terminalAckPending = terminalAckPending,
            cancelPending = if (json.has("cancelPending")) json.requiredBoolean("cancelPending") else false,
            retryable = json.requiredBoolean("retryable"), failureCode = json.nullableString("failureCode"),
            failureIsLocal = if (json.has("failureIsLocal")) json.requiredBoolean("failureIsLocal")
                else state == OnlineTransferState.Failed && terminalAckPending,
            publicationAt = json.nullableString("publicationAt")?.let(Instant::parse),
        )
        if (!json.isNull("remoteDigest")) json.getJSONObject("remoteDigest").let { digest ->
            record.remoteDigest = BobTransferDigest(UUID.randomUUID(), id,
                digest.requiredString("algorithm"), digest.requiredString("value"), digest.requiredLong("bytes"))
        }
        if (!json.isNull("remoteFailure")) json.getJSONObject("remoteFailure").let { failure ->
            val code = failure.requiredString("code")
            val message = failure.requiredString("message")
            require(code.isNotBlank()) { "Missing peer failure code." }
            record.remoteFailure = BobTransferFailed(UUID.randomUUID(), id,
                code, message, failure.requiredBoolean("retryable"))
        }
        return record
    }

    private fun TransferRecord.journalFacts(): TransferJournalFacts = TransferJournalFacts(
        UUID.fromString(item.transferId), serverId, item.direction, item.kind, item.name, item.mediaType,
        item.size, item.bytesTransferred, item.state, item.storedName, sourceUri?.toString(),
        pendingUri?.toString(), item.localContentUri?.toString(), retryOf, localBytes, localSha256,
        remoteDigest?.algorithm, remoteDigest?.bytes, remoteDigest?.value, verifyingSince != null,
        terminalAt != null, publicationAt != null, terminalAckPending, cancelPending, item.errorMessage,
        failureCode, failureIsLocal, remoteFailure != null,
    )

    private fun cleanupOrphanPending(keep: Set<Uri>) {
        listOf(BobTransferKind.Image, BobTransferKind.File).forEach { kind ->
            val collection = if (kind == BobTransferKind.Image)
                MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
                else MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
            runCatching {
                resolver.query(collection, arrayOf(MediaStore.MediaColumns._ID),
                    pendingRowSelection(), pendingRowArgs(kind), null)?.use { cursor ->
                    while (cursor.moveToNext()) {
                        val uri = android.content.ContentUris.withAppendedId(collection, cursor.getLong(0))
                        if (uri !in keep) deleteBobPending(uri, kind)
                    }
                }
            }
        }
    }

    private data class ActiveTransport(
        val connection: BobHttpsConnection,
        val session: BobWebSocketSession,
        val contentClient: OkHttpClient,
    ) {
        val serverId: UUID get() = connection.serverInfo.serverId
        val ready = CompletableDeferred<Unit>()
    }

    private data class TransferRecord(
        val serverId: UUID,
        @Volatile var item: OnlineTransferItem,
        val sourceUri: Uri? = null,
        var pendingUri: Uri? = null,
        val retryOf: UUID? = null,
        var offerEnvelopeId: UUID? = null,
        var localBytes: Long? = null,
        var localSha256: String? = null,
        var remoteDigest: BobTransferDigest? = null,
        var verifyingSince: Instant? = null,
        var terminalAt: Instant? = null,
        var terminalAckPending: Boolean = false,
        var cancelPending: Boolean = false,
        var senderCancelWindow: SenderCancelWindow? = null,
        var failureCode: String? = null,
        var failureIsLocal: Boolean = false,
        var remoteFailure: BobTransferFailed? = null,
        var retryable: Boolean = true,
        var publicationAt: Instant? = null,
        var publishStarted: Boolean = false,
        var owner: ActiveTransport? = null,
        var call: Call? = null,
        var input: java.io.InputStream? = null,
        var output: java.io.OutputStream? = null,
        @Volatile var lastActivityNanos: Long = System.nanoTime(),
        var timeout: Job? = null,
        val finalizationLock: Any = Any(),
    )

    private data class SourceMetadata(val name: String, val size: Long?, val mediaType: String)
    private data class BodyHandles(val call: Call?, val input: java.io.InputStream?, val output: java.io.OutputStream?)
    private data class PendingMedia(val uri: Uri, val storedName: String)
    private data class OwnedMediaRow(val isPending: Boolean, val storedName: String)
    private data class Verification(val digest: BobTransferDigest, val bytes: Long, val sha256: String, val uri: Uri)

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
        val safeName = sanitizeBobFileName(name
            ?.substringAfterLast('/')
            ?.substringAfterLast('\\')
            ?.takeIf(String::isNotBlank)
            ?: if (kind == BobTransferKind.Image) "image" else "unnamed")
        val mediaType = resolver.getType(uri)
            ?.toMediaTypeOrNull()
            ?.let { it.type + "/" + it.subtype }
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
        try {
            if (uri.scheme != "content" || uri.authority != collection.authority ||
                uri.path?.startsWith(collection.path.orEmpty() + "/") != true
            ) throw TransferIoException("invalid_metadata", "MediaStore returned an unexpected destination.")
            val actualName = resolver.query(
                uri, arrayOf(MediaStore.MediaColumns.DISPLAY_NAME), null, null, null,
            )?.use { cursor ->
                if (cursor.moveToFirst()) cursor.getString(0) else null
            }?.takeIf(String::isNotBlank) ?: availableName
            return PendingMedia(uri, actualName)
        } catch (error: Exception) {
            deleteBobPending(uri, offer.kind)
            throw error
        }
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
        return sanitizeBobFileName("${UUID.randomUUID()}-${requested}")
    }

    private companion object {
        const val DEFAULT_MEDIA_TYPE = "application/octet-stream"
        const val MAX_NAME_BYTES = 200
        const val MAX_NAME_ATTEMPTS = 1_000
        const val BODY_IDLE_TIMEOUT_SECONDS = 60L
        const val DIGEST_WAIT_MILLIS = TRANSFER_DIGEST_WAIT_MILLIS
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
    private val ensureActive: () -> Unit,
    private val onStreamOpened: (java.io.InputStream) -> Unit,
) : RequestBody() {
    @Volatile var bytesWritten: Long = 0L
        private set
    @Volatile var sha256: String = ""
        private set
    override fun contentType(): MediaType? = mediaType
    override fun contentLength(): Long = declaredSize ?: -1L
    override fun isOneShot(): Boolean = true
    override fun writeTo(sink: BufferedSink) {
        ensureActive()
        val input = resolver.openInputStream(uri)
            ?: throw FileNotFoundException("Selected content stream is unavailable.")
        input.use { source ->
            onStreamOpened(source)
            val result = copyTransferStream(
                source, declaredSize,
                writeChunk = { buffer, count -> sink.write(buffer, 0, count) },
                ensureActive = ensureActive,
                onProgress = onProgress,
            )
            bytesWritten = result.bytes
            sha256 = result.sha256
        }
    }
}

private fun OnlineTransferState.isTerminal(): Boolean =
    this == OnlineTransferState.Completed || this == OnlineTransferState.Failed || this == OnlineTransferState.Canceled

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


private fun BobTransferDigest.sameDigestPayload(other: BobTransferDigest): Boolean =
    transferId == other.transferId &&
        algorithm == other.algorithm &&
        value == other.value &&
        bytes == other.bytes


private fun OnlineTransferState.terminalState(): BobTransferTerminalState = when (this) {
    OnlineTransferState.Completed -> BobTransferTerminalState.Completed
    OnlineTransferState.Canceled -> BobTransferTerminalState.Canceled
    else -> BobTransferTerminalState.Failed
}
private fun String.isRetryable(): Boolean = this !in setOf(
    "unsupported_version", "invalid_message", "message_too_large", "invalid_state",
    "transfer_not_found", "invalid_metadata", "unsupported_kind", "wrong_direction",
)
private fun JSONObject.nullableString(key: String): String? =
    if (isNull(key)) null else requiredString(key)
private fun JSONObject.nullableLong(key: String): Long? =
    if (isNull(key)) null else requiredLong(key)
private fun JSONObject.requiredString(key: String): String =
    get(key) as? String ?: throw IllegalArgumentException("Expected a string for $key.")
private fun JSONObject.requiredLong(key: String): Long {
    val value = get(key)
    require(value is Long || value is Int) { "Expected an int64 for $key." }
    return (value as Number).toLong()
}
private fun JSONObject.requiredBoolean(key: String): Boolean =
    get(key) as? Boolean ?: throw IllegalArgumentException("Expected a boolean for $key.")
private fun parseJournalUuid(value: String): UUID = UUID.fromString(value).also {
    require(it.toString().equals(value, ignoreCase = true)) { "Expected a canonical transfer UUID." }
}
