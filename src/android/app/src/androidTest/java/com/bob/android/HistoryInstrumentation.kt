package com.bob.android

import android.app.Activity
import android.app.Instrumentation
import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.os.Bundle
import android.util.AtomicFile
import com.bob.android.network.BobTransferKind
import com.bob.android.persistence.AtomicTransferHistoryStore
import com.bob.android.persistence.TransferHistoryCheckpointWriter
import com.bob.android.transfer.OnlineTransferDirection
import com.bob.android.transfer.OnlineTransferItem
import com.bob.android.transfer.OnlineTransferState
import java.io.File
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.runBlocking

/** Platform-only device checks, with no test dependency and no access to real history files. */
class HistoryInstrumentation : Instrumentation() {
    override fun onCreate(arguments: Bundle?) {
        super.onCreate(arguments)
        start()
    }

    override fun onStart() {
        val directory = File(targetContext.cacheDir, "history-check-${UUID.randomUUID()}")
        directory.mkdirs()
        val context = object : ContextWrapper(targetContext) {
            override fun getApplicationContext(): Context = this
            override fun getFilesDir(): File = directory
        }
        val results = Bundle()
        try {
            val first = record(BobTransferKind.Image, OnlineTransferDirection.Incoming, OnlineTransferState.Completed)
            val second = record(BobTransferKind.File, OnlineTransferDirection.Outgoing, OnlineTransferState.Completed)
                .copy(serverId = UUID.randomUUID().toString(), name = "测试 📄.pdf")
            AtomicTransferHistoryStore(context).write(listOf(first, second))
            check(AtomicTransferHistoryStore(context).read() == listOf(first, second)) { "History round trip failed" }

            val active = record(BobTransferKind.Image, OnlineTransferDirection.Incoming, OnlineTransferState.Verifying)
            AtomicTransferHistoryStore(context).write(listOf(active))
            val interrupted = AtomicTransferHistoryStore(context).read().single()
            check(interrupted.state == OnlineTransferState.Failed && interrupted.localContentUri == null)
            check(interrupted.errorMessage?.contains("Interrupted") == true)

            val unsafe = first.copy(localContentUri = Uri.parse("file:///unpublished.part"))
            AtomicTransferHistoryStore(context).write(listOf(unsafe))
            check(AtomicTransferHistoryStore(context).read().single().localContentUri == null)

            AtomicTransferHistoryStore(context).write(listOf(first))
            val file = File(directory, "transfer-history-v1.json")
            val atomic = AtomicFile(file)
            val partial = atomic.startWrite()
            partial.write("incomplete".toByteArray())
            atomic.failWrite(partial)
            check(AtomicTransferHistoryStore(context).read() == listOf(first)) { "Atomic rollback failed" }

            runBlocking {
                val writer = TransferHistoryCheckpointWriter(AtomicTransferHistoryStore(context), this)
                val records = List(60) { index ->
                    first.copy(transferId = UUID.randomUUID().toString(), name = "history-$index.png",
                        localRecordedAt = first.localRecordedAt.plusSeconds(index.toLong()))
                }
                repeat(60) { count -> writer.request(records.take(count + 1)) }
                val last = writer.request(records)
                check(writer.await(last)) { "I/O checkpoint did not finish" }
                writer.close()
                check(AtomicTransferHistoryStore(context).read() == records) { "Latest checkpoint lost records" }
            }

            file.writeText("corrupt archive")
            val corrupt = AtomicTransferHistoryStore(context)
            check(corrupt.read().isEmpty() && corrupt.readWarning != null)
            check(runCatching { corrupt.write(listOf(first)) }.isFailure)
            check(file.readText() == "corrupt archive") { "Malformed archive was overwritten" }

            results.putString("stream", "BOB transfer history: 6 checks passed (round trip, interruption, URI safety, atomic rollback, ordered checkpoints, corruption preservation).\n")
            results.putInt("checksPassed", 6)
            finish(Activity.RESULT_OK, results)
        } catch (error: Throwable) {
            results.putString("stream", "BOB transfer history check failed: ${error.javaClass.simpleName}: ${error.message}\n")
            finish(Activity.RESULT_CANCELED, results)
        } finally {
            // The target is the unique directory constructed inside this app's cache.
            directory.deleteRecursively()
        }
    }

    private fun record(kind: BobTransferKind, direction: OnlineTransferDirection, state: OnlineTransferState) =
        OnlineTransferItem(
            transferId = UUID.randomUUID().toString(), serverId = UUID.randomUUID().toString(),
            direction = direction, kind = kind, name = "image.png", size = 42,
            mediaType = "image/png", state = state, bytesTransferred = 42, storedName = "image.png",
            localContentUri = Uri.parse("content://media/external/images/media/123"),
            errorMessage = null, createdAt = Instant.parse("2026-10-29T00:00:00Z"),
            localRecordedAt = Instant.parse("2026-10-29T00:00:01Z"),
        )
}
