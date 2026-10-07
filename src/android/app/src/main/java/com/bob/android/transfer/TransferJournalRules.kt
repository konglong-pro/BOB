package com.bob.android.transfer

import com.bob.android.network.BobTransferKind
import com.bob.android.network.BobTransferFailed
import java.net.URI
import java.util.Base64
import java.util.UUID

/** Checked for the whole journal before any MediaStore mutation or journal rewrite. */
internal data class TransferJournalFacts(
    val id: UUID,
    val serverId: UUID,
    val direction: OnlineTransferDirection,
    val kind: BobTransferKind,
    val name: String,
    val mediaType: String,
    val size: Long?,
    val bytes: Long,
    val state: OnlineTransferState,
    val storedName: String?,
    val sourceUri: String?,
    val pendingUri: String?,
    val localUri: String?,
    val retryOf: UUID?,
    val localBytes: Long?,
    val localSha256: String?,
    val digestAlgorithm: String?,
    val digestBytes: Long?,
    val digestValue: String?,
    val hasVerifyingSince: Boolean,
    val hasTerminalAt: Boolean,
    val hasPublicationAt: Boolean,
    val terminalAckPending: Boolean,
    val cancelPending: Boolean,
    val error: String?,
    val failureCode: String?,
    val failureIsLocal: Boolean,
    val hasRemoteFailure: Boolean,
)

internal fun validateTransferJournal(records: List<TransferJournalFacts>) {
    val ids = HashSet<UUID>()
    val activeDirections = HashSet<Pair<UUID, OnlineTransferDirection>>()
    records.forEach { record ->
        require(ids.add(record.id)) { "Duplicate transfer ID in saved history." }
        require(record.id != UUID(0, 0) && record.serverId != UUID(0, 0)) { "Empty transfer identity." }
        require(record.retryOf != record.id) { "A transfer cannot retry itself." }
        require(record.name.isNotBlank() && record.mediaType.isNotBlank()) { "Missing transfer metadata." }
        require(record.size == null || record.size >= 0L) { "Negative transfer size." }
        require(record.bytes >= 0L && (record.size == null || record.bytes <= record.size)) { "Invalid byte count." }
        require((record.localBytes == null) == (record.localSha256 == null)) { "Incomplete local digest." }
        record.localBytes?.let {
            require(it >= 0L && it == record.bytes && (record.size == null || it == record.size)) {
                "Local digest byte count conflicts with metadata."
            }
        }
        record.localSha256?.let { require(isTransferSha256(it)) { "Invalid local SHA-256." } }
        val hasDigest = record.digestAlgorithm != null
        require(hasDigest == (record.digestBytes != null) && hasDigest == (record.digestValue != null)) {
            "Incomplete peer digest."
        }
        if (hasDigest) {
            require(record.direction == OnlineTransferDirection.Incoming && record.digestAlgorithm == "sha-256" &&
                record.digestBytes!! >= 0L && isTransferSha256(record.digestValue!!)) { "Invalid peer digest." }
        }
        record.pendingUri?.let {
            require(record.direction == OnlineTransferDirection.Incoming && isBobMediaUri(it, record.kind)) {
                "Pending destination is outside BOB MediaStore collections."
            }
        }
        if (record.direction == OnlineTransferDirection.Incoming) {
            require(record.sourceUri == null) { "Incoming transfer has a source URI." }
            record.localUri?.let {
                require(record.state == OnlineTransferState.Completed && isBobMediaUri(it, record.kind)) {
                    "Invalid published destination."
                }
            }
        } else {
            require(record.pendingUri == null) { "Outgoing transfer has a pending destination." }
        }
        val terminal = record.state in setOf(OnlineTransferState.Completed, OnlineTransferState.Failed, OnlineTransferState.Canceled)
        require(terminal == record.hasTerminalAt) { "Terminal timestamp conflicts with state." }
        require(!record.terminalAckPending || terminal) { "Nonterminal acknowledgement flag." }
        require(!record.cancelPending || (record.state == OnlineTransferState.Canceled && !record.terminalAckPending)) {
            "Invalid pending cancellation."
        }
        require(!record.failureIsLocal || record.state == OnlineTransferState.Failed) { "Invalid failure origin." }
        require(!record.hasRemoteFailure || record.state == OnlineTransferState.Failed) { "Unexpected peer failure." }
        require(!record.hasPublicationAt || (record.direction == OnlineTransferDirection.Incoming &&
            record.state in setOf(OnlineTransferState.Verifying, OnlineTransferState.Completed) && hasDigest &&
            record.digestBytes == record.localBytes && record.digestValue == record.localSha256)) {
            "Invalid publication intent."
        }
        when (record.state) {
            OnlineTransferState.Verifying -> {
                require(record.localBytes != null && record.hasVerifyingSince && record.storedName != null) {
                    "Verification is missing completed body metadata."
                }
                require(if (record.direction == OnlineTransferDirection.Incoming) record.pendingUri != null
                    else record.sourceUri != null) { "Verification is missing its content." }
            }
            OnlineTransferState.Completed -> {
                require(record.localBytes != null && record.storedName != null && record.pendingUri == null) {
                    "Completed transfer is missing its immutable payload."
                }
            }
            OnlineTransferState.Failed -> require(!record.failureCode.isNullOrBlank() && !record.error.isNullOrBlank()) {
                "Failed transfer is missing its immutable payload."
            }
            OnlineTransferState.Canceled -> require(record.failureCode == "canceled" && record.error == null) {
                "Canceled transfer has conflicting failure data."
            }
            else -> require(record.localBytes == null && !record.hasVerifyingSince) {
                "Unfinished body has verification metadata."
            }
        }
        if (record.state != OnlineTransferState.Failed && record.state != OnlineTransferState.Canceled) {
            require(record.failureCode == null && record.error == null) { "State has conflicting failure data." }
        }
        if (!terminal && record.state != OnlineTransferState.Queued) {
            require(activeDirections.add(record.serverId to record.direction)) { "Multiple active transfers in one direction." }
        }
    }
}

