package com.bob.android.ui

import android.app.Application
import android.net.InetAddresses
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.bob.android.application.BobConnectionCoordinator
import com.bob.android.application.BobConnectionPhase
import com.bob.android.application.BobConnectionState
import com.bob.android.discovery.DiscoveredComputer
import com.bob.android.discovery.DiscoveryPhase
import com.bob.android.discovery.DiscoverySnapshot
import com.bob.android.discovery.NsdBobDiscoveryService
import com.bob.android.network.BobEndpoint
import com.bob.android.network.BobProtocol
import com.bob.android.network.BobTransferKind
import com.bob.android.persistence.PersistedTextMessage
import com.bob.android.persistence.TextDeliveryStatus
import com.bob.android.persistence.TextDirection
import com.bob.android.transfer.OnlineTransferItem
import com.bob.android.transfer.OnlineTransferState
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

class BobViewModel(application: Application) : AndroidViewModel(application) {
    private val discovery = NsdBobDiscoveryService(application)
    private val connection = BobConnectionCoordinator(application, viewModelScope)
    private val selectedTarget = MutableStateFlow<SelectedTarget?>(null)
    private val manualIp = MutableStateFlow("")
    private val notice = MutableStateFlow<String?>(null)
    private val draftText = MutableStateFlow("")
    private val isQueueingText = MutableStateFlow(false)
    private var automaticConnectionsEnabled = true
    private var lastAutomaticEndpointSignature: String? = null
    private var appVisible = false
    private var backgroundDisconnectJob: Job? = null
    private var automaticConnectJob: Job? = null
    private var draftTargetIdentity: String? = null
    private var draftRevision = 0L
    private var externalContentSelectionDepth = 0

    private val baseInputs = combine(
        discovery.snapshot,
        selectedTarget,
        manualIp,
        notice,
        connection.state,
    ) { snapshot, selection, manualIpValue, noticeValue, connectionState ->
        BaseInputs(snapshot, selection, manualIpValue, noticeValue, connectionState)
    }

    val uiState: StateFlow<BobUiState> = combine(
        baseInputs,
        draftText,
        isQueueingText,
        connection.textStore.timeline,
        connection.transferItems,
    ) { base, draft, queueing, messages, transfers ->
        buildUiState(base, draft, queueing, messages, transfers)
    }.stateIn(
        scope = viewModelScope,
        started = SharingStarted.Eagerly,
        initialValue = BobUiState(),
    )

    init {
        viewModelScope.launch {
            discovery.snapshot.collect { snapshot ->
                reconcileAutomaticSelection(snapshot)
                maybeConnectAutomatically(snapshot)
            }
        }
    }

    fun startDiscovery() {
        appVisible = true
        backgroundDisconnectJob?.cancel()
        backgroundDisconnectJob = null
        discovery.start()
    }

    fun stopDiscovery() {
        appVisible = false
        automaticConnectJob?.cancel()
        automaticConnectJob = null
        discovery.stop()
        scheduleBackgroundDisconnect()
    }

    fun beginExternalContentSelection() {
        externalContentSelectionDepth += 1
        backgroundDisconnectJob?.cancel()
        backgroundDisconnectJob = null
    }

    fun endExternalContentSelection() {
        externalContentSelectionDepth = (externalContentSelectionDepth - 1).coerceAtLeast(0)
        if (!appVisible) scheduleBackgroundDisconnect()
    }

    fun refresh() {
        notice.value = null
        discovery.refresh()
    }

    fun selectPeer(key: String) {
        val peer = discovery.snapshot.value.devices
            .firstOrNull { it.serviceName == key && it.isCompatible }
            ?: return
        automaticConnectionsEnabled = false
        updateSelectedTarget(
            SelectedTarget.Discovered(
                key = peer.serviceName,
                deviceId = peer.deviceId,
                automatic = false,
            ),
        )
        notice.value = null
        connection.connect(peer.toEndpoints())
    }

