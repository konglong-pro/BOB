package com.bob.android.application

import android.content.Context
import android.net.Uri
import android.os.Build
import com.bob.android.network.BobEndpoint
import com.bob.android.network.BobHttpsConnection
import com.bob.android.network.BobHttpsConnector
import com.bob.android.network.BobIncomingText
import com.bob.android.network.BobProtocol
import com.bob.android.network.BobRemoteError
import com.bob.android.network.BobSessionCloseCause
import com.bob.android.network.BobSessionClosure
import com.bob.android.network.BobSessionPhase
import com.bob.android.network.BobSessionState
import com.bob.android.network.BobTextAcknowledgement
import com.bob.android.network.BobTransferAccepted
import com.bob.android.network.BobTransferCompleted
import com.bob.android.network.BobTransferDigest
import com.bob.android.network.BobTransferFailed
import com.bob.android.network.BobTransferKind
import com.bob.android.network.BobTransferOffer
import com.bob.android.network.BobTransferProgress
import com.bob.android.network.BobTransferTerminalAck
import com.bob.android.network.BobWebSocketSession
import com.bob.android.network.BobWelcome
import com.bob.android.persistence.AtomicTextMessageStore
import com.bob.android.persistence.PersistedTextMessage
import com.bob.android.persistence.TextDirection
import com.bob.android.persistence.TextMessageConflictException
import com.bob.android.security.BobCertificatePinMismatchException
import com.bob.android.security.BobCertificateValidationException
import com.bob.android.security.BobIdentityMismatchException
import com.bob.android.security.BobHttpsException
import com.bob.android.security.BobSecurityException
import com.bob.android.security.BobServerInfoException
import com.bob.android.security.BobTrustConflictException
import com.bob.android.security.BobTrustStore
import com.bob.android.transfer.OnlineTransferCoordinator
import java.io.Closeable
import java.io.IOException
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.time.Instant
import java.util.UUID
import javax.net.ssl.SSLHandshakeException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

enum class BobConnectionPhase {
    Idle,
    Securing,
    OpeningSession,
    Synchronizing,
    Connected,
    Retrying,
    Blocked,
    Failed,
}

data class BobConnectionState(
    val phase: BobConnectionPhase = BobConnectionPhase.Idle,
    val endpoint: BobEndpoint? = null,
    val serverId: UUID? = null,
    val serverName: String? = null,
    val maxTextBytes: Int? = null,
    val message: String? = null,
    val retryAttempt: Int = 0,
    val trustResetAllowed: Boolean = false,
) {
    val isConnected: Boolean
        get() = phase == BobConnectionPhase.Connected

    val canResetTrust: Boolean
        get() = phase == BobConnectionPhase.Blocked && serverId != null && trustResetAllowed
}

