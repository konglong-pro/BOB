package com.bob.android.persistence

import com.bob.android.transfer.OnlineTransferItem
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** One I/O writer coalesces snapshots; a terminal sender can await its revision. */
class TransferHistoryCheckpointWriter(
    private val store: AtomicTransferHistoryStore,
    scope: CoroutineScope,
) {
    private val lock = Any()
    private val signals = Channel<Unit>(Channel.CONFLATED)
    private var latest = Checkpoint(0, emptyList())
    private var processedRevision = 0L
    private var lastResult = true
    private val waiters = mutableListOf<Pair<Long, CompletableDeferred<Boolean>>>()
    private val mutableWarning = MutableStateFlow(store.readWarning)
    val warning: StateFlow<String?> = mutableWarning.asStateFlow()

    init {
        scope.launch(Dispatchers.IO) {
            try {
                for (signal in signals) writeLatest()
            } finally {
                // An Activity/ViewModel shutdown drains the latest requested checkpoint.
                withContext(NonCancellable) { writeLatest() }
            }
        }
    }

    fun request(items: List<OnlineTransferItem>): Long {
        val revision = synchronized(lock) {
            latest = Checkpoint(latest.revision + 1, items.toList())
            latest.revision
        }
        signals.trySend(Unit)
        return revision
    }

    suspend fun await(revision: Long): Boolean {
        val completion = CompletableDeferred<Boolean>()
        synchronized(lock) {
            if (revision <= processedRevision) completion.complete(lastResult)
            else waiters += revision to completion
        }
        return completion.await()
    }

    fun close() { signals.close() }

    private fun writeLatest() {
        val checkpoint = synchronized(lock) { latest.takeIf { it.revision > processedRevision } } ?: return
        val saved = runCatching { store.write(checkpoint.items) }.isSuccess
        mutableWarning.value = if (saved) null else store.readWarning
            ?: "Transfer history could not be saved. Keep BOB open and check available storage."
        val ready = synchronized(lock) {
            processedRevision = checkpoint.revision
            lastResult = saved
            waiters.filter { it.first <= processedRevision }.also { complete -> waiters.removeAll(complete.toSet()) }
        }
        ready.forEach { it.second.complete(saved) }
    }

    private data class Checkpoint(val revision: Long, val items: List<OnlineTransferItem>)
}
