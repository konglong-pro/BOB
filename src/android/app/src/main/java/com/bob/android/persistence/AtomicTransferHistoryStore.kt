package com.bob.android.persistence

import android.content.Context
import android.net.Uri
import android.util.AtomicFile
import com.bob.android.network.BobTransferKind
import com.bob.android.transfer.OnlineTransferDirection
import com.bob.android.transfer.OnlineTransferItem
import com.bob.android.transfer.OnlineTransferState
import java.io.File
import java.io.FileNotFoundException
import java.time.Instant
import java.util.UUID
import org.json.JSONArray
import org.json.JSONObject

/** Display history only: archived entries never become live protocol workers. */
class AtomicTransferHistoryStore(context: Context) {
    private val storage = AtomicFile(File(context.applicationContext.filesDir, "transfer-history-v1.json"))
    var readWarning: String? = null
        private set

    fun read(): List<OnlineTransferItem> = try {
        val root = JSONObject(storage.openRead().bufferedReader(Charsets.UTF_8).use { it.readText() })
        require(root.getInt("schemaVersion") == 1)
        val array = root.getJSONArray("transfers")
        val seen = hashSetOf<String>()
        List(array.length()) { index ->
            val json = array.getJSONObject(index)
            val id = UUID.fromString(json.getString("transferId")).toString()
            require(seen.add(id))
            val serverId = UUID.fromString(json.getString("serverId")).toString()
            val direction = OnlineTransferDirection.valueOf(json.getString("direction"))
            val savedState = OnlineTransferState.valueOf(json.getString("state"))
            val completed = savedState == OnlineTransferState.Completed
            val interrupted = !completed && savedState != OnlineTransferState.Failed
            val uri = json.nullableString("localContentUri")?.let(Uri::parse)
                ?.takeIf { it.scheme == "content" }
                ?.takeIf { direction == OnlineTransferDirection.Outgoing || completed }
            OnlineTransferItem(
                transferId = id,
                serverId = serverId,
                direction = direction,
                kind = BobTransferKind.valueOf(json.getString("kind")),
                name = json.getString("name"),
                size = if (json.isNull("size")) null else json.getLong("size").also { require(it >= 0) },
                mediaType = json.getString("mediaType"),
                state = if (interrupted) OnlineTransferState.Failed else savedState,
                bytesTransferred = json.getLong("bytesTransferred").also { require(it >= 0) },
                storedName = json.nullableString("storedName"),
                localContentUri = uri,
                errorMessage = if (interrupted) "Interrupted when BOB stopped. Select the content again to resend."
                    else json.nullableString("errorMessage"),
                createdAt = Instant.parse(json.getString("createdAt")),
                localRecordedAt = Instant.parse(json.getString("localRecordedAt")),
            )
        }.sortedBy(OnlineTransferItem::localRecordedAt)
    } catch (_: FileNotFoundException) {
        emptyList()
    } catch (_: Exception) {
        // Keep the original archive for recovery rather than replacing malformed data.
        readWarning = "Transfer history could not be read. The saved archive has been kept."
        emptyList()
    }

    fun write(items: List<OnlineTransferItem>) {
        check(readWarning == null) { "The saved archive needs recovery before it can be replaced." }
        val array = JSONArray()
        items.forEach { item ->
            array.put(JSONObject()
                .put("transferId", item.transferId)
                .put("serverId", item.serverId)
                .put("direction", item.direction.name)
                .put("kind", item.kind.name)
                .put("name", item.name)
                .put("size", item.size ?: JSONObject.NULL)
                .put("mediaType", item.mediaType)
                .put("state", item.state.name)
                .put("bytesTransferred", item.bytesTransferred)
                .put("storedName", item.storedName ?: JSONObject.NULL)
                .put("localContentUri", item.localContentUri?.toString() ?: JSONObject.NULL)
                .put("errorMessage", item.errorMessage ?: JSONObject.NULL)
                .put("createdAt", item.createdAt.toString())
                .put("localRecordedAt", item.localRecordedAt.toString()))
        }
        val bytes = JSONObject().put("schemaVersion", 1).put("transfers", array)
            .toString().toByteArray(Charsets.UTF_8)
        val output = storage.startWrite()
        try {
            output.write(bytes)
            storage.finishWrite(output)
        } catch (error: Exception) {
            storage.failWrite(output)
            throw error
        }
    }

    private fun JSONObject.nullableString(key: String): String? =
        if (isNull(key)) null else getString(key)
}
