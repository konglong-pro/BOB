package com.bob.android.transfer

import com.bob.android.application.BobConnectionPhase
import com.bob.android.background.QUEUED_RECONNECT_WAIT_MILLIS
import com.bob.android.background.isActiveTransfer
import com.bob.android.background.shouldKeepStartedTransferService
import com.bob.android.network.BobTransferFailed
import com.bob.android.network.BobTransferKind
import java.io.ByteArrayInputStream
import java.io.IOException
import java.io.InputStream
import java.security.MessageDigest
import java.time.Instant
import java.util.Base64
import java.util.UUID
import java.util.concurrent.CancellationException

/** Executed by streamingChecks without a test framework or new dependency. */
fun main() {
    val checks = listOf(
        "zero-byte known and unknown lengths" to ::checkEmpty,
        "known and chunked streams retain the same SHA-256" to ::checkDigest,
        "short content cannot succeed" to { expectCode("size_mismatch") {
            copyTransferStream(ByteArrayInputStream(byteArrayOf(1, 2)), 3L, { _, _ -> })
        } },
        "oversized content is rejected before writing the excess chunk" to ::checkOversized,
        "cancellation stops reading and writing" to ::checkCancellation,
        "local cancel waits for canonical control in both content directions" to ::checkSenderCancelOrdering,
        "local cancel has a fixed 60 second bound despite byte progress in either direction" to ::checkSenderCancelDeadline,
        "destination failure is preserved" to ::checkDestinationFailure,
        "broken zero-byte reads do not spin forever" to ::checkEmptyReads,
        "file names preserve extensions and Unicode boundaries" to ::checkFileNames,
        "digest coordination waits the contracted 24 hours" to {
            check(TRANSFER_DIGEST_WAIT_MILLIS == 86_400_000L)
        },
        "saved history rejects duplicate identities, corrupt counters, hashes and states" to ::checkJournalValidation,
        "pending cleanup accepts only BOB collection and application ownership" to ::checkMediaDestinationRules,
        "simultaneous local failures retain immutable peer payloads" to ::checkPeerFailures,
        "completed cannot override a failed transfer" to ::checkTerminalRules,
        "an already-started background batch waits at most 60 seconds in both directions" to ::checkForegroundGrace,
        "2 GiB plus 17 bytes uses int64 counters and a bounded buffer" to ::checkLargeStream,
    )
    for ((name, verify) in checks) {
        try { verify() } catch (error: Throwable) { throw AssertionError(name, error) }
        println("PASS " + name)
    }
    println("Android streaming checks passed: " + checks.size)
}

private fun validJournalFacts(): TransferJournalFacts = TransferJournalFacts(
    UUID.randomUUID(), UUID.randomUUID(), OnlineTransferDirection.Incoming, BobTransferKind.File,
    "sample.txt", "text/plain", 0L, 0L, OnlineTransferState.Verifying, "sample.txt", null,
    "content://media/external_primary/downloads/15", null, null, 0L,
    "47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU", "sha-256", 0L,
    "47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU", true, false, false,
    false, false, null, null, false, false,
)

