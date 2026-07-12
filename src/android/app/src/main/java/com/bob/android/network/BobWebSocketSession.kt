package com.bob.android.network

import java.io.IOException
import java.time.Instant
import java.util.UUID
import java.util.concurrent.Executor
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit
import okhttp3.HttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString

/**
 * One reusable, generation-guarded BOB WSS session.
 *
 * [okHttpClient] must already enforce the certificate pin and server identity selected by the
 * caller. [webSocketUrl] is the authenticated `https://host:port/bob/v1/ws` URL; OkHttp performs
 * the WebSocket upgrade over that strict TLS client. Listener callbacks are submitted in order,
 * so [callbackExecutor] should be a serial executor (the Android main executor is appropriate).
 */
class BobWebSocketSession(
    okHttpClient: OkHttpClient,
    private val webSocketUrl: HttpUrl,
    private val expectedServerId: UUID,
    val authenticatedServerName: String,
    private val installationId: UUID,
    private val clientName: String,
    private val osVersion: String,
    private val appVersion: String,
    private val listener: Listener,
    private val callbackExecutor: Executor = DirectExecutor,
) {
    interface Listener {
        fun onStateChanged(state: BobSessionState) {}

        fun onWelcome(welcome: BobWelcome) {}

        fun onSnapshotPage(page: BobSnapshotPage) {}

        fun onConnected(welcome: BobWelcome) {}

        /** Persist idempotently before calling [BobWebSocketSession.acknowledgeText]. */
        fun onTextReceived(text: BobIncomingText) {}

        fun onTextAcknowledged(acknowledgement: BobTextAcknowledgement) {}

        fun onRemoteError(error: BobRemoteError) {}

        fun onDisconnected(closure: BobSessionClosure) {}

        fun onFailure(error: Throwable, httpStatus: Int?) {}
    }

    private val lock = Any()
    private val webSocketClient = okHttpClient.newBuilder()
        .pingInterval(BobProtocol.HEARTBEAT_SECONDS, TimeUnit.SECONDS)
        .build()
    private val scheduler = ScheduledThreadPoolExecutor(
        1,
    ) { runnable ->
        Thread(runnable, "bob-wss-timeouts").apply { isDaemon = true }
    }.apply {
        removeOnCancelPolicy = true
        executeExistingDelayedTasksAfterShutdownPolicy = false
    }

    private var generation = 0L
    private var socket: WebSocket? = null
    private var helloId: UUID? = null
    private var welcome: BobWelcome? = null
    private var snapshotId: UUID? = null
    private var nextSnapshotPage = 0
    private var helloTimeout: ScheduledFuture<*>? = null
    private var forcedClose: ScheduledFuture<*>? = null
    private var localCloseCode: Int? = null
    private var localCloseReason: String? = null
    private var remoteCloseCode: Int? = null
    private var remoteCloseReason: String? = null
    private var terminalNotified = false
    private var disposed = false
    private var currentState = BobSessionState(BobSessionPhase.Idle, generation)

    init {
        require(webSocketUrl.scheme == "https") { "BOB WebSocket URL must use strict HTTPS/WSS" }
        require(expectedServerId != UUID(0L, 0L)) { "expectedServerId cannot be empty" }
        require(installationId != UUID(0L, 0L)) { "installationId cannot be empty" }
        require(authenticatedServerName.isNotBlank()) { "authenticatedServerName cannot be blank" }
        require(clientName.isNotBlank() && clientName.length <= 128) {
            "clientName must contain 1..128 non-blank characters"
        }
    }

    val state: BobSessionState
        get() = synchronized(lock) { currentState }

    /** Starts a new generation. Any prior socket is canceled and its late callbacks are ignored. */
    fun connect(): Boolean {
        val oldSocket: WebSocket?
        val listenerGeneration: Long
        val connectingState: BobSessionState
        synchronized(lock) {
            if (disposed) return false
            oldSocket = socket
            generation += 1
            listenerGeneration = generation
            cancelTimersLocked()
            socket = null
            helloId = null
            welcome = null
            snapshotId = null
            nextSnapshotPage = 0
            localCloseCode = null
            localCloseReason = null
            remoteCloseCode = null
            remoteCloseReason = null
            terminalNotified = false
            currentState = BobSessionState(BobSessionPhase.Connecting, generation)
            connectingState = currentState
        }

        oldSocket?.cancel()
        publishState(connectingState)
        val shouldOpen = synchronized(lock) {
            isCurrentGenerationLocked(listenerGeneration) &&
                !terminalNotified &&
                currentState.phase == BobSessionPhase.Connecting
        }
        if (!shouldOpen) return false

        val request = Request.Builder()
            .url(webSocketUrl)
            .header("Sec-WebSocket-Protocol", BobProtocol.SUBPROTOCOL)
            .build()
        val createdSocket = try {
            webSocketClient.newWebSocket(request, SocketListener(listenerGeneration))
        } catch (error: RuntimeException) {
            finishFailure(listenerGeneration, null, error, null)
            return false
        }

        val keepSocket = synchronized(lock) {
            if (isCurrentGenerationLocked(listenerGeneration) && !isTerminalLocked()) {
                if (socket == null) socket = createdSocket
                socket === createdSocket
            } else {
                false
            }
        }
        if (!keepSocket) createdSocket.cancel()
        return keepSocket
    }

    /** Queues a v1 text message only after the final snapshot page has been accepted. */
    fun sendText(textId: UUID, text: String, createdAt: Instant): UUID? {
        val limits = synchronized(lock) {
            if (currentState.phase != BobSessionPhase.Connected) return null
            welcome?.limits ?: return null
        }
        if (BobProtocol.utf8Size(text) > limits.maxTextBytes) return null
        val envelope = try {
            BobProtocol.textSend(textId, text, createdAt)
        } catch (_: IllegalArgumentException) {
            return null
        }
        return if (sendConnected(envelope)) envelope.id else null
    }

    /** Sends an ack only after the caller has durably and idempotently stored [textId]. */
    fun acknowledgeText(textId: UUID, storedAt: Instant, replyTo: UUID? = null): UUID? {
        val envelope = try {
            BobProtocol.textAck(textId, storedAt, replyTo)
        } catch (_: IllegalArgumentException) {
            return null
        }
        return if (sendConnected(envelope)) envelope.id else null
    }

    /** Idempotently begins a graceful close. */
    fun close(code: Int = 1000, reason: String = "client_close"): Boolean {
        if (code != 1000 && code !in 3000..4999) return false
        val current = synchronized(lock) { Pair(generation, socket) }
        val webSocket = current.second
        if (webSocket == null) {
            return finishClosed(
                current.first,
                code,
                reason,
                locallyRequested = true,
                forced = false,
            )
        }
        return beginClose(current.first, webSocket, code, reason, locallyRequested = true)
    }

    /** Permanently releases this session. A disposed instance cannot reconnect. */
    fun dispose() {
        val oldSocket: WebSocket?
        val closure: BobSessionClosure
        val closedState: BobSessionState
        synchronized(lock) {
            if (disposed) return
            disposed = true
            generation += 1
            oldSocket = socket
            cancelTimersLocked()
            socket = null
            terminalNotified = true
            closure = BobSessionClosure(
                cause = BobSessionCloseCause.Disposed,
                code = null,
                reason = "disposed",
                retryable = false,
                forced = true,
            )
            currentState = BobSessionState(BobSessionPhase.Closed, generation, closure = closure)
            closedState = currentState
        }
        oldSocket?.cancel()
        scheduler.shutdownNow()
        publishState(closedState)
        dispatch(closedState.generation) { listener.onDisconnected(closure) }
    }

    private inner class SocketListener(private val listenerGeneration: Long) : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (response.header("Sec-WebSocket-Protocol") != BobProtocol.SUBPROTOCOL) {
                registerSocketIfCurrent(listenerGeneration, webSocket)
                protocolViolation(
                    listenerGeneration,
                    webSocket,
                    null,
                    "Server did not select ${BobProtocol.SUBPROTOCOL}",
                )
                return
            }

            val outboundHello = BobProtocol.hello(
                installationId = installationId,
                clientName = clientName,
                osVersion = osVersion,
                appVersion = appVersion,
            )
            val encodedHello = BobProtocol.encode(outboundHello)
            val awaitingState: BobSessionState
            synchronized(lock) {
                if (!isCurrentCallbackLocked(listenerGeneration, webSocket) ||
                    currentState.phase != BobSessionPhase.Connecting
                ) {
                    webSocket.cancel()
                    return
                }
                socket = webSocket
                helloId = outboundHello.id
                currentState = BobSessionState(BobSessionPhase.AwaitingWelcome, generation)
                awaitingState = currentState
                helloTimeout?.cancel(false)
                helloTimeout = scheduler.schedule(
                    { onWelcomeTimeout(listenerGeneration, webSocket) },
                    BobProtocol.HELLO_DEADLINE_SECONDS,
                    TimeUnit.SECONDS,
                )
            }
            if (!webSocket.send(encodedHello)) {
                finishFailure(
                    listenerGeneration,
                    webSocket,
                    IOException("Unable to queue session.hello"),
                    null,
                )
                return
            }
            publishState(awaitingState)
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            handleTextMessage(listenerGeneration, webSocket, text)
        }

        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            if (!isCurrentCallback(listenerGeneration, webSocket)) return
            protocolViolation(
                listenerGeneration,
                webSocket,
                null,
                "BOB v1 accepts UTF-8 JSON text messages only",
            )
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            handleRemoteClosing(listenerGeneration, webSocket, code, reason)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            val localClose = synchronized(lock) {
                localCloseCode?.let { localCode ->
                    Pair(localCode, localCloseReason.orEmpty())
                }
            }
            finishClosed(
                listenerGeneration,
                localClose?.first ?: code,
                localClose?.second ?: reason,
                locallyRequested = localClose != null,
                forced = false,
            )
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            val httpStatus = response?.code
            response?.close()
            val expectedClose = synchronized(lock) {
                if (!isCurrentCallbackLocked(listenerGeneration, webSocket)) return
                val code = localCloseCode ?: remoteCloseCode
                val reason = localCloseReason ?: remoteCloseReason
                if (code == null) null else Triple(code, reason.orEmpty(), localCloseCode != null)
            }
            if (expectedClose != null) {
                finishClosed(
                    listenerGeneration,
                    expectedClose.first,
                    expectedClose.second,
                    locallyRequested = expectedClose.third,
                    forced = true,
                )
            } else {
                finishFailure(listenerGeneration, webSocket, t, httpStatus)
            }
        }
    }

    private fun handleTextMessage(listenerGeneration: Long, webSocket: WebSocket, text: String) {
        val phaseAndLimit = synchronized(lock) {
            if (!isCurrentCallbackLocked(listenerGeneration, webSocket)) return
            Pair(
                currentState.phase,
                welcome?.limits?.maxEnvelopeBytes ?: BobProtocol.MAX_ENVELOPE_BYTES,
            )
        }

        val messageBytes = BobProtocol.utf8Size(text)
        val envelope = try {
            BobProtocol.decode(text, phaseAndLimit.second)
        } catch (error: BobProtocolException) {
            protocolViolation(listenerGeneration, webSocket, null, error.message.orEmpty(), error.code)
            return
        }

        if (envelope.version != BobProtocol.VERSION) {
            sendError(
                listenerGeneration,
                webSocket,
                BobProtocol.ERROR_UNSUPPORTED_VERSION,
                "Envelope version is not supported",
                retryable = false,
                replyTo = envelope.id,
            )
            if (phaseAndLimit.first != BobSessionPhase.Connected) {
                beginClose(
                    listenerGeneration,
                    webSocket,
                    BobProtocol.CLOSE_UNSUPPORTED_VERSION,
                    BobProtocol.ERROR_UNSUPPORTED_VERSION,
                    locallyRequested = true,
                )
            }
            return
        }

        if (envelope.type == "session.snapshot" &&
            messageBytes > BobProtocol.MAX_SNAPSHOT_ENVELOPE_BYTES
        ) {
            protocolViolation(
                listenerGeneration,
                webSocket,
                envelope.id,
                "Snapshot page exceeds 768 KiB",
                BobProtocol.ERROR_MESSAGE_TOO_LARGE,
            )
            return
        }

        try {
            when (phaseAndLimit.first) {
                BobSessionPhase.AwaitingWelcome -> when (envelope.type) {
                    "session.welcome" -> handleWelcome(listenerGeneration, webSocket, envelope)
                    "error" -> handleRemoteError(listenerGeneration, webSocket, envelope)
                    else -> protocolViolation(
                        listenerGeneration,
                        webSocket,
                        envelope.id,
                        "Expected session.welcome",
                    )
                }

                BobSessionPhase.ReceivingSnapshot -> when (envelope.type) {
                    "session.snapshot" -> handleSnapshot(listenerGeneration, webSocket, envelope)
                    "error" -> handleRemoteError(listenerGeneration, webSocket, envelope)
                    else -> protocolViolation(
                        listenerGeneration,
                        webSocket,
                        envelope.id,
                        "Expected session.snapshot",
                    )
                }

                BobSessionPhase.Connected -> when (envelope.type) {
                    "text.send" -> {
                        val maximumTextBytes = synchronized(lock) {
                            welcome?.limits?.maxTextBytes ?: BobProtocol.MAX_TEXT_BYTES
                        }
                        val incoming = BobProtocol.parseTextSend(envelope, maximumTextBytes)
                        dispatch(listenerGeneration) { listener.onTextReceived(incoming) }
                    }

                    "text.ack" -> {
                        val acknowledgement = BobProtocol.parseTextAck(envelope)
                        dispatch(listenerGeneration) {
                            listener.onTextAcknowledged(acknowledgement)
                        }
                    }

                    "error" -> handleRemoteError(listenerGeneration, webSocket, envelope)
                    else -> sendError(
                        listenerGeneration,
                        webSocket,
                        BobProtocol.ERROR_INVALID_MESSAGE,
                        "Unsupported message type: ${envelope.type}",
                        retryable = false,
                        replyTo = envelope.id,
                    )
                }

                else -> Unit
            }
        } catch (error: BobProtocolException) {
            protocolViolation(
                listenerGeneration,
                webSocket,
                envelope.id,
                error.message.orEmpty(),
                error.code,
            )
        }
    }

    private fun handleWelcome(
        listenerGeneration: Long,
        webSocket: WebSocket,
        envelope: BobEnvelope,
    ) {
        val expectedHelloId = synchronized(lock) {
            if (!isCurrentCallbackLocked(listenerGeneration, webSocket)) return
            helloId ?: throw BobProtocolException(
                BobProtocol.ERROR_INVALID_MESSAGE,
                "Missing local hello id",
            )
        }
        val parsed = BobProtocol.parseWelcome(envelope, expectedHelloId, expectedServerId)
        val receivingState: BobSessionState
        synchronized(lock) {
            if (!isCurrentCallbackLocked(listenerGeneration, webSocket) ||
                currentState.phase != BobSessionPhase.AwaitingWelcome
            ) {
                return
            }
            helloTimeout?.cancel(false)
            helloTimeout = null
            welcome = parsed
            snapshotId = null
            nextSnapshotPage = 0
            currentState = BobSessionState(
                phase = BobSessionPhase.ReceivingSnapshot,
                generation = generation,
                welcome = parsed,
            )
            receivingState = currentState
        }
        publishState(receivingState)
        dispatch(listenerGeneration) { listener.onWelcome(parsed) }
    }

    private fun handleSnapshot(
        listenerGeneration: Long,
        webSocket: WebSocket,
        envelope: BobEnvelope,
    ) {
        val parsed = BobProtocol.parseSnapshotPage(envelope)
        var connectedState: BobSessionState? = null
        var acceptedWelcome: BobWelcome? = null
        synchronized(lock) {
            if (!isCurrentCallbackLocked(listenerGeneration, webSocket) ||
                currentState.phase != BobSessionPhase.ReceivingSnapshot
            ) {
                return
            }
            val activeSnapshotId = snapshotId
            if (activeSnapshotId == null) {
                if (parsed.page != 0) {
                    throw BobProtocolException(
                        BobProtocol.ERROR_INVALID_MESSAGE,
                        "Snapshot must start at page 0",
                    )
                }
                snapshotId = parsed.snapshotId
            } else if (activeSnapshotId != parsed.snapshotId) {
                throw BobProtocolException(
                    BobProtocol.ERROR_INVALID_MESSAGE,
                    "snapshotId changed before the final page",
                )
            }
            if (parsed.page != nextSnapshotPage) {
                throw BobProtocolException(
                    BobProtocol.ERROR_INVALID_MESSAGE,
                    "Snapshot pages are not contiguous",
                )
            }
            if (!parsed.isLast && parsed.page == Int.MAX_VALUE) {
                throw BobProtocolException(
                    BobProtocol.ERROR_INVALID_MESSAGE,
                    "Snapshot page overflow",
                )
            }
            nextSnapshotPage = parsed.page + 1
            if (parsed.isLast) {
                acceptedWelcome = welcome ?: throw BobProtocolException(
                    BobProtocol.ERROR_INVALID_MESSAGE,
                    "Snapshot arrived without welcome",
                )
                currentState = BobSessionState(
                    phase = BobSessionPhase.Connected,
                    generation = generation,
                    welcome = acceptedWelcome,
                )
                connectedState = currentState
            }
        }

        dispatch(listenerGeneration) { listener.onSnapshotPage(parsed) }
        val stateAfterSnapshot = connectedState
        val finalWelcome = acceptedWelcome
        if (stateAfterSnapshot != null && finalWelcome != null) {
            publishState(stateAfterSnapshot)
            dispatch(listenerGeneration) { listener.onConnected(finalWelcome) }
        }
    }

    private fun handleRemoteError(
        listenerGeneration: Long,
        webSocket: WebSocket,
        envelope: BobEnvelope,
    ) {
        val error = BobProtocol.parseError(envelope)
        dispatch(listenerGeneration) { listener.onRemoteError(error) }
        val awaitingWelcome = synchronized(lock) {
            isCurrentCallbackLocked(listenerGeneration, webSocket) &&
                currentState.phase == BobSessionPhase.AwaitingWelcome
        }
        if (awaitingWelcome) {
            when (error.code) {
                BobProtocol.ERROR_UNSUPPORTED_VERSION -> beginClose(
                    listenerGeneration,
                    webSocket,
                    BobProtocol.CLOSE_UNSUPPORTED_VERSION,
                    BobProtocol.ERROR_UNSUPPORTED_VERSION,
                    locallyRequested = true,
                )
                BobProtocol.ERROR_PEER_BUSY -> beginClose(
                    listenerGeneration,
                    webSocket,
                    BobProtocol.CLOSE_PEER_BUSY,
                    BobProtocol.ERROR_PEER_BUSY,
                    locallyRequested = true,
                )
            }
        }
    }

    private fun sendConnected(envelope: BobEnvelope): Boolean {
        val current: Triple<Long, WebSocket, Int> = synchronized(lock) {
            if (currentState.phase != BobSessionPhase.Connected) return false
            val activeSocket = socket ?: return false
            val maximumBytes = welcome?.limits?.maxEnvelopeBytes ?: return false
            Triple(generation, activeSocket, maximumBytes)
        }
        val encoded = try {
            BobProtocol.encode(envelope, current.third)
        } catch (_: BobProtocolException) {
            return false
        }
        synchronized(lock) {
            if (!isCurrentCallbackLocked(current.first, current.second) ||
                currentState.phase != BobSessionPhase.Connected
            ) {
                return false
            }
        }
        return current.second.send(encoded)
    }

    private fun sendError(
        listenerGeneration: Long,
        webSocket: WebSocket,
        code: String,
        message: String,
        retryable: Boolean,
        replyTo: UUID?,
    ): Boolean {
        val maximumBytes = synchronized(lock) {
            if (!isCurrentCallbackLocked(listenerGeneration, webSocket)) return false
            welcome?.limits?.maxEnvelopeBytes ?: BobProtocol.MAX_ENVELOPE_BYTES
        }
        val encoded = try {
            BobProtocol.encode(
                BobProtocol.error(code, message, retryable, replyTo),
                maximumBytes,
            )
        } catch (_: BobProtocolException) {
            return false
        }
        return webSocket.send(encoded)
    }

    private fun protocolViolation(
        listenerGeneration: Long,
        webSocket: WebSocket,
        replyTo: UUID?,
        message: String,
        code: String = BobProtocol.ERROR_INVALID_MESSAGE,
    ) {
        if (!isCurrentCallback(listenerGeneration, webSocket)) return
        sendError(
            listenerGeneration,
            webSocket,
            code,
            message.ifBlank { "Invalid BOB protocol message" },
            retryable = false,
            replyTo = replyTo,
        )
        beginClose(
            listenerGeneration,
            webSocket,
            if (code == BobProtocol.ERROR_UNSUPPORTED_VERSION) {
                BobProtocol.CLOSE_UNSUPPORTED_VERSION
            } else {
                BobProtocol.CLOSE_INVALID_MESSAGE
            },
            code,
            locallyRequested = true,
        )
    }

    private fun onWelcomeTimeout(listenerGeneration: Long, webSocket: WebSocket) {
        val timedOut = synchronized(lock) {
            isCurrentCallbackLocked(listenerGeneration, webSocket) &&
                currentState.phase == BobSessionPhase.AwaitingWelcome
        }
        if (!timedOut) return
        beginClose(
            listenerGeneration,
            webSocket,
            BobProtocol.CLOSE_HELLO_TIMEOUT,
            "welcome_timeout",
            locallyRequested = true,
        )
    }

    private fun beginClose(
        listenerGeneration: Long,
        webSocket: WebSocket,
        code: Int,
        reason: String,
        locallyRequested: Boolean,
    ): Boolean {
        val closingState: BobSessionState
        synchronized(lock) {
            if (!isCurrentCallbackLocked(listenerGeneration, webSocket) || isTerminalLocked()) {
                return false
            }
            if (currentState.phase == BobSessionPhase.Closing) return false
            helloTimeout?.cancel(false)
            helloTimeout = null
            if (locallyRequested) {
                localCloseCode = code
                localCloseReason = reason
            }
            currentState = BobSessionState(
                phase = BobSessionPhase.Closing,
                generation = generation,
                welcome = welcome,
            )
            closingState = currentState
            scheduleForcedCloseLocked(listenerGeneration, webSocket, code, reason, locallyRequested)
        }
        publishState(closingState)

        val queued = try {
            webSocket.close(code, reason)
        } catch (_: IllegalArgumentException) {
            false
        }
        if (!queued) {
            webSocket.cancel()
            finishClosed(listenerGeneration, code, reason, locallyRequested, forced = true)
        }
        return true
    }

    private fun handleRemoteClosing(
        listenerGeneration: Long,
        webSocket: WebSocket,
        code: Int,
        reason: String,
    ) {
        var closingState: BobSessionState? = null
        var shouldReply = true
        var locallyRequested = false
        synchronized(lock) {
            if (!isCurrentCallbackLocked(listenerGeneration, webSocket) || isTerminalLocked()) return
            helloTimeout?.cancel(false)
            helloTimeout = null
            remoteCloseCode = code
            remoteCloseReason = reason
            locallyRequested = localCloseCode != null
            shouldReply = !locallyRequested
            if (currentState.phase != BobSessionPhase.Closing) {
                currentState = BobSessionState(
                    phase = BobSessionPhase.Closing,
                    generation = generation,
                    welcome = welcome,
                )
                closingState = currentState
            }
            scheduleForcedCloseLocked(
                listenerGeneration,
                webSocket,
                code,
                reason,
                locallyRequested = locallyRequested,
            )
        }
        closingState?.let(::publishState)
        if (shouldReply && !webSocket.close(code, reason)) {
            webSocket.cancel()
            finishClosed(listenerGeneration, code, reason, locallyRequested, forced = true)
        }
    }

    private fun scheduleForcedCloseLocked(
        listenerGeneration: Long,
        webSocket: WebSocket,
        code: Int,
        reason: String,
        locallyRequested: Boolean,
    ) {
        forcedClose?.cancel(false)
        forcedClose = scheduler.schedule(
            {
                val shouldForce = synchronized(lock) {
                    isCurrentCallbackLocked(listenerGeneration, webSocket) &&
                        currentState.phase == BobSessionPhase.Closing
                }
                if (shouldForce) {
                    webSocket.cancel()
                    finishClosed(
                        listenerGeneration,
                        code,
                        reason,
                        locallyRequested,
                        forced = true,
                    )
                }
            },
            CONTROL_CLOSE_TIMEOUT_SECONDS,
            TimeUnit.SECONDS,
        )
    }

    private fun finishClosed(
        listenerGeneration: Long,
        code: Int,
        reason: String,
        locallyRequested: Boolean,
        forced: Boolean,
    ): Boolean {
        val closure = classifyClose(code, reason, locallyRequested, forced)
        val closedState: BobSessionState
        synchronized(lock) {
            if (!isCurrentGenerationLocked(listenerGeneration) || terminalNotified) return false
            terminalNotified = true
            cancelTimersLocked()
            socket = null
            currentState = BobSessionState(
                phase = when (closure.cause) {
                    BobSessionCloseCause.UnsupportedVersion -> BobSessionPhase.Blocked
                    BobSessionCloseCause.Normal,
                    BobSessionCloseCause.ClientRequest,
                    BobSessionCloseCause.Disposed,
                    -> BobSessionPhase.Closed
                    else -> BobSessionPhase.Failed
                },
                generation = generation,
                welcome = welcome,
                closure = closure,
            )
            closedState = currentState
        }
        publishState(closedState)
        dispatch(listenerGeneration) { listener.onDisconnected(closure) }
        return true
    }

    private fun finishFailure(
        listenerGeneration: Long,
        webSocket: WebSocket?,
        error: Throwable,
        httpStatus: Int?,
    ) {
        val closure = BobSessionClosure(
            cause = BobSessionCloseCause.NetworkFailure,
            code = null,
            reason = error.message,
            retryable = true,
            forced = true,
        )
        val failedState: BobSessionState
        synchronized(lock) {
            if (!isCurrentGenerationLocked(listenerGeneration) || terminalNotified) return
            if (webSocket != null && socket != null && socket !== webSocket) return
            terminalNotified = true
            cancelTimersLocked()
            socket = null
            currentState = BobSessionState(
                phase = BobSessionPhase.Failed,
                generation = generation,
                welcome = welcome,
                closure = closure,
            )
            failedState = currentState
        }
        webSocket?.cancel()
        publishState(failedState)
        dispatch(listenerGeneration) { listener.onFailure(error, httpStatus) }
        dispatch(listenerGeneration) { listener.onDisconnected(closure) }
    }

    private fun registerSocketIfCurrent(listenerGeneration: Long, webSocket: WebSocket) {
        synchronized(lock) {
            if (isCurrentGenerationLocked(listenerGeneration) &&
                !terminalNotified &&
                currentState.phase == BobSessionPhase.Connecting &&
                socket == null
            ) {
                socket = webSocket
            }
        }
    }

    private fun isCurrentCallback(listenerGeneration: Long, webSocket: WebSocket): Boolean =
        synchronized(lock) { isCurrentCallbackLocked(listenerGeneration, webSocket) }

    private fun isCurrentCallbackLocked(listenerGeneration: Long, webSocket: WebSocket): Boolean =
        isCurrentGenerationLocked(listenerGeneration) &&
            !terminalNotified &&
            (socket == null || socket === webSocket)

    private fun isCurrentGenerationLocked(listenerGeneration: Long): Boolean =
        !disposed && generation == listenerGeneration

    private fun isTerminalLocked(): Boolean =
        currentState.phase == BobSessionPhase.Closed ||
            currentState.phase == BobSessionPhase.Failed ||
            currentState.phase == BobSessionPhase.Blocked

    private fun cancelTimersLocked() {
        helloTimeout?.cancel(false)
        helloTimeout = null
        forcedClose?.cancel(false)
        forcedClose = null
    }

    private fun publishState(state: BobSessionState) {
        dispatch(state.generation) { listener.onStateChanged(state) }
    }

    private fun dispatch(listenerGeneration: Long, callback: () -> Unit) {
        try {
            callbackExecutor.execute {
                val isCurrent = synchronized(lock) { generation == listenerGeneration }
                if (!isCurrent) return@execute
                try {
                    callback()
                } catch (_: Exception) {
                    // A UI callback must not tear down OkHttp's WebSocket reader thread.
                }
            }
        } catch (_: Exception) {
            // The UI executor may reject work while the application is shutting down.
        }
    }

    private companion object {
        const val CONTROL_CLOSE_TIMEOUT_SECONDS = 2L
        val DirectExecutor = Executor { command -> command.run() }

        fun classifyClose(
            code: Int,
            reason: String,
            locallyRequested: Boolean,
            forced: Boolean,
        ): BobSessionClosure {
            val cause = when (code) {
                1000 -> if (locallyRequested) {
                    BobSessionCloseCause.ClientRequest
                } else {
                    BobSessionCloseCause.Normal
                }
                BobProtocol.CLOSE_UNSUPPORTED_VERSION -> BobSessionCloseCause.UnsupportedVersion
                BobProtocol.CLOSE_PEER_BUSY -> BobSessionCloseCause.PeerBusy
                BobProtocol.CLOSE_HELLO_TIMEOUT -> BobSessionCloseCause.HelloTimeout
                BobProtocol.CLOSE_INVALID_MESSAGE -> BobSessionCloseCause.ProtocolError
                else -> BobSessionCloseCause.RemoteClose
            }
            val retryable = when (cause) {
                BobSessionCloseCause.PeerBusy,
                BobSessionCloseCause.HelloTimeout,
                BobSessionCloseCause.RemoteClose,
                BobSessionCloseCause.NetworkFailure,
                -> true
                else -> false
            }
            return BobSessionClosure(cause, code, reason, retryable, forced)
        }
    }
}

enum class BobSessionPhase {
    Idle,
    Connecting,
    AwaitingWelcome,
    ReceivingSnapshot,
    Connected,
    Closing,
    Closed,
    Failed,
    Blocked,
}

enum class BobSessionCloseCause {
    Normal,
    ClientRequest,
    UnsupportedVersion,
    PeerBusy,
    HelloTimeout,
    ProtocolError,
    RemoteClose,
    NetworkFailure,
    Disposed,
}

data class BobSessionClosure(
    val cause: BobSessionCloseCause,
    val code: Int?,
    val reason: String?,
    val retryable: Boolean,
    val forced: Boolean,
)

data class BobSessionState(
    val phase: BobSessionPhase,
    val generation: Long,
    val welcome: BobWelcome? = null,
    val closure: BobSessionClosure? = null,
)