    fun updateManualIp(value: String) {
        manualIp.value = value.take(MAX_MANUAL_IP_LENGTH)
        notice.value = null
    }

    fun useManualIp() {
        val address = manualIp.value.trim()
        if (address.isEmpty() || !InetAddresses.isNumericAddress(address)) {
            notice.value = "Enter a valid IPv4 or IPv6 address."
            return
        }

        automaticConnectionsEnabled = false
        updateSelectedTarget(SelectedTarget.Manual(address))
        notice.value = "Connecting to ${endpoint(address, DEFAULT_PORT)}"
        connection.connect(BobEndpoint(host = address))
    }

    fun requestConnection() {
        val target = selectedTarget.value
        if (target == null) {
            notice.value = "Select a computer or enter an IP address first."
            return
        }

        val resolved = resolveEndpoints(target, discovery.snapshot.value.devices)
        if (resolved.isNullOrEmpty()) {
            notice.value = "This computer is no longer in the discovery results. Search again or enter its IP address."
            return
        }
        automaticConnectionsEnabled = false
        notice.value = null
        connection.connect(resolved)
    }

    fun disconnect() {
        automaticConnectionsEnabled = false
        connection.disconnect("Disconnected by you.")
    }

    fun resetTrust() {
        automaticConnectionsEnabled = false
        connection.resetBlockedTrust()
    }

    fun updateDraftText(value: String) {
        val nextDraft = value.take(MAX_DRAFT_CHARACTERS)
        draftRevision += 1
        if (nextDraft.isEmpty()) {
            draftTargetIdentity = null
        } else if (draftText.value.isEmpty() || draftTargetIdentity == null) {
            draftTargetIdentity = selectedTarget.value?.draftIdentity()
        }
        draftText.value = nextDraft
        notice.value = null
    }

    fun sendText() {
        val text = draftText.value
        if (text.trim().isEmpty()) {
            notice.value = "Enter some text."
            return
        }
        val maxTextBytes = connection.state.value.maxTextBytes ?: BobProtocol.MAX_TEXT_BYTES
        if (BobProtocol.utf8Size(text) > maxTextBytes) {
            notice.value = "Text exceeds the current connection limit of $maxTextBytes bytes."
            return
        }
        if (!connection.state.value.isConnected || isQueueingText.value) return

        val sentDraftRevision = draftRevision
        val sentDraftOwner = draftTargetIdentity
        isQueueingText.value = true
        viewModelScope.launch {
            try {
                connection.queueText(text)
                if (draftRevision == sentDraftRevision && draftTargetIdentity == sentDraftOwner) {
                    clearDraft()
                }
            } catch (_: Exception) {
                notice.value = "Could not queue text. The draft was kept."
            } finally {
                isQueueingText.value = false
            }
        }
    }

    fun queueImages(uris: List<Uri>) {
        queueContent(uris, BobTransferKind.Image)
    }

    fun queueFiles(uris: List<Uri>) {
        queueContent(uris, BobTransferKind.File)
    }

    private fun queueContent(uris: List<Uri>, kind: BobTransferKind) {
        if (uris.isEmpty()) return
        if (!connection.state.value.isConnected) {
            notice.value = "Reconnect before sending selected content."
            return
        }
        viewModelScope.launch {
            try {
                connection.queueTransfers(uris, kind)
            } catch (_: Exception) {
                notice.value = "Could not queue selected content."
            }
        }
    }