/** Owns exactly one pinned HTTPS/WSS connection and its durable text outbox. */
class BobConnectionCoordinator(
    context: Context,
    private val scope: CoroutineScope,
    val textStore: AtomicTextMessageStore = AtomicTextMessageStore(context),
    private val httpsConnector: BobHttpsConnector = BobHttpsConnector(context),
    private val trustStore: BobTrustStore = BobTrustStore(context),
) : Closeable {
    private val applicationContext = context.applicationContext
    private val callbackExecutor = applicationContext.mainExecutor
    private val outboxMutex = Mutex()
    private val mutableState = MutableStateFlow(BobConnectionState())
    val state: StateFlow<BobConnectionState> = mutableState.asStateFlow()
    private val transferCoordinator = OnlineTransferCoordinator(applicationContext, scope)
    val transferItems = transferCoordinator.items

    private var generation = 0L
    private var connectionJob: Job? = null
    private var requestedEndpoints: List<BobEndpoint> = emptyList()
    private var transportGeneration: Long? = null
    private var httpsConnection: BobHttpsConnection? = null
    private var webSocketSession: BobWebSocketSession? = null
    private var trustResetInProgress = false
    private var closed = false

    fun connect(endpoint: BobEndpoint) {
        connect(listOf(endpoint))
    }

    fun connect(endpoints: List<BobEndpoint>) {
        if (closed || trustResetInProgress) return
        require(endpoints.isNotEmpty()) { "At least one BOB endpoint is required." }
        val distinctEndpoints = endpoints.distinctBy { endpoint ->
            "${endpoint.host.lowercase()}:${endpoint.port}:${endpoint.expectedServerId}"
        }
        val current = mutableState.value
        if (requestedEndpoints == distinctEndpoints && current.phase in ACTIVE_PHASES) return

        generation += 1
        val connectionGeneration = generation
        requestedEndpoints = distinctEndpoints
        connectionJob?.cancel()
        closeTransport()
        connectionJob = scope.launch {
            runConnectionLoop(connectionGeneration, distinctEndpoints)
        }
    }

    fun refreshConnectedEndpoints(endpoints: List<BobEndpoint>) {
        if (closed || endpoints.isEmpty()) return
        val connectedServerId = mutableState.value.serverId ?: return
        if (!mutableState.value.isConnected ||
            endpoints.none { endpoint -> endpoint.expectedServerId == connectedServerId }
        ) {
            return
        }
        requestedEndpoints = endpoints.distinctBy { endpoint ->
            "${endpoint.host.lowercase()}:${endpoint.port}:${endpoint.expectedServerId}"
        }
    }

    fun disconnect(message: String = "Disconnected") {
        if (closed) return
        val previous = mutableState.value
        generation += 1
        connectionJob?.cancel()
        connectionJob = null
        closeTransport()
        val wasAuthenticated = previous.phase in setOf(
            BobConnectionPhase.OpeningSession,
            BobConnectionPhase.Synchronizing,
            BobConnectionPhase.Connected,
        )
        mutableState.value = BobConnectionState(
            phase = BobConnectionPhase.Idle,
            endpoint = requestedEndpoints.firstOrNull(),
            serverId = previous.serverId.takeIf { wasAuthenticated },
            serverName = previous.serverName.takeIf { wasAuthenticated },
            message = message,
        )
    }

    /** Removes only the blocked server's saved pin and never reconnects implicitly. */
    fun resetBlockedTrust() {
        if (closed) return
        val blocked = mutableState.value
        val serverId = blocked.serverId ?: return
        if (!blocked.canResetTrust || trustResetInProgress) return

        generation += 1
        val resetGeneration = generation
        trustResetInProgress = true
        connectionJob?.cancel()
        connectionJob = null
        closeTransport()
        mutableState.value = blocked.copy(
            message = "Removing the saved certificate pin for this computer…",
            trustResetAllowed = false,
        )
        scope.launch {
            try {
                withContext(Dispatchers.IO) { httpsConnector.resetTrust(serverId) }
                if (!isCurrent(resetGeneration)) return@launch
                trustResetInProgress = false
                mutableState.value = BobConnectionState(
                    phase = BobConnectionPhase.Idle,
                    endpoint = requestedEndpoints.firstOrNull(),
                    message = "Certificate trust reset. Reconnect to trust this computer again.",
                )
            } catch (error: Exception) {
                if (!isCurrent(resetGeneration)) return@launch
                trustResetInProgress = false
                mutableState.value = BobConnectionState(
                    phase = BobConnectionPhase.Failed,
                    endpoint = requestedEndpoints.firstOrNull(),
                    serverId = serverId,
                    message = error.userMessage("Could not reset trust"),
                )
            } finally {
                trustResetInProgress = false
            }
        }
    }

    /** Durably queues the original text before attempting to place it on WSS. */
    suspend fun queueText(text: String): PersistedTextMessage {
        val snapshot = mutableState.value
        val serverId = snapshot.serverId
            ?: throw IllegalStateException("Not connected to a computer.")
        if (!snapshot.isConnected) throw IllegalStateException("Secure session is not connected.")
        val maxTextBytes = snapshot.maxTextBytes
            ?: throw IllegalStateException("Text size limit has not been negotiated.")
        val textBytes = BobProtocol.utf8Size(text)
        if (textBytes > maxTextBytes) {
            throw IllegalArgumentException(
                "Text is $textBytes bytes in UTF-8, exceeding this computer's $maxTextBytes-byte limit.",
            )
        }

        val textId = UUID.randomUUID()
        val createdAt = Instant.now()
        try {
            BobProtocol.encode(BobProtocol.textSend(textId, text, createdAt))
        } catch (error: Exception) {
            throw IllegalArgumentException("Text does not fit in a 1 MiB WSS message. Shorten it and try again.", error)
        }

        val message = withContext(Dispatchers.IO) {
            textStore.queueOutgoing(
                serverId = serverId.toString(),
                text = text,
                textId = textId.toString(),
                createdAt = createdAt,
            )
        }
        sendPersistedIfCurrent(message)
        return message
    }

    suspend fun queueTransfers(uris: List<Uri>, kind: BobTransferKind) {
        if (!mutableState.value.isConnected) {
            throw IllegalStateException("Secure session is not connected.")
        }
        transferCoordinator.queueOutgoing(uris, kind)
    }

    private suspend fun runConnectionLoop(
        connectionGeneration: Long,
        initialEndpoints: List<BobEndpoint>,
    ) {
        var attempt = 0
        var preferredEndpoint: BobEndpoint? = null
        while (currentCoroutineContext().isActive && isCurrent(connectionGeneration)) {
            val endpoints = requestedEndpoints.ifEmpty { initialEndpoints }
            val candidates = preferredEndpoint
                ?.let { preferred ->
                    listOf(preferred) + endpoints.filterNot { candidate ->
                        candidate.host.equals(preferred.host, ignoreCase = true) &&
                            candidate.port == preferred.port
                    }
                }
                ?: endpoints
            var outcome: ConnectionOutcome = ConnectionOutcome.Retry("Connection failed")
            for ((index, endpoint) in candidates.withIndex()) {
                mutableState.value = BobConnectionState(
                    phase = BobConnectionPhase.Securing,
                    endpoint = endpoint,
                    serverId = endpoint.expectedServerId,
                    serverName = endpoint.displayNameHint,
                    message = if (candidates.size == 1) {
                        if (attempt == 0) "Verifying HTTPS certificate" else "Revalidating secure connection"
                    } else {
                        "Trying secure address ${index + 1} of ${candidates.size}"
                    },
                    retryAttempt = attempt,
                )

                try {
                    val established = httpsConnector.establish(endpoint)
                    if (!isCurrent(connectionGeneration)) {
                        established.close()
                        return
                    }
                    // Once any candidate authenticates, retries for that concrete address are
                    // strict-pinned to the observed serverId. Every discovered candidate retains
                    // the same mDNS serverId and Android Network socket factory.
                    preferredEndpoint = endpoint.copy(
                        expectedServerId = established.serverInfo.serverId,
                        displayNameHint = established.serverInfo.name,
                    )
                    transportGeneration = connectionGeneration
                    httpsConnection = established
                    outcome = runWebSocket(connectionGeneration, established)
                    break
                } catch (error: Throwable) {
                    closeTransport(connectionGeneration)
                    if (!isCurrent(connectionGeneration)) return
                    val blockedServerId = error.blockedServerId(endpoint)
                    if (error.isTrustOrIdentityFailure()) {
                        mutableState.value = BobConnectionState(
                            phase = BobConnectionPhase.Blocked,
                            endpoint = endpoint,
                            serverId = blockedServerId,
                            serverName = endpoint.displayNameHint,
                            message = error.userMessage(
                                "Certificate or computer identity verification failed",
                                endpoint,
                            ),
                            trustResetAllowed = error.allowsTrustReset(),
                        )
                        return
                    }
                    if (error.isInvalidServerResponse()) {
                        mutableState.value = BobConnectionState(
                            phase = BobConnectionPhase.Failed,
                            endpoint = endpoint,
                            serverId = endpoint.expectedServerId,
                            serverName = endpoint.displayNameHint,
                            message = error.userMessage("Invalid response from BOB service", endpoint),
                        )
                        return
                    }
                    if (!error.isReachabilityFailure()) {
                        mutableState.value = BobConnectionState(
                            phase = BobConnectionPhase.Failed,
                            endpoint = endpoint,
                            serverId = endpoint.expectedServerId,
                            serverName = endpoint.displayNameHint,
                            message = error.userMessage("Connection failed", endpoint),
                        )
                        return
                    }
                    outcome = ConnectionOutcome.Retry(
                        error.userMessage("Connection failed", endpoint),
                    )
                }
            }

            closeTransport(connectionGeneration)
            if (!isCurrent(connectionGeneration)) return
            when (outcome) {
                ConnectionOutcome.Stop -> return
                is ConnectionOutcome.Retry -> {
                    attempt += 1
                    val seconds = RETRY_SECONDS[(attempt - 1).coerceAtMost(RETRY_SECONDS.lastIndex)]
                    val delayLabel = if (seconds == 1) "1 second" else "$seconds seconds"
                    val retryMessage = outcome.message.trim().trimEnd('.', '!', '?')
                    mutableState.value = mutableState.value.copy(
                        phase = BobConnectionPhase.Retrying,
                        endpoint = preferredEndpoint ?: endpoints.first(),
                        message = "$retryMessage. Retrying in $delayLabel.",
                        retryAttempt = attempt,
                    )
                    delay(seconds * 1_000L)
                }
            }
        }
    }

    private suspend fun runWebSocket(
        connectionGeneration: Long,
        connection: BobHttpsConnection,
    ): ConnectionOutcome {
        val terminal = CompletableDeferred<BobSessionClosure>()
        var lastFailureMessage: String? = null
        lateinit var session: BobWebSocketSession
        val listener = object : BobWebSocketSession.Listener {
            override fun onStateChanged(state: BobSessionState) {
                if (!isCurrentSession(connectionGeneration, session)) return
                when (state.phase) {
                    BobSessionPhase.Connecting,
                    BobSessionPhase.AwaitingWelcome,
                    -> publishSessionPhase(
                        BobConnectionPhase.OpeningSession,
                        connection,
                        "Establishing secure WSS session",
                    )
                    BobSessionPhase.ReceivingSnapshot -> publishSessionPhase(
                        BobConnectionPhase.Synchronizing,
                        connection,
                        "Synchronizing session snapshot",
                    )
                    else -> Unit
                }
            }

            override fun onWelcome(welcome: BobWelcome) {
                if (!isCurrentSession(connectionGeneration, session)) return
                publishSessionPhase(
                    BobConnectionPhase.Synchronizing,
                    connection,
                    "Verified ${welcome.serverName}. Synchronizing…",
                    serverName = welcome.serverName,
                    maxTextBytes = welcome.limits.maxTextBytes,
                )
            }

            override fun onConnected(welcome: BobWelcome) {
                if (!isCurrentSession(connectionGeneration, session)) return
                // The server may send a transfer.offer immediately after the final snapshot.
                // Attach before any persistence suspension so that first offer cannot be lost.
                transferCoordinator.attach(connection, session)
                scope.launch {
                    try {
                        outboxMutex.withLock {
                            withContext(Dispatchers.IO) {
                                textStore.prepareOutgoingForReconnect(welcome.serverId.toString())
                            }
                        }
                        if (!isCurrentSession(connectionGeneration, session)) return@launch
                        mutableState.value = BobConnectionState(
                            phase = BobConnectionPhase.Connected,
                            endpoint = connection.endpoint,
                            serverId = welcome.serverId,
                            serverName = welcome.serverName,
                            maxTextBytes = welcome.limits.maxTextBytes,
                            message = "HTTPS, certificate pin, and WSS session verified",
                        )
                        replayIncomingAcknowledgements(
                            connectionGeneration,
                            session,
                            welcome.serverId,
                        )
                        drainOutbox(connectionGeneration, session, welcome.serverId)
                    } catch (error: Exception) {
                        if (isCurrentSession(connectionGeneration, session)) {
                            mutableState.value = mutableState.value.copy(
                                message = error.userMessage("Could not restore text queue"),
                            )
                            session.close(4400, "persistence_failed")
                        }
                    }
                }
            }

            override fun onTextReceived(text: BobIncomingText) {
                if (!isCurrentSession(connectionGeneration, session)) return
                scope.launch {
                    try {
                        val persisted = withContext(Dispatchers.IO) {
                            textStore.persistIncoming(
                                serverId = connection.serverInfo.serverId.toString(),
                                textId = text.textId.toString(),
                                text = text.text,
                                createdAt = text.createdAt,
                            )
                        }
                        if (isCurrentSession(connectionGeneration, session)) {
                            session.acknowledgeText(
                                textId = text.textId,
                                storedAt = requireNotNull(persisted.message.storedAt),
                                replyTo = text.envelopeId,
                            )
                        }
                    } catch (_: TextMessageConflictException) {
                        if (isCurrentSession(connectionGeneration, session)) {
                            session.close(4400, "invalid_metadata")
                        }
                    } catch (error: Exception) {
                        if (isCurrentSession(connectionGeneration, session)) {
                            session.close(4400, "persistence_failed")
                            mutableState.value = mutableState.value.copy(
                                message = error.userMessage("Could not save received text"),
                            )
                        }
                    }
                }
            }

            override fun onTextAcknowledged(acknowledgement: BobTextAcknowledgement) {
                if (!isCurrentSession(connectionGeneration, session)) return
                scope.launch {
                    try {
                        withContext(Dispatchers.IO) {
                            textStore.markOutgoingDelivered(
                                serverId = connection.serverInfo.serverId.toString(),
                                textId = acknowledgement.textId.toString(),
                                storedAt = acknowledgement.storedAt,
                            )
                        }
                    } catch (error: Exception) {
                        if (isCurrentSession(connectionGeneration, session)) {
                            mutableState.value = mutableState.value.copy(
                                message = error.userMessage("Acknowledgement received, but delivery status could not be saved"),
                            )
                        }
                    }
                }
            }

            override fun onTransferOffer(offer: BobTransferOffer) {
                if (isCurrentSession(connectionGeneration, session)) {
                    transferCoordinator.onOffer(offer)
                }
            }

            override fun onTransferAccepted(accepted: BobTransferAccepted) {
                if (isCurrentSession(connectionGeneration, session)) {
                    transferCoordinator.onAccepted(accepted)
                }
            }

            override fun onTransferDigest(digest: BobTransferDigest) {
                if (isCurrentSession(connectionGeneration, session)) {
                    transferCoordinator.onDigest(digest)
                }
            }

            override fun onTransferCompleted(completed: BobTransferCompleted) {
                if (isCurrentSession(connectionGeneration, session)) {
                    transferCoordinator.onCompleted(completed)
                }
            }

            override fun onTransferTerminalAck(acknowledgement: BobTransferTerminalAck) {
                if (isCurrentSession(connectionGeneration, session)) {
                    transferCoordinator.onTerminalAck(acknowledgement)
                }
            }

            override fun onTransferFailed(failed: BobTransferFailed) {
                if (isCurrentSession(connectionGeneration, session)) {
                    transferCoordinator.onFailed(failed)
                }
            }

            override fun onTransferProgress(progress: BobTransferProgress) {
                if (isCurrentSession(connectionGeneration, session)) {
                    transferCoordinator.onProgress(progress)
                }
            }

            override fun onRemoteError(error: BobRemoteError) {
                if (!isCurrentSession(connectionGeneration, session)) return
                mutableState.value = mutableState.value.copy(
                    message = error.uiMessage(),
                )
            }

            override fun onFailure(error: Throwable, httpStatus: Int?) {
                if (!isCurrentSession(connectionGeneration, session)) return
                val suffix = httpStatus?.let { " (HTTP $it)" }.orEmpty()
                lastFailureMessage =
                    error.userMessage("WSS connection failed", connection.endpoint) + suffix
                mutableState.value = mutableState.value.copy(
                    message = lastFailureMessage,
                )
            }

            override fun onDisconnected(closure: BobSessionClosure) {
                if (!isCurrentSession(connectionGeneration, session)) return
                terminal.complete(closure)
            }
        }

        session = BobWebSocketSession(
            okHttpClient = connection.client,
            webSocketUrl = connection.endpoint.webSocketUrl(),
            expectedServerId = connection.serverInfo.serverId,
            authenticatedServerName = connection.serverInfo.name,
            installationId = trustStore.getOrCreateInstallationId(),
            clientName = Build.MODEL.take(128).ifBlank { "Android" },
            osVersion = Build.VERSION.RELEASE,
            appVersion = appVersion(),
            listener = listener,
            callbackExecutor = callbackExecutor,
        )
        webSocketSession = session
        publishSessionPhase(
            BobConnectionPhase.OpeningSession,
            connection,
            "Establishing secure WSS session",
        )
        val started = session.connect()
        if (!started && session.state.closure == null) {
            session.dispose()
            return ConnectionOutcome.Retry("Could not start WSS session")
        }

        val closure = terminal.await()
        return when {
            closure.cause == BobSessionCloseCause.UnsupportedVersion -> ConnectionOutcome.Stop.also {
                mutableState.value = mutableState.value.copy(
                    phase = BobConnectionPhase.Failed,
                    message = "BOB protocol versions on this computer and phone are incompatible. Certificate trust was not changed.",
                )
            }
            closure.cause == BobSessionCloseCause.ProtocolError -> ConnectionOutcome.Stop.also {
                mutableState.value = mutableState.value.copy(
                    phase = BobConnectionPhase.Failed,
                    message = "Invalid WSS protocol message. Connection stopped.",
                )
            }
            closure.retryable || closure.cause == BobSessionCloseCause.Normal -> ConnectionOutcome.Retry(
                when (closure.cause) {
                    BobSessionCloseCause.PeerBusy -> "This computer is connected to another phone"
                    BobSessionCloseCause.HelloTimeout -> "WSS session confirmation timed out"
                    BobSessionCloseCause.Normal -> "WSS session closed"
                    BobSessionCloseCause.RemoteClose -> "The computer closed the WSS session"
                    BobSessionCloseCause.NetworkFailure ->
                        lastFailureMessage ?: "WSS connection was interrupted"
                    else -> "WSS connection was interrupted"
                },
            )
            else -> ConnectionOutcome.Stop.also {
                mutableState.value = mutableState.value.copy(
                    phase = BobConnectionPhase.Idle,
                    message = "WSS session closed.",
                )
            }
        }
    }

    private suspend fun drainOutbox(
        connectionGeneration: Long,
        session: BobWebSocketSession,
        serverId: UUID,
    ) {
        outboxMutex.withLock {
            val queued = withContext(Dispatchers.IO) {
                textStore.queuedOutgoing(serverId.toString())
            }
            for (message in queued) {
                if (!isCurrentSession(connectionGeneration, session)) return@withLock
                withContext(Dispatchers.IO) {
                    textStore.markOutgoingSending(message.serverId, message.textId)
                }
                if (session.sendText(
                        UUID.fromString(message.textId),
                        message.text,
                        message.createdAt,
                    ) == null
                ) {
                    withContext(Dispatchers.IO) {
                        textStore.requeueOutgoing(message.serverId, message.textId)
                    }
                    return@withLock
                }
            }
        }
    }

    private fun replayIncomingAcknowledgements(
        connectionGeneration: Long,
        session: BobWebSocketSession,
        serverId: UUID,
    ) {
        val incoming = textStore.timeline.value.filter { message ->
            message.serverId == serverId.toString() &&
                message.direction == TextDirection.Incoming &&
                message.storedAt != null
        }
        for (message in incoming) {
            if (!isCurrentSession(connectionGeneration, session)) return
            if (session.acknowledgeText(
                    textId = UUID.fromString(message.textId),
                    storedAt = requireNotNull(message.storedAt),
                ) == null
            ) {
                return
            }
        }
    }

    private suspend fun sendPersistedIfCurrent(message: PersistedTextMessage) {
        val session = webSocketSession ?: return
        val snapshot = mutableState.value
        if (!snapshot.isConnected || snapshot.serverId?.toString() != message.serverId) return
        try {
            drainOutbox(generation, session, requireNotNull(snapshot.serverId))
        } catch (error: Exception) {
            if (webSocketSession === session) {
                mutableState.value = mutableState.value.copy(
                    message = error.userMessage("Text was safely queued but cannot be sent yet"),
                )
            }
        }
    }

    private fun publishSessionPhase(
        phase: BobConnectionPhase,
        connection: BobHttpsConnection,
        message: String,
        serverName: String = connection.serverInfo.name,
        maxTextBytes: Int? = null,
    ) {
        mutableState.value = BobConnectionState(
            phase = phase,
            endpoint = connection.endpoint,
            serverId = connection.serverInfo.serverId,
            serverName = serverName,
            maxTextBytes = maxTextBytes,
            message = message,
        )
    }

    private fun isCurrent(connectionGeneration: Long): Boolean =
        !closed && generation == connectionGeneration

    private fun isCurrentSession(
        connectionGeneration: Long,
        session: BobWebSocketSession,
    ): Boolean = isCurrent(connectionGeneration) && webSocketSession === session

    private fun closeTransport(ownerGeneration: Long? = null) {
        if (ownerGeneration != null && transportGeneration != ownerGeneration) return
        val session = webSocketSession
        webSocketSession = null
        if (session != null) transferCoordinator.detach(session)
        session?.dispose()
        val connection = httpsConnection
        httpsConnection = null
        transportGeneration = null
        connection?.close()
    }

    private fun appVersion(): String = runCatching {
        applicationContext.packageManager
            .getPackageInfo(applicationContext.packageName, 0)
            .versionName
    }.getOrNull().orEmpty().ifBlank { "0.1.0" }

    override fun close() {
        if (closed) return
        closed = true
        trustResetInProgress = false
        generation += 1
        connectionJob?.cancel()
        connectionJob = null
        closeTransport()
    }

    private sealed interface ConnectionOutcome {
        data object Stop : ConnectionOutcome
        data class Retry(val message: String) : ConnectionOutcome
    }

    private companion object {
        val ACTIVE_PHASES = setOf(
            BobConnectionPhase.Securing,
            BobConnectionPhase.OpeningSession,
            BobConnectionPhase.Synchronizing,
            BobConnectionPhase.Connected,
        )
        val RETRY_SECONDS = intArrayOf(1, 2, 4, 8, 15)
    }
}