internal fun isTransferSha256(value: String): Boolean = runCatching {
    val bytes = Base64.getUrlDecoder().decode(value)
    value.length == 43 && bytes.size == 32 &&
        Base64.getUrlEncoder().withoutPadding().encodeToString(bytes) == value
}.getOrDefault(false)

internal fun isBobMediaUri(value: String, kind: BobTransferKind): Boolean = runCatching {
    val uri = URI(value)
    val collection = if (kind == BobTransferKind.Image) "images/media" else "downloads"
    val prefix = "/external_primary/$collection/"
    val id = uri.path?.takeIf { it.startsWith(prefix) }?.removePrefix(prefix)
    uri.scheme == "content" && uri.rawAuthority == "media" && uri.rawQuery == null && uri.rawFragment == null &&
        id != null && id.matches(Regex("[1-9][0-9]*")) && id.toLongOrNull() != null &&
        uri.rawPath == uri.path
}.getOrDefault(false)

internal fun isOwnedBobMediaRow(kind: BobTransferKind, relativePath: String?, owner: String?, packageName: String): Boolean {
    val path = if (kind == BobTransferKind.Image) "Pictures/BOB/" else "Download/BOB/"
    return relativePath == path && owner == packageName
}

internal fun canReceiveCompleted(state: OnlineTransferState): Boolean = state in setOf(
    OnlineTransferState.Verifying, OnlineTransferState.Canceled, OnlineTransferState.Completed,
)

internal fun rememberPeerFailure(
    failureIsLocal: Boolean,
    recordedPeer: BobTransferFailed?,
    original: BobTransferFailed,
    incoming: BobTransferFailed,
): BobTransferFailed {
    val previous = recordedPeer ?: if (!failureIsLocal) original else null
    if (previous != null && (previous.transferId != incoming.transferId || previous.code != incoming.code ||
            previous.message != incoming.message || previous.retryable != incoming.retryable)) {
        throw TransferIoException("invalid_metadata", "Conflicting repeated peer failure.")
    }
    return incoming
}