    private fun reconcileAutomaticSelection(snapshot: DiscoverySnapshot) {
        val eligible = snapshot.devices.filter(DiscoveredComputer::isCompatible)
        val activeServerId = connection.state.value.serverId?.toString()
        val nextTarget = when (val current = selectedTarget.value) {
            is SelectedTarget.Manual -> current
            is SelectedTarget.Discovered -> {
                val selectedPeer = eligible.firstOrNull { it.serviceName == current.key }
                when {
                    selectedPeer != null && eligible.size > 1 && current.automatic &&
                        !connection.state.value.isConnected -> null
                    selectedPeer != null -> current.copy(deviceId = selectedPeer.deviceId)
                    activeServerId != null -> current
                    eligible.size == 1 && current.automatic ->
                        eligible.single().let { peer ->
                            SelectedTarget.Discovered(
                                key = peer.serviceName,
                                deviceId = peer.deviceId,
                                automatic = true,
                            )
                        }
                    else -> null
                }
            }
            null -> eligible.singleOrNull()
                ?.let { peer ->
                    SelectedTarget.Discovered(
                        key = peer.serviceName,
                        deviceId = peer.deviceId,
                        automatic = true,
                    )
                }
        }
        updateSelectedTarget(nextTarget)
    }

    private fun maybeConnectAutomatically(snapshot: DiscoverySnapshot) {
        automaticConnectJob?.cancel()
        automaticConnectJob = null
        if (!automaticConnectionsEnabled) return
        val connectionPhase = connection.state.value.phase
        if (connectionPhase == BobConnectionPhase.Blocked) return
        val eligible = snapshot.devices.filter(DiscoveredComputer::isCompatible)
        if (eligible.size > 1) {
            if (connectionPhase in setOf(
                    BobConnectionPhase.Securing,
                    BobConnectionPhase.OpeningSession,
                    BobConnectionPhase.Synchronizing,
                    BobConnectionPhase.Retrying,
                )
            ) {
                connection.disconnect("Multiple computers found. Choose one to connect.")
                lastAutomaticEndpointSignature = null
            }
            return
        }
        val peer = eligible.singleOrNull() ?: return
        val selection = selectedTarget.value as? SelectedTarget.Discovered ?: return
        if (!selection.automatic || selection.key != peer.serviceName) return

        val signature = "${peer.deviceId}|${peer.hosts.joinToString(",")}|${peer.port}|${peer.network?.networkHandle}"
        if (signature == lastAutomaticEndpointSignature) return
        automaticConnectJob = viewModelScope.launch {
            delay(AUTOMATIC_CONNECT_SETTLE_MILLIS)
            val settled = discovery.snapshot.value.devices
                .filter(DiscoveredComputer::isCompatible)
                .singleOrNull()
            if (settled?.serviceName != peer.serviceName || !automaticConnectionsEnabled) return@launch
            lastAutomaticEndpointSignature = signature
            val endpoints = settled.toEndpoints()
            val connected = connection.state.value
            if (connected.isConnected && connected.serverId?.toString() == settled.deviceId) {
                connection.refreshConnectedEndpoints(endpoints)
            } else {
                connection.connect(endpoints)
            }
        }
    }