private fun Throwable.isTrustOrIdentityFailure(): Boolean =
    causeChain().any { cause ->
        cause is BobSecurityException ||
            cause is BobCertificatePinMismatchException ||
            cause is BobCertificateValidationException ||
            cause is SSLHandshakeException && cause.cause is java.security.cert.CertificateException
    }

private fun Throwable.isInvalidServerResponse(): Boolean =
    causeChain().any { it is BobServerInfoException || it is BobHttpsException }

private fun Throwable.isReachabilityFailure(): Boolean =
    causeChain().any { it is IOException }

private fun Throwable.blockedServerId(endpoint: BobEndpoint): UUID? =
    causeChain().firstNotNullOfOrNull { cause ->
        when (cause) {
            is BobTrustConflictException -> cause.serverId
            is BobIdentityMismatchException -> cause.expectedServerId
            is BobCertificatePinMismatchException -> cause.serverId
            else -> null
        }
    } ?: endpoint.expectedServerId

private fun Throwable.allowsTrustReset(): Boolean =
    causeChain().any { it is BobTrustConflictException || it is BobCertificatePinMismatchException }

private fun Throwable.causeChain(): Sequence<Throwable> = sequence {
    var current: Throwable? = this@causeChain
    val seen = HashSet<Throwable>()
    while (current != null && seen.add(current)) {
        yield(current)
        current = current.cause
    }
}

