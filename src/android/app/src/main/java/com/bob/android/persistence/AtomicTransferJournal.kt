package com.bob.android.persistence

import android.content.Context
import android.util.AtomicFile
import java.io.File
import java.io.FileNotFoundException
import java.io.FileOutputStream
import java.io.IOException
import java.nio.file.Files
import org.json.JSONArray
import org.json.JSONObject

/** Coordination metadata only; file bodies always remain in their source or MediaStore. */
class AtomicTransferJournal(context: Context) {
    private val storage = AtomicFile(File(context.applicationContext.filesDir, "transfers.v1.json"))

    fun read(): JSONArray {
        val text = try {
            storage.openRead().bufferedReader(Charsets.UTF_8).use { it.readText() }
        } catch (error: FileNotFoundException) {
            // A provider/filesystem failure must not masquerade as a new installation.
            // AtomicFile also uses a legacy .bak during recovery.
            if (listOf(storage.baseFile, File(storage.baseFile.path + ".bak"), File(storage.baseFile.path + ".new"))
                    .all { Files.notExists(it.toPath()) }) return JSONArray()
            throw TransferJournalException("Could not read saved file transfers. The original journal was kept.", error)
        } catch (error: Exception) {
            throw TransferJournalException("Could not read saved file transfers. The original journal was kept.", error)
        }
        try {
            val snapshot = JSONObject(text)
            val version = snapshot.get("version")
            require((version is Int || version is Long) && (version as Number).toLong() == 1L) {
                "Unsupported file transfer journal version."
            }
            return snapshot.getJSONArray("transfers")
        } catch (error: Exception) {
            throw TransferJournalException("Saved file transfers are invalid. The original journal was kept.", error)
        }
    }

    fun write(records: JSONArray) {
        var stream: FileOutputStream? = null
        try {
            val encoded = JSONObject().put("version", 1).put("transfers", records)
                .toString().toByteArray(Charsets.UTF_8)
            val output = storage.startWrite()
            stream = output
            output.write(encoded)
            storage.finishWrite(output)
            stream = null
        } catch (error: Exception) {
            stream?.let { runCatching { storage.failWrite(it) } }
            throw TransferJournalException("Could not save file transfer history. No acknowledgement was sent.", error)
        }
    }
}

class TransferJournalException(message: String, cause: Throwable? = null) : IOException(message, cause)