    private fun buildUiState(
        base: BaseInputs,
        draft: String,
        queueing: Boolean,
        messages: List<PersistedTextMessage>,
        transfers: List<OnlineTransferItem>,
    ): BobUiState {
        val snapshot = base.snapshot
        val selection = base.selection
        val connectionState = base.connectionState
        val compatibleCount = snapshot.devices.count(DiscoveredComputer::isCompatible)
        val discoveryLabel = when {
            snapshot.phase == DiscoveryPhase.Failed -> snapshot.errorMessage ?: "Local network discovery failed"
            compatibleCount == 0 && snapshot.phase == DiscoveryPhase.Searching -> "Searching on this Wi-Fi"
            compatibleCount == 1 -> "1 available computer found"
            compatibleCount > 1 -> "$compatibleCount available computers found"
            else -> "Discovery stopped"
        }
        val guidance = when {
            compatibleCount == 0 -> "No computers found. Refresh or enter the Windows IP address."
            compatibleCount == 1 -> "The only available computer will connect automatically using HTTPS, TOFU, and WSS."
            else -> "Multiple computers found. Choose one to connect."
        }

        val maxTextBytes = connectionState.maxTextBytes ?: BobProtocol.MAX_TEXT_BYTES
        val draftError = when {
            draft.isNotEmpty() && draft.trim().isEmpty() -> "Text cannot contain only whitespace."
            BobProtocol.utf8Size(draft) > maxTextBytes ->
                "UTF-8 content exceeds the current connection limit of $maxTextBytes bytes."
            else -> null
        }
        val activeServerId = connectionState.serverId?.toString()
        val timeline = if (activeServerId == null) {
            messages.asReversed().take(RECENT_MESSAGE_LIMIT).map(::toTimelineItem)
        } else {
            messages
                .filter { it.serverId == activeServerId }
                .asReversed()
                .take(RECENT_MESSAGE_LIMIT)
                .map(::toTimelineItem)
        }
        val connectionPresentation = presentConnection(connectionState, selection != null)
        val busy = connectionState.phase in setOf(
            BobConnectionPhase.Securing,
            BobConnectionPhase.OpeningSession,
            BobConnectionPhase.Synchronizing,
        )

        return BobUiState(
            discoveryLabel = discoveryLabel,
            isSearching = snapshot.phase == DiscoveryPhase.Searching,
            peers = snapshot.devices.map { peer ->
                PeerUiModel(
                    key = peer.serviceName,
                    name = peer.displayName,
                    endpoint = endpoint(peer.preferredHost, peer.port),
                    isCompatible = peer.isCompatible,
                    isSelected = selection is SelectedTarget.Discovered && selection.key == peer.serviceName,
                )
            },
            selectionGuidance = guidance,
            selectedTargetLabel = selection?.let { targetLabel(it, snapshot.devices) },
            manualIp = base.manualIp,
            notice = base.notice,
            canRequestConnection = selection != null && !busy && !connectionState.isConnected &&
                !(connectionState.phase == BobConnectionPhase.Blocked && connectionState.canResetTrust),
            connectionPhase = connectionPresentation.phase,
            connectionLabel = connectionPresentation.label,
            connectionDetail = connectionState.message ?: connectionPresentation.detail,
            authenticatedPeerName = connectionState.serverName,
            canDisconnect = connectionState.phase in setOf(
                BobConnectionPhase.Securing,
                BobConnectionPhase.OpeningSession,
                BobConnectionPhase.Synchronizing,
                BobConnectionPhase.Connected,
                BobConnectionPhase.Retrying,
            ),
            canResetTrust = connectionState.canResetTrust,
            draftText = draft,
            draftError = draftError,
            canSendText = connectionState.isConnected && draft.trim().isNotEmpty() &&
                draftError == null && !queueing,
            canChooseContent = connectionState.isConnected,
            isQueueingText = queueing,
            timeline = timeline,
            transfers = transfers.asReversed().map(::toTransferTimelineItem),
        )
    }

    private fun presentConnection(
        state: BobConnectionState,
        hasSelection: Boolean,
    ): ConnectionPresentation = when (state.phase) {
        BobConnectionPhase.Idle -> if (hasSelection) {
            ConnectionPresentation(ConnectionUiPhase.Ready, "Ready to connect", "The certificate will be verified before opening WSS.")
        } else {
            ConnectionPresentation(ConnectionUiPhase.NoTarget, "No computer selected", "Choose a discovered computer or enter an IP address.")
        }
        BobConnectionPhase.Securing ->
            ConnectionPresentation(ConnectionUiPhase.Securing, "Verifying HTTPS", "Checking the certificate using TOFU or the saved pin.")
        BobConnectionPhase.OpeningSession ->
            ConnectionPresentation(ConnectionUiPhase.Connecting, "Connecting to WSS", "Secure HTTPS identity verified.")
        BobConnectionPhase.Synchronizing ->
            ConnectionPresentation(ConnectionUiPhase.Synchronizing, "Synchronizing", "Sending will be enabled after the full snapshot arrives.")
        BobConnectionPhase.Connected ->
            ConnectionPresentation(ConnectionUiPhase.Connected, "Connected", "Text can now be sent in both directions.")
        BobConnectionPhase.Retrying ->
            ConnectionPresentation(ConnectionUiPhase.Retrying, "Connection interrupted. Retrying", "The saved pin will be used when the network returns.")
        BobConnectionPhase.Blocked ->
            ConnectionPresentation(ConnectionUiPhase.Blocked, "Connection blocked", "The certificate or server identity does not match the trust record.")
        BobConnectionPhase.Failed ->
            ConnectionPresentation(ConnectionUiPhase.Failed, "Connection failed", "Check BOB on Windows and try again.")
    }