private fun Throwable.userMessage(prefix: String, endpoint: BobEndpoint? = null): String {
    val causes = causeChain().toList()
    val detail = when {
        causes.any { it is BobCertificatePinMismatchException } ->
            "the saved certificate pin no longer matches"
        causes.any { it is BobIdentityMismatchException || it is BobTrustConflictException } ->
            "the computer identity does not match the discovery or trust record"
        causes.any { it is BobCertificateValidationException } ->
            "the computer certificate does not meet BOB security requirements"
        causes.any { it is SSLHandshakeException } -> "the TLS handshake was rejected"
        causes.any { it is SocketTimeoutException } -> "the connection timed out"
        causes.any { it is ConnectException } -> "no BOB service accepted the connection"
        causes.any { it is NoRouteToHostException } -> "the address is not reachable on this network"
        causes.any { it is UnknownHostException } -> "the address could not be resolved"
        causes.any { it is BobServerInfoException } -> "the BOB /info response was invalid"
        causes.any { it is BobHttpsException } -> "the HTTPS response was not a valid BOB service response"
        causes.any { it is IOException } -> "the network connection was interrupted"
        else -> null
    }
    val target = endpoint?.let { value ->
        val host = if (':' in value.host && !value.host.startsWith("[")) {
            "[${value.host}]"
        } else {
            value.host
        }
        " at $host:${value.port}"
    }.orEmpty()
    return if (detail == null) "$prefix$target." else "$prefix$target: $detail."
}

private fun BobRemoteError.uiMessage(): String = when (code) {
    BobProtocol.ERROR_UNSUPPORTED_VERSION ->
        "The devices do not share a supported protocol version."
    BobProtocol.ERROR_PEER_BUSY ->
        "This computer is already connected to another phone."
    BobProtocol.ERROR_INVALID_METADATA ->
        "The computer rejected invalid message metadata."
    BobProtocol.ERROR_MESSAGE_TOO_LARGE ->
        "The computer rejected a message that was too large."
    BobProtocol.ERROR_INVALID_MESSAGE ->
        "The computer rejected an invalid protocol message."
    else -> "The computer reported a protocol error."
}
