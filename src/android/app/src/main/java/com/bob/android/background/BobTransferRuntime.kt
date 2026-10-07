package com.bob.android.background

import android.content.Context
import android.content.Intent
import com.bob.android.application.BobConnectionCoordinator
import com.bob.android.application.BobConnectionPhase
import com.bob.android.transfer.OnlineTransferItem
import com.bob.android.transfer.OnlineTransferState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.combine

/** The service and screen share one process-local connection during an active transfer. */
class BobTransferRuntime private constructor(context: Context) {
    private val applicationContext = context.applicationContext
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    val connection = BobConnectionCoordinator(applicationContext, scope)
    var isVisible = false
        private set
    private var backgroundDisconnect: Job? = null
    private var serviceRequested = false
    private var backgroundServiceRestricted = false
    private var stopServiceJob: Job? = null
    private var lastActiveKeys = emptySet<String>()
    private var serviceServerId: String? = null
    private var queuedReconnectStartedNanos: Long? = null
    private var queuedReconnectTimeout: Job? = null

    init {
        scope.launch {
            combine(connection.transferItems, connection.state) { items, _ -> items }.collect { items ->
                refreshForegroundState(items)
            }
        }
    }

    private fun refreshForegroundState(items: List<OnlineTransferItem>) {
        val active = foregroundItems(items)
        if (active.isNotEmpty()) {
            clearQueuedReconnectWindow()
            stopServiceJob?.cancel()
            stopServiceJob = null
            lastActiveKeys = active.map { it.transferId }.toSet()
            backgroundDisconnect?.cancel()
            backgroundDisconnect = null
            if (!backgroundServiceRestricted) ensureForegroundService(active)
            return
        }
        val waiting = queuedReconnectItems(items)
        if (waiting.isNotEmpty()) {
            stopServiceJob?.cancel()
            stopServiceJob = null
            backgroundDisconnect?.cancel()
            backgroundDisconnect = null
            TransferForegroundService.update(waiting)
            return
        }
        if (serviceRequested && stopServiceJob == null) {
            stopServiceJob = scope.launch {
                // The next FIFO item can become active immediately after a terminal update.
                delay(300L)
                if (foregroundServiceItems().isEmpty()) {
                    val ended = connection.transferItems.value.filter { it.transferId in lastActiveKeys }
                    TransferForegroundService.finish(ended, isVisible)
                    applicationContext.stopService(Intent(applicationContext, TransferForegroundService::class.java))
                    serviceRequested = false
                    serviceServerId = null
                    clearQueuedReconnectWindow()
                }
                stopServiceJob = null
            }
        }
        scheduleIdleDisconnect()
    }

    /** Includes at most a short reconnect wait for a batch whose service already started. */
    fun foregroundServiceItems(): List<OnlineTransferItem> {
        val items = connection.transferItems.value
        return foregroundItems(items).ifEmpty { queuedReconnectItems(items) }
    }

    private fun queuedReconnectItems(items: List<OnlineTransferItem>): List<OnlineTransferItem> {
        val serverId = connection.state.value.serverId?.toString()
        val queued = items.filter { it.serverId == serviceServerId && it.state == OnlineTransferState.Queued }
        val elapsed = queuedReconnectStartedNanos?.let { (System.nanoTime() - it) / 1_000_000L }
        if (backgroundServiceRestricted || serverId != serviceServerId ||
            !shouldKeepStartedTransferService(serviceRequested, connection.state.value.phase, elapsed)) {
            return emptyList()
        }
        if (queuedReconnectStartedNanos == null) {
            queuedReconnectStartedNanos = System.nanoTime()
            queuedReconnectTimeout = scope.launch {
                delay(QUEUED_RECONNECT_WAIT_MILLIS)
                refreshForegroundState(connection.transferItems.value)
            }
        }
        // The computer owns its incoming batch queue, so the phone cannot infer
        // whether another offer will arrive. Keep the same bounded window in both
        // directions, including a delayed next offer while already connected.
        return queued.take(1).ifEmpty { items.filter { it.serverId == serviceServerId && it.transferId in lastActiveKeys }.take(1) }
    }

    private fun clearQueuedReconnectWindow() {
        queuedReconnectStartedNanos = null
        queuedReconnectTimeout?.cancel()
        queuedReconnectTimeout = null
    }

    fun setVisible(visible: Boolean) {
        isVisible = visible
        if (visible) {
            backgroundServiceRestricted = false
            backgroundDisconnect?.cancel()
            backgroundDisconnect = null
            val active = foregroundItems(connection.transferItems.value)
            if (active.isNotEmpty()) ensureForegroundService(active)
        } else {
            scheduleIdleDisconnect()
        }
    }

    fun serviceTimedOut() {
        pauseForPlatformLimit("Android reached its background data transfer time limit. Retry while BOB is open.")
    }

    private fun pauseForPlatformLimit(message: String) {
        backgroundServiceRestricted = true
        serviceRequested = false
        serviceServerId = null
        clearQueuedReconnectWindow()
        connection.pauseBackgroundTransfers(message)
        connection.disconnect("Background transfer service stopped.")
    }

    private fun ensureForegroundService(active: List<OnlineTransferItem>) {
        if (backgroundServiceRestricted) return
        try {
            if (!serviceRequested) {
                serviceServerId = connection.state.value.serverId?.toString()
                applicationContext.startForegroundService(Intent(applicationContext, TransferForegroundService::class.java))
                serviceRequested = true
            }
            TransferForegroundService.update(active)
        } catch (_: IllegalStateException) {
            pauseForPlatformLimit("Android could not keep this transfer running in the background.")
        } catch (_: SecurityException) {
            pauseForPlatformLimit("Android did not allow the transfer service to start.")
        }
    }

    private fun scheduleIdleDisconnect() {
        backgroundDisconnect?.cancel()
        if (isVisible || foregroundServiceItems().isNotEmpty()) return
        backgroundDisconnect = scope.launch {
            delay(2_000L)
            if (!isVisible && foregroundServiceItems().isEmpty()) {
                connection.disconnect("App moved to the background. Local network connection paused.")
            }
        }
    }

    private fun foregroundItems(items: List<OnlineTransferItem>): List<OnlineTransferItem> {
        val state = connection.state.value
        if (state.phase in setOf(BobConnectionPhase.Idle, BobConnectionPhase.Blocked, BobConnectionPhase.Failed)) {
            return emptyList()
        }
        val serverId = state.serverId?.toString() ?: return emptyList()
        return items.filter { it.serverId == serverId && it.isActiveTransfer() }
    }

    companion object {
        @Volatile private var instance: BobTransferRuntime? = null

        fun get(context: Context): BobTransferRuntime = instance ?: synchronized(this) {
            instance ?: BobTransferRuntime(context).also { instance = it }
        }
    }
}

internal fun OnlineTransferItem.isActiveTransfer(): Boolean = isSendingCancellation || state in setOf(
    OnlineTransferState.Offered,
    OnlineTransferState.Accepted,
    OnlineTransferState.Transferring,
    OnlineTransferState.Verifying,
)
