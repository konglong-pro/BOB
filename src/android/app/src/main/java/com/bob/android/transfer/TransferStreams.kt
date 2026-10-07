package com.bob.android.transfer

import java.io.IOException
import java.io.InputStream
import java.security.MessageDigest
import java.util.Base64

internal data class TransferStreamDigest(val bytes: Long, val sha256: String)

internal const val TRANSFER_DIGEST_WAIT_MILLIS = 24 * 60 * 60 * 1_000L

/** Bounded memory, int64 accounting, and no elapsed-time limit on a moving file. */
internal fun copyTransferStream(
    source: InputStream,
    expectedSize: Long?,
    writeChunk: (ByteArray, Int) -> Unit,
    ensureActive: () -> Unit = {},
    onProgress: (Long) -> Unit = {},
): TransferStreamDigest {
    require(expectedSize == null || expectedSize >= 0L)
    val digest = MessageDigest.getInstance("SHA-256")
    val buffer = ByteArray(64 * 1024)
    var bytes = 0L
    var lastProgressAt = 0L
    var emptyReads = 0
    while (true) {
        ensureActive()
        val count = source.read(buffer)
        if (count == -1) break
        if (count == 0) {
            // InputStream must make progress for a nonempty buffer. Tolerate a short
            // provider quirk without spinning forever on a broken ContentProvider.
            if (++emptyReads >= 16) {
                throw TransferIoException("io_error", "Content source repeatedly returned no read progress.")
            }
            Thread.yield()
            continue
        }
        emptyReads = 0
        ensureActive()
        val nextBytes = Math.addExact(bytes, count.toLong())
        if (expectedSize != null && nextBytes > expectedSize) {
            throw TransferIoException("size_mismatch", "Content exceeded its declared size.")
        }
        writeChunk(buffer, count)
        digest.update(buffer, 0, count)
        bytes = nextBytes
        val now = System.nanoTime()
        if (now - lastProgressAt >= 250_000_000L) {
            onProgress(bytes)
            lastProgressAt = now
        }
    }
    ensureActive()
    if (expectedSize != null && bytes != expectedSize) {
        throw TransferIoException("size_mismatch", "Content byte count did not match its declared size.")
    }
    return TransferStreamDigest(
        bytes,
        Base64.getUrlEncoder().withoutPadding().encodeToString(digest.digest()),
    )
}

internal class TransferIoException(val code: String, message: String) : IOException(message)
