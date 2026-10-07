package com.bob.android.ui

import android.app.Application
import android.net.InetAddresses
import android.net.Uri
import androidx.compose.foundation.text.input.InputTransformation
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.snapshotFlow
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.bob.android.background.BobTransferRuntime
import com.bob.android.application.BobConnectionPhase
import com.bob.android.application.BobConnectionState
import com.bob.android.application.BobContentSelection
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
import com.bob.android.persistence.SavedConnectionPreference
import com.bob.android.persistence.SavedConnectionPreferences
import com.bob.android.persistence.SavedManualEndpoint
import com.bob.android.security.BobTrustStore
import com.bob.android.transfer.OnlineTransferItem
import com.bob.android.transfer.OnlineTransferState
import com.bob.android.transfer.OnlineTransferDirection
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID
import java.util.Locale
import kotlinx.coroutines.CancellationException
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
    private val runtime = BobTransferRuntime.get(application)
    private val connection = runtime.connection
    private val connectionPreferences = SavedConnectionPreferences(application)
    private val trustStore = BobTrustStore(application)
    private var preferenceReadFailed = false
    private var savedPreference = try {
        connectionPreferences.read()
    } catch (_: Exception) {
        preferenceReadFailed = true
        SavedConnectionPreference(autoConnect = false)
    }
    private val selectedTarget = MutableStateFlow<SelectedTarget?>(savedPreference.manualEndpoint
        ?.takeIf { savedPreference.preferManual }
        ?.let { SelectedTarget.Manual(it.address, it.serverId) })
    val manualIpInput = TextFieldState(initialText = savedPreference.manualEndpoint?.address.orEmpty())
    private val notice = MutableStateFlow<String?>(if (preferenceReadFailed)
        "Could not read saved connection preferences. Automatic connection was paused." else null)
    val draftInput = TextFieldState()
    private val isQueueingText = MutableStateFlow(false)
    private var automaticConnectionsEnabled = savedPreference.autoConnect && !savedPreference.preferManual
    private var manualReconnectEnabled = savedPreference.autoConnect && savedPreference.preferManual
    private var userWantsConnection = savedPreference.autoConnect
    private var lastAutomaticEndpointSignature: String? = null
    private var appVisible = false
    private var backgroundDisconnectJob: Job? = null
    private var automaticConnectJob: Job? = null
    private var draftTargetIdentity: String? = null
    private var draftRevision = 0L
    private var externalContentSelectionDepth = 0
    private var pendingContentSelection: PendingContentSelection? = null

    // Keep text, selection and IME composition in one synchronous editor state.
    // The screen flow observes it, but never feeds older text back into the editor.
    val manualIpTransformation = InputTransformation {
        if (length > MAX_MANUAL_IP_LENGTH) replace(MAX_MANUAL_IP_LENGTH, length, "")
        notice.value = null
    }
    val draftTransformation = InputTransformation {
        if (length > MAX_DRAFT_CHARACTERS) replace(MAX_DRAFT_CHARACTERS, length, "")
        updateDraftOwnership(asCharSequence().toString())
    }

    private val baseInputs = combine(
        discovery.snapshot,
        selectedTarget,
        snapshotFlow { manualIpInput.text.toString() },
        notice,
        connection.state,
    ) { snapshot, selection, manualIpValue, noticeValue, connectionState ->
        BaseInputs(snapshot, selection, manualIpValue, noticeValue, connectionState)
    }

    val uiState: StateFlow<BobUiState> = combine(
        baseInputs,
        snapshotFlow { draftInput.text.toString() },
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
            connection.state.collect { state ->
                rememberSuccessfulManualConnection(state)
                // A selected session can become idle while the screen stays visible.
                // Restore its verified target only while the user still wants it.
                if (state.phase == BobConnectionPhase.Idle) maybeReconnectSelectedTarget()
            }
        }
        viewModelScope.launch {
            discovery.snapshot.collect { snapshot ->
                reconcileAutomaticSelection(snapshot)
                maybeConnectAutomatically(snapshot)
                maybeReconnectSelectedTarget(snapshot)
            }
        }
    }

    fun startDiscovery() {
        backgroundDisconnectJob?.cancel()
        backgroundDisconnectJob = null
        appVisible = true
        runtime.setVisible(true)
        if (automaticConnectionsEnabled && !connection.state.value.isConnected) {
            lastAutomaticEndpointSignature = null
        }
        discovery.start()
        maybeReconnectSelectedTarget()
        maybeConnectAutomatically(discovery.snapshot.value)
    }

    fun stopDiscovery() {
        appVisible = false
        automaticConnectJob?.cancel()
        automaticConnectJob = null
        discovery.stop()
        scheduleBackgroundDisconnect()
    }

    fun beginExternalContentSelection(kind: BobTransferKind): Boolean {
        if (pendingContentSelection != null) return false
        val authorization = connection.captureContentSelection()
        if (authorization == null) {
            notice.value = "Reconnect before selecting content."
            return false
        }
        pendingContentSelection = PendingContentSelection(authorization, kind, selectedTarget.value?.draftIdentity())
        externalContentSelectionDepth += 1
        runtime.setVisible(true)
        backgroundDisconnectJob?.cancel()
        backgroundDisconnectJob = null
        return true
    }

    fun endExternalContentSelection() {
        if (pendingContentSelection == null) return
        pendingContentSelection = null
        releaseContentSelectionLease()
    }

    private fun releaseContentSelectionLease() {
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
        if (!savePreference(savedPreference.copy(preferManual = false, autoConnect = true))) return
        automaticConnectionsEnabled = false
        manualReconnectEnabled = false
        userWantsConnection = true
        automaticConnectJob?.cancel()
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

    fun useManualIp() {
        val address = manualIpInput.text.toString().trim()
        if (address.isEmpty() || !InetAddresses.isNumericAddress(address)) {
            notice.value = "Enter a valid IPv4 or IPv6 address."
            return
        }

        val knownId = savedPreference.manualEndpoint?.takeIf { it.address.equals(address, ignoreCase = true) }?.serverId
            ?: (selectedTarget.value as? SelectedTarget.Manual)
                ?.takeIf { it.address.equals(address, ignoreCase = true) }?.serverId
        val target = SelectedTarget.Manual(address, knownId)
        if (!prepareExplicitConnection(target)) return
        updateSelectedTarget(target)
        notice.value = null
        connection.connect(requireNotNull(resolveEndpoints(target, emptyList())))
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
        if (!prepareExplicitConnection(target)) return
        notice.value = null
        connection.connect(resolved)
    }

    fun disconnect() {
        automaticConnectionsEnabled = false
        manualReconnectEnabled = false
        userWantsConnection = false
        automaticConnectJob?.cancel()
        automaticConnectJob = null
        connection.disconnect("Disconnected by you.")
        if (!savePreference(savedPreference.copy(autoConnect = false))) {
            notice.value = "Disconnected. The preference could not be saved, so automatic connection may return after a restart. Restore storage and tap Disconnect again."
        }
    }

    fun resetTrust() {
        automaticConnectionsEnabled = false
        manualReconnectEnabled = false
        userWantsConnection = false
        automaticConnectJob?.cancel()
        automaticConnectJob = null
        savePreference(savedPreference.copy(autoConnect = false))
        connection.resetBlockedTrust()
    }

    private fun savePreference(value: SavedConnectionPreference): Boolean = try {
        if (preferenceReadFailed || value != savedPreference) connectionPreferences.write(value)
        savedPreference = value
        preferenceReadFailed = false
        true
    } catch (_: Exception) {
        automaticConnectionsEnabled = false
        manualReconnectEnabled = false
        userWantsConnection = false
        notice.value = "Could not save connection settings. Automatic connection is paused for this session. Restore storage and try again."
        false
    }

    private fun prepareExplicitConnection(target: SelectedTarget): Boolean {
        val next = when (target) {
            is SelectedTarget.Discovered -> savedPreference.copy(preferManual = false, autoConnect = true)
            is SelectedTarget.Manual -> target.serverId?.let { authenticatedId ->
                savedPreference.copy(manualEndpoint = SavedManualEndpoint(target.address, authenticatedId),
                    preferManual = true, autoConnect = true)
            } ?: savedPreference.copy(preferManual = false, autoConnect = false)
        }
        // A new address is not remembered until WSS completes. Pause the older saved
        // target first, so a failed attempt cannot reconnect to a different computer.
        if (!savePreference(next)) return false
        automaticConnectionsEnabled = false
        manualReconnectEnabled = target is SelectedTarget.Manual && target.serverId != null
        userWantsConnection = true
        automaticConnectJob?.cancel()
        automaticConnectJob = null
        return true
    }

    private fun rememberSuccessfulManualConnection(state: BobConnectionState) {
        if (!state.isConnected || !userWantsConnection) return
        val target = selectedTarget.value as? SelectedTarget.Manual ?: return
        val authenticatedId = state.serverId ?: return
        if (!state.endpoint?.host.equals(target.address, ignoreCase = true) ||
            (target.serverId != null && target.serverId != authenticatedId)) return
        updateSelectedTarget(target.copy(serverId = authenticatedId))
        val saved = savedPreference.copy(manualEndpoint = SavedManualEndpoint(target.address, authenticatedId),
            preferManual = true, autoConnect = true)
        manualReconnectEnabled = savePreference(saved)
    }

    private fun maybeReconnectSelectedTarget(snapshot: DiscoverySnapshot = discovery.snapshot.value) {
        if (!appVisible || !userWantsConnection) return
        // The connection loop already owns active/retrying sessions. Security and
        // storage failures remain visible until the user explicitly reconnects.
        if (connection.state.value.phase != BobConnectionPhase.Idle) return
        val endpoints = when (val target = selectedTarget.value) {
            is SelectedTarget.Manual -> {
                if (!manualReconnectEnabled) return
                val expectedId = target.serverId ?: return
                listOf(BobEndpoint(host = target.address, expectedServerId = expectedId))
            }
            is SelectedTarget.Discovered -> {
                if (target.automatic) return
                val peer = snapshot.devices.firstOrNull {
                    it.serviceName == target.key && it.deviceId == target.deviceId && it.isCompatible
                } ?: return
                peer.toEndpoints()
            }
            null -> return
        }
        val expectedId = endpoints.firstOrNull()?.expectedServerId ?: return
        val hasSavedTrust = try {
            trustStore.find(expectedId) != null
        } catch (_: Exception) {
            notice.value = "Could not read saved certificate trust. Automatic connection was paused."
            manualReconnectEnabled = false
            userWantsConnection = false
            return
        }
        if (!hasSavedTrust) {
            manualReconnectEnabled = false
            userWantsConnection = false
            notice.value = "Saved certificate trust is missing. Tap Connect to verify this computer again."
            return
        }
        connection.connect(endpoints)
    }

    private fun updateDraftOwnership(nextDraft: String) {
        draftRevision += 1
        if (nextDraft.isEmpty()) {
            draftTargetIdentity = null
        } else if (draftInput.text.isEmpty() || draftTargetIdentity == null) {
            draftTargetIdentity = selectedTarget.value?.draftIdentity()
        }
        notice.value = null
    }

    fun sendText() {
        val text = draftInput.text.toString()
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

    fun cancelTransfer(key: String) {
        runCatching { UUID.fromString(key) }.getOrNull()?.let(connection::cancelTransfer)
    }

    fun retryTransfer(key: String) {
        if (!connection.state.value.isConnected) {
            notice.value = "Reconnect before retrying this file."
            return
        }
        runCatching { UUID.fromString(key) }.getOrNull()?.let(connection::retryTransfer)
    }

    private fun queueContent(uris: List<Uri>, kind: BobTransferKind) {
        val selection = pendingContentSelection
        pendingContentSelection = null
        if (selection == null) {
            if (uris.isNotEmpty()) notice.value = "The computer selection expired. Reconnect and select the content again."
            return
        }
        if (selection.kind != kind || uris.isEmpty()) {
            if (selection.kind != kind) notice.value = "The content selection expired. Please select it again."
            releaseContentSelectionLease()
            return
        }
        val selectedUris = uris.toList()
        viewModelScope.launch {
            try {
                connection.queueTransfers(selectedUris, kind, selection.authorization)
                if (selection.targetIdentity != selectedTarget.value?.draftIdentity()) {
                    notice.value = "Selected content was kept with its original computer. Reconnect to that computer to send it."
                }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                notice.value = "Could not save all selected content. Check the file history before selecting again."
            } finally {
                releaseContentSelectionLease()
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
                    // Discovery briefly empties when the screen stops. Keep an
                    // explicitly chosen identity until that same peer resolves.
                    !current.automatic -> current
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
        if (!appVisible || !automaticConnectionsEnabled) return
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
            if (settled?.serviceName != peer.serviceName || !appVisible || !automaticConnectionsEnabled) return@launch
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
            ?: when (selection) {
                is SelectedTarget.Discovered -> selection.deviceId
                is SelectedTarget.Manual -> selection.serverId?.toString()
                null -> null
            }
        val timeline = if (activeServerId != null) {
            messages
                .filter { it.serverId == activeServerId }
                .asReversed()
                .take(RECENT_MESSAGE_LIMIT)
                .map(::toTimelineItem)
        } else emptyList()
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
            transfers = transfers.filter { activeServerId != null && it.serverId == activeServerId }
                .map(::toTransferTimelineItem),
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
            recordedAtEpochMillis = message.localReceivedAt.toEpochMilli(),
        )
    }

    private fun toTransferTimelineItem(item: OnlineTransferItem): TransferTimelineItemUi {
        val status = if (item.isSendingCancellation) "Canceling" else if (
            item.state == OnlineTransferState.Canceled && !item.retryable
        ) "Cancel pending" else when (item.state) {
            OnlineTransferState.Queued -> "Queued"
            OnlineTransferState.Offered -> "Waiting for computer"
            OnlineTransferState.Accepted -> "Accepted"
            OnlineTransferState.Transferring -> "Transferring"
            OnlineTransferState.Verifying -> "Verifying"
            OnlineTransferState.Completed -> "Completed"
            OnlineTransferState.Failed -> "Failed"
            OnlineTransferState.Canceled -> "Canceled"
        }
        val progress = item.size?.takeIf { it > 0L }?.let { total ->
            val percent = (item.bytesTransferred.toDouble() / total.toDouble() * 100.0)
                .toInt().coerceIn(0, 100)
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
            isOutgoing = item.direction == OnlineTransferDirection.Outgoing,
            recordedAtEpochMillis = item.recordedAt.toEpochMilli(),
            timeLabel = TIME_FORMATTER.format(item.recordedAt),
            bytesTransferred = item.bytesTransferred,
            totalBytes = item.size,
            isActive = item.isSendingCancellation || item.state in setOf(OnlineTransferState.Offered, OnlineTransferState.Accepted,
                OnlineTransferState.Transferring, OnlineTransferState.Verifying),
            canCancel = item.state !in setOf(OnlineTransferState.Completed, OnlineTransferState.Failed,
                OnlineTransferState.Canceled),
            canRetry = item.retryable && item.direction == OnlineTransferDirection.Outgoing &&
                item.localContentUri != null && item.state in setOf(OnlineTransferState.Failed, OnlineTransferState.Canceled),
        )
    }

    private fun formatBytes(bytes: Long): String = when {
        bytes >= (1L shl 60) -> "%.1f EiB".format(Locale.ROOT, bytes / (1L shl 60).toDouble())
        bytes >= (1L shl 40) -> "%.1f TiB".format(Locale.ROOT, bytes / (1L shl 40).toDouble())
        bytes >= 1024L * 1024L * 1024L -> "%.1f GiB".format(Locale.ROOT, bytes / (1024.0 * 1024.0 * 1024.0))
        bytes >= 1024L * 1024L -> "%.1f MiB".format(Locale.ROOT, bytes / (1024.0 * 1024.0))
        bytes >= 1024L -> "%.1f KiB".format(Locale.ROOT, bytes / 1024.0)
        else -> "$bytes B"
    }

    private fun scheduleBackgroundDisconnect() {
        backgroundDisconnectJob?.cancel()
        if (externalContentSelectionDepth > 0) return
        backgroundDisconnectJob = viewModelScope.launch {
            // Avoid tearing down a healthy session during a short configuration change.
            delay(BACKGROUND_DISCONNECT_DELAY_MILLIS)
            if (!appVisible && externalContentSelectionDepth == 0) {
                runtime.setVisible(false)
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
        is SelectedTarget.Manual -> listOf(BobEndpoint(host = target.address, expectedServerId = target.serverId))
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
        if (draftInput.text.isNotEmpty() && nextIdentity != null) {
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
        draftInput.edit { replace(0, length, "") }
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
        runtime.setVisible(false)
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

    private data class PendingContentSelection(
        val authorization: BobContentSelection,
        val kind: BobTransferKind,
        val targetIdentity: String?,
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

        data class Manual(val address: String, val serverId: UUID? = null) : SelectedTarget
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