    private fun toTimelineItem(message: PersistedTextMessage): TextTimelineItemUi {
        val outgoing = message.direction == TextDirection.Outgoing
        val preview = message.text.preview()
        val status = when (message.status) {
            TextDeliveryStatus.Queued -> "Queued"
            TextDeliveryStatus.Sending -> "Awaiting computer"
            TextDeliveryStatus.Delivered -> "Delivered"
            TextDeliveryStatus.Received -> "Received"
        }
        return TextTimelineItemUi(
            key = "${message.serverId}:${message.textId}",
            text = message.text,
            previewText = preview.first,
            isTruncated = preview.second,
            directionLabel = if (outgoing) "Sent" else "Received",
            statusLabel = status,
            timeLabel = TIME_FORMATTER.format(message.localReceivedAt),
            isOutgoing = outgoing,
        )
    }

    private fun toTransferTimelineItem(item: OnlineTransferItem): TransferTimelineItemUi {
        val status = when (item.state) {
            OnlineTransferState.Queued -> "Queued"
            OnlineTransferState.Offered -> "Waiting for computer"
            OnlineTransferState.Accepted -> "Accepted"
            OnlineTransferState.Transferring -> "Transferring"
            OnlineTransferState.Verifying -> "Verifying"
            OnlineTransferState.Completed -> "Completed"
            OnlineTransferState.Failed -> "Failed"
        }
        val progress = item.size?.takeIf { it > 0L }?.let { total ->
            val percent = ((item.bytesTransferred.coerceAtMost(total) * 100L) / total)
            "${formatBytes(item.bytesTransferred)} / ${formatBytes(total)} · $percent%"
        } ?: formatBytes(item.bytesTransferred)
        return TransferTimelineItemUi(
            key = item.transferId,
            name = item.storedName ?: item.name,
            kindLabel = if (item.kind == BobTransferKind.Image) "Image" else "File",
            directionLabel = if (item.direction == com.bob.android.transfer.OnlineTransferDirection.Outgoing) {
                "Sent"
            } else {
                "Received"
            },
            statusLabel = status,
            progressLabel = progress,
            previewUri = item.localContentUri
                ?.takeIf { item.kind == BobTransferKind.Image }
                ?.toString(),
            errorMessage = item.errorMessage,
        )
    }

    private fun formatBytes(bytes: Long): String = when {
        bytes >= 1024L * 1024L * 1024L -> "%.1f GiB".format(bytes / (1024.0 * 1024.0 * 1024.0))
        bytes >= 1024L * 1024L -> "%.1f MiB".format(bytes / (1024.0 * 1024.0))
        bytes >= 1024L -> "%.1f KiB".format(bytes / 1024.0)
        else -> "$bytes B"
    }

    private fun scheduleBackgroundDisconnect() {
        backgroundDisconnectJob?.cancel()
        if (externalContentSelectionDepth > 0) return
        backgroundDisconnectJob = viewModelScope.launch {
            // Avoid tearing down a healthy session during a short configuration change.
            delay(BACKGROUND_DISCONNECT_DELAY_MILLIS)
            if (!appVisible && externalContentSelectionDepth == 0) {
                connection.disconnect("App moved to the background. Local network connection paused.")
                if (automaticConnectionsEnabled) lastAutomaticEndpointSignature = null
            }
        }
    }