private fun checkJournalValidation() {
    val valid = validJournalFacts()
    validateTransferJournal(listOf(valid))
    validateTransferJournal(listOf(valid.copy(hasPublicationAt = true)))
    val malformed = listOf(
        valid.copy(id = UUID(0, 0)), valid.copy(serverId = UUID(0, 0)), valid.copy(retryOf = valid.id),
        valid.copy(size = -1L), valid.copy(bytes = -1L), valid.copy(size = 0L, bytes = 1L),
        valid.copy(localBytes = null), valid.copy(localSha256 = null), valid.copy(localBytes = -1L),
        valid.copy(localSha256 = "invalid"), valid.copy(localSha256 = valid.localSha256 + "="),
        valid.copy(digestAlgorithm = "md5"), valid.copy(digestBytes = -1L), valid.copy(digestValue = "invalid"),
        valid.copy(pendingUri = null), valid.copy(hasVerifyingSince = false), valid.copy(storedName = null),
        valid.copy(hasTerminalAt = true), valid.copy(terminalAckPending = true), valid.copy(cancelPending = true),
        valid.copy(state = OnlineTransferState.Completed), valid.copy(state = OnlineTransferState.Failed, hasTerminalAt = true),
        valid.copy(state = OnlineTransferState.Canceled, hasTerminalAt = true), valid.copy(failureIsLocal = true),
        valid.copy(hasRemoteFailure = true), valid.copy(error = "Unexpected error"),
        valid.copy(hasPublicationAt = true, digestValue = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
        valid.copy(localUri = "content://media/external_primary/downloads/15"),
        valid.copy(pendingUri = "content://media/external_primary/images/media/15"),
    )
    malformed.forEach { bad ->
        check(runCatching { validateTransferJournal(listOf(valid.copy(serverId = UUID.randomUUID(), id = UUID.randomUUID()), bad)) }
            .exceptionOrNull() is IllegalArgumentException)
    }
    check(runCatching { validateTransferJournal(listOf(valid, valid.copy(serverId = UUID.randomUUID()))) }
        .exceptionOrNull() is IllegalArgumentException)
    check(runCatching { validateTransferJournal(listOf(valid, valid.copy(id = UUID.randomUUID()))) }
        .exceptionOrNull() is IllegalArgumentException)
    val canceled = valid.copy(state = OnlineTransferState.Canceled, hasTerminalAt = true,
        failureCode = "canceled", cancelPending = true)
    validateTransferJournal(listOf(canceled))
    val failed = valid.copy(state = OnlineTransferState.Failed, hasTerminalAt = true,
        failureCode = "network_interrupted", error = "Interrupted", failureIsLocal = true, hasRemoteFailure = true)
    validateTransferJournal(listOf(failed))
}

private fun checkMediaDestinationRules() {
    check(isBobMediaUri("content://media/external_primary/downloads/15", BobTransferKind.File))
    check(isBobMediaUri("content://media/external_primary/images/media/42", BobTransferKind.Image))
    listOf("content://other/external_primary/downloads/15", "file:///Download/BOB/sample.txt",
        "content://media/external_primary/downloads", "content://media/external_primary/downloads/0",
        "content://media/external_primary/downloads/15/extra", "content://media/external_primary/downloads/15?x=1",
        "content://media/external_primary/downloads/15#fragment", "content://media/external_primary/downloads/%31%35",
        "content://media/external_primary/downloads/9223372036854775808")
        .forEach { check(!isBobMediaUri(it, BobTransferKind.File)) }
    check(!isBobMediaUri("content://media/external_primary/images/media/42", BobTransferKind.File))
    check(isOwnedBobMediaRow(BobTransferKind.File, "Download/BOB/", "com.bob.android", "com.bob.android"))
    check(!isOwnedBobMediaRow(BobTransferKind.File, "Download/", "com.bob.android", "com.bob.android"))
    check(!isOwnedBobMediaRow(BobTransferKind.File, "Download/BOB/", "another.app", "com.bob.android"))
    check(!isOwnedBobMediaRow(BobTransferKind.Image, "Download/BOB/", "com.bob.android", "com.bob.android"))
}

private fun checkPeerFailures() {
    val id = UUID.randomUUID()
    val local = BobTransferFailed(UUID.randomUUID(), id, "network_interrupted", "Phone stream stopped", true)
    val peer = BobTransferFailed(UUID.randomUUID(), id, "io_error", "Computer stream stopped", true)
    check(rememberPeerFailure(true, null, local, peer) === peer)
    check(local.code == "network_interrupted" && local.message == "Phone stream stopped")
    val duplicate = peer.copy(envelopeId = UUID.randomUUID())
    check(rememberPeerFailure(true, peer, local, duplicate) === duplicate)
    listOf(peer.copy(code = "source_missing"), peer.copy(message = "Different failure"),
        peer.copy(retryable = false), peer.copy(transferId = UUID.randomUUID())).forEach { changed ->
        expectCode("invalid_metadata") { rememberPeerFailure(true, peer, local, changed) }
    }
    expectCode("invalid_metadata") { rememberPeerFailure(false, null, peer, local) }
    check(rememberPeerFailure(false, null, peer, duplicate) === duplicate)
}

private fun checkTerminalRules() {
    check(!canReceiveCompleted(OnlineTransferState.Failed))
    check(!canReceiveCompleted(OnlineTransferState.Transferring))
    check(canReceiveCompleted(OnlineTransferState.Verifying))
    check(canReceiveCompleted(OnlineTransferState.Completed))
    check(canReceiveCompleted(OnlineTransferState.Canceled))
}

private fun checkForegroundGrace() {
    check(QUEUED_RECONNECT_WAIT_MILLIS == 60_000L)
    BobConnectionPhase.entries.forEach { phase ->
        check(!shouldKeepStartedTransferService(false, phase, null))
        check(!shouldKeepStartedTransferService(true, phase, 60_000L))
    }
    listOf(BobConnectionPhase.Connected, BobConnectionPhase.Retrying, BobConnectionPhase.Securing,
        BobConnectionPhase.OpeningSession, BobConnectionPhase.Synchronizing).forEach { phase ->
        check(shouldKeepStartedTransferService(true, phase, null))
        check(shouldKeepStartedTransferService(true, phase, 59_999L))
        check(!shouldKeepStartedTransferService(true, phase, -1L))
    }
    listOf(BobConnectionPhase.Idle, BobConnectionPhase.Blocked, BobConnectionPhase.Failed).forEach { phase ->
        check(!shouldKeepStartedTransferService(true, phase, 0L))
    }
}

private fun checkEmpty() {
    val expected = "47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU"
    listOf<Long?>(0L, null).forEach { size ->
        val result = copyTransferStream(ByteArrayInputStream(byteArrayOf()), size,
            { _, _ -> error("Zero-byte content wrote data.") })
        check(result.bytes == 0L && result.sha256 == expected)
    }
}

private fun checkDigest() {
    val input = ByteArray(200_017) { (it % 251).toByte() }
    val expected = base64(MessageDigest.getInstance("SHA-256").digest(input))
    listOf<Long?>(input.size.toLong(), null).forEach { size ->
        var written = 0L
        val result = copyTransferStream(ByteArrayInputStream(input), size, { buffer, count ->
            check(buffer.size <= 64 * 1024)
            written += count
        })
        check(result.bytes == input.size.toLong() && result.bytes == written && result.sha256 == expected)
    }
}

private fun checkOversized() {
    var written = 0L
    expectCode("size_mismatch") {
        copyTransferStream(ByteArrayInputStream(ByteArray(8)), 4L, { _, count -> written += count })
    }
    check(written == 0L)
}

private fun checkCancellation() {
    var written = 0L
    val failure = runCatching {
        copyTransferStream(ZeroStream(1_000_000L), null, { _, count -> written += count },
            ensureActive = { if (written >= 64 * 1024L) throw CancellationException("Canceled") })
    }.exceptionOrNull()
    check(failure is CancellationException && written == 64 * 1024L)
}

private fun checkSenderCancelOrdering() {
    // Deliver control after additional HTTP chunks, reproducing the ordering that
    // made an early local close look like a network failure to the receiver. This
    // exercises the same body guard and bounded copier used by both HTTP workers.
    OnlineTransferDirection.entries.forEach { direction ->
        val responses = if (direction == OnlineTransferDirection.Outgoing) {
            listOf(OnlineTransferState.Canceled, OnlineTransferState.Failed, OnlineTransferState.Completed)
        } else listOf(OnlineTransferState.Canceled, OnlineTransferState.Failed)
        responses.forEach { canonical -> checkCancelControlOrdering(direction, canonical) }
        val waiting = OnlineTransferItem(UUID.randomUUID().toString(), direction,
            BobTransferKind.File, "cancel.bin", null, "application/octet-stream", OnlineTransferState.Canceled,
            0L, null, null, null, Instant.EPOCH, retryable = false, isSendingCancellation = true)
        check(waiting.isActiveTransfer() && !waiting.retryable)
        check(!waiting.copy(isSendingCancellation = false).isActiveTransfer())
    }
    // A received body and matching digest can finish while cancellation is still
    // awaiting WSS. Neither its saved canceled state nor any pending verifying
    // state may cross the MediaStore publication boundary.
    val bytes = ByteArray(3 * 64 * 1024) { (it % 251).toByte() }
    val window = SenderCancelWindow(0L)
    val result = copyTransferStream(ByteArrayInputStream(bytes), bytes.size.toLong(), { _, _ -> },
        ensureActive = {
            check(canStreamTransferBody(OnlineTransferState.Canceled, true, window, 0L))
        })
    check(result.bytes == bytes.size.toLong())
    check(result.sha256 == base64(MessageDigest.getInstance("SHA-256").digest(bytes)))
    OnlineTransferState.entries.forEach { check(!canPublishIncomingBody(it, true)) }
    check(!canPublishIncomingBody(OnlineTransferState.Canceled, false))
    check(canPublishIncomingBody(OnlineTransferState.Verifying, false))
}

private fun checkCancelControlOrdering(direction: OnlineTransferDirection, canonical: OnlineTransferState) {
        var state = OnlineTransferState.Transferring
        var peerState = OnlineTransferState.Transferring
        var pending = false
        var window: SenderCancelWindow? = null
        var writes = 0
        val events = mutableListOf<String>()
        val source = object : ByteArrayInputStream(ByteArray(6 * 64 * 1024)) {
            override fun close() {
                events += "http_closed"
                if (peerState == OnlineTransferState.Transferring) peerState = OnlineTransferState.Failed
                super.close()
            }
        }
        expectCode("body_terminated") {
            source.use {
                copyTransferStream(it, 6 * 64 * 1024L, { _, _ ->
                    writes += 1
                    if (writes == 1) {
                        state = OnlineTransferState.Canceled
                        pending = true
                        window = SenderCancelWindow(0L)
                        events += "cancel_requested"
                    } else if (writes == 3) {
                        peerState = canonical
                        state = canonical
                        pending = false
                        window!!.close()
                        events += "canonical_received"
                    }
                }, ensureActive = {
                    if (!canStreamTransferBody(state, pending, window, 0L)) {
                        throw TransferIoException("body_terminated", "Canonical response ended the body.")
                    }
                })
            }
        }
        check(writes == 3 && peerState == canonical) { "$direction terminated before canonical control." }
        check(events == listOf("cancel_requested", "canonical_received", "http_closed"))
}

private fun checkSenderCancelDeadline() {
    check(SENDER_CANCEL_WAIT_MILLIS == 60_000L)
    OnlineTransferDirection.entries.forEach(::checkCancelDeadlineForDirection)
}

private fun checkCancelDeadlineForDirection(direction: OnlineTransferDirection) {
    val window = SenderCancelWindow(0L)
    var now = 0L
    var written = 0L
    expectCode("cancel_wait_expired") {
        copyTransferStream(ZeroStream(20 * 64 * 1024L), null, { _, count ->
            written += count
            now += 5_000_000_000L
        }, ensureActive = {
            if (!canStreamTransferBody(OnlineTransferState.Canceled, true, window, now)) {
                throw TransferIoException("cancel_wait_expired", "Cancel response did not arrive.")
            }
        })
    }
    check(now == 60_000_000_000L && written == 12 * 64 * 1024L) { "$direction exceeded the cancel deadline." }
    check(window.remainingMillis(now) == 0L && !window.permitsBody(now))
    check(window.remainingMillis(59_999_000_001L) == 1L)
    window.close()
    check(!window.permitsBody(0L))
    // Only a new session starts a fresh control window; the persisted request ID
    // and pending flag are retained by the coordinator across this replacement.
    val replay = SenderCancelWindow(now)
    check(replay.permitsBody(now) && replay.remainingMillis(now) == 60_000L)
}

private fun checkDestinationFailure() {
    val failure = IOException("Destination failed")
    val actual = runCatching {
        copyTransferStream(ByteArrayInputStream(ByteArray(8)), 8L, { _, _ -> throw failure })
    }.exceptionOrNull()
    check(actual === failure)
}

private fun checkEmptyReads() {
    var reads = 0
    val source = object : InputStream() {
        override fun read(): Int = error("The worker must use its bounded buffer.")
        override fun read(buffer: ByteArray, offset: Int, length: Int): Int { reads += 1; return 0 }
    }
    expectCode("io_error") { copyTransferStream(source, null, { _, _ -> }) }
    check(reads == 16)
}

private fun checkFileNames() {
    check(sanitizeBobFileName("../CON.txt. ") == "..CON.txt")
    check(sanitizeBobFileName("CON.txt") == "_CON.txt")
    check(sanitizeBobFileName("e\u0301.txt") == "é.txt")
    check(sanitizeBobFileName("\u0000/\\<>:\"|?*") == "unnamed")
    val name = sanitizeBobFileName("😀".repeat(100) + ".txt")
    check(name.endsWith(".txt") && name.toByteArray(Charsets.UTF_8).size <= 200)
    check(name.substringBeforeLast('.').codePoints().allMatch { it == 0x1F600 })
    val collision = name.withCollisionSuffix(100)
    check(collision.endsWith(" (100).txt") && collision.toByteArray(Charsets.UTF_8).size <= 200)
    check("😀😀abcdef".truncateUtf8(5) == "😀")
}

private fun checkLargeStream() {
    val size = (2L shl 30) + 17L
    var written = 0L
    val result = copyTransferStream(ZeroStream(size), size, { buffer, count ->
        check(buffer.size == 64 * 1024)
        written += count.toLong()
    })
    val independentDigest = MessageDigest.getInstance("SHA-256")
    val zeros = ByteArray(8 * 1024)
    var remaining = size
    while (remaining > 0L) {
        val count = minOf(remaining, zeros.size.toLong()).toInt()
        independentDigest.update(zeros, 0, count)
        remaining -= count
    }
    check(result.bytes == size && written == size && written > Int.MAX_VALUE)
    check(result.sha256 == base64(independentDigest.digest()))
}

private class ZeroStream(private var remaining: Long) : InputStream() {
    override fun read(): Int = if (remaining-- > 0L) 0 else -1
    override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
        if (remaining <= 0L) return -1
        val count = minOf(remaining, length.toLong()).toInt()
        java.util.Arrays.fill(buffer, offset, offset + count, 0.toByte())
        remaining -= count
        return count
    }
}

private fun expectCode(code: String, operation: () -> Unit) {
    val failure = runCatching(operation).exceptionOrNull()
    check(failure is TransferIoException && failure.code == code) { "Expected failure: " + code }
}

private fun base64(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
