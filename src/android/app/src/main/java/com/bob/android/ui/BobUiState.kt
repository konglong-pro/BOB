package com.bob.android.ui

data class PeerUiModel(
    val key: String,
    val name: String,
    val endpoint: String,
    val isCompatible: Boolean,
    val isSelected: Boolean,
)

enum class ConnectionUiPhase {
    NoTarget,
    Ready,
    Securing,
    Connecting,
    Synchronizing,
    Connected,
    Retrying,
    Blocked,
    Failed,
}

data class TextTimelineItemUi(
    val key: String,
    val text: String,
    val previewText: String,
    val isTruncated: Boolean,
    val directionLabel: String,
    val statusLabel: String,
    val timeLabel: String,
    val isOutgoing: Boolean,
)

data class TransferTimelineItemUi(
    val key: String,
    val name: String,
    val kindLabel: String,
    val directionLabel: String,
    val statusLabel: String,
    val progressLabel: String,
    val previewUri: String?,
    val errorMessage: String?,
)

data class BobUiState(
    val discoveryLabel: String = "Discovery has not started",
    val isSearching: Boolean = false,
    val peers: List<PeerUiModel> = emptyList(),
    val selectionGuidance: String = "Waiting for a BOB computer on the same Wi-Fi",
    val selectedTargetLabel: String? = null,
    val manualIp: String = "",
    val notice: String? = null,
    val canRequestConnection: Boolean = false,
    val connectionPhase: ConnectionUiPhase = ConnectionUiPhase.NoTarget,
    val connectionLabel: String = "No computer selected",
    val connectionDetail: String = "Select a computer to verify HTTPS and its certificate pin.",
    val authenticatedPeerName: String? = null,
    val canDisconnect: Boolean = false,
    val canResetTrust: Boolean = false,
    val draftText: String = "",
    val draftError: String? = null,
    val canSendText: Boolean = false,
    val canChooseContent: Boolean = false,
    val isQueueingText: Boolean = false,
    val timeline: List<TextTimelineItemUi> = emptyList(),
    val transfers: List<TransferTimelineItemUi> = emptyList(),
)