    private fun resolveEndpoints(
        target: SelectedTarget,
        peers: List<DiscoveredComputer>,
    ): List<BobEndpoint>? = when (target) {
        is SelectedTarget.Discovered -> peers.firstOrNull {
            it.serviceName == target.key && it.isCompatible
        }?.toEndpoints()
        is SelectedTarget.Manual -> listOf(BobEndpoint(host = target.address))
    }

    private fun DiscoveredComputer.toEndpoints(): List<BobEndpoint> = hosts.map { host ->
        BobEndpoint(
            host = host,
            port = port,
            apiPath = apiPath,
            expectedServerId = UUID.fromString(deviceId),
            displayNameHint = displayName,
            socketFactory = network?.socketFactory,
        )
    }

    private fun targetLabel(target: SelectedTarget, peers: List<DiscoveredComputer>): String =
        when (target) {
            is SelectedTarget.Discovered -> peers.firstOrNull { it.serviceName == target.key }
                ?.let { "${it.displayName} (${endpoint(it.preferredHost, it.port)})" }
                ?: "Offline computer"
            is SelectedTarget.Manual -> endpoint(target.address, DEFAULT_PORT)
        }

    private fun updateSelectedTarget(nextTarget: SelectedTarget?) {
        val nextIdentity = nextTarget?.draftIdentity()
        if (draftText.value.isNotEmpty() && nextIdentity != null) {
            when (val owner = draftTargetIdentity) {
                null -> {
                    draftTargetIdentity = nextIdentity
                    draftRevision += 1
                }
                nextIdentity -> Unit
                else -> clearDraft()
            }
        }
        selectedTarget.value = nextTarget
    }

    private fun clearDraft() {
        draftRevision += 1
        draftText.value = ""
        draftTargetIdentity = null
    }

    private fun SelectedTarget.draftIdentity(): String = when (this) {
        is SelectedTarget.Discovered -> "device:${deviceId.lowercase()}"
        is SelectedTarget.Manual -> "manual:${address.lowercase()}"
    }

    private fun endpoint(host: String, port: Int): String =
        if (':' in host && !host.startsWith("[")) "[$host]:$port" else "$host:$port"

    private fun String.preview(): Pair<String, Boolean> {
        if (length <= MAX_TIMELINE_PREVIEW_CHARS) return this to false
        var end = MAX_TIMELINE_PREVIEW_CHARS
        if (end > 0 && this[end - 1].isHighSurrogate()) end -= 1
        return substring(0, end) + "…" to true
    }

    override fun onCleared() {
        backgroundDisconnectJob?.cancel()
        automaticConnectJob?.cancel()
        connection.close()
        discovery.close()
        super.onCleared()
    }

    private data class BaseInputs(
        val snapshot: DiscoverySnapshot,
        val selection: SelectedTarget?,
        val manualIp: String,
        val notice: String?,
        val connectionState: BobConnectionState,
    )

    private data class ConnectionPresentation(
        val phase: ConnectionUiPhase,
        val label: String,
        val detail: String,
    )

    private sealed interface SelectedTarget {
        data class Discovered(
            val key: String,
            val deviceId: String,
            val automatic: Boolean,
        ) : SelectedTarget

        data class Manual(val address: String) : SelectedTarget
    }

    private companion object {
        const val DEFAULT_PORT = 42424
        const val MAX_MANUAL_IP_LENGTH = 64
        const val MAX_DRAFT_CHARACTERS = 300_000
        const val MAX_TIMELINE_PREVIEW_CHARS = 1_200
        const val RECENT_MESSAGE_LIMIT = 20
        const val BACKGROUND_DISCONNECT_DELAY_MILLIS = 2_000L
        const val AUTOMATIC_CONNECT_SETTLE_MILLIS = 1_200L
        val TIME_FORMATTER: DateTimeFormatter = DateTimeFormatter
            .ofPattern("MM-dd HH:mm")
            .withZone(ZoneId.systemDefault())
    }
}
