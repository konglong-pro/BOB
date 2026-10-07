package com.bob.android.device

import android.app.Activity
import android.app.Instrumentation
import android.content.ContentProvider
import android.content.ContentResolver
import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.content.ContextWrapper
import android.content.SharedPreferences
import android.content.pm.ProviderInfo
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.Bundle
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import android.util.AtomicFile
import com.bob.android.network.BobTransferKind
import com.bob.android.network.BobTransferOffer
import com.bob.android.persistence.AtomicTransferJournal
import com.bob.android.persistence.SavedConnectionPreference
import com.bob.android.persistence.SavedConnectionPreferenceException
import com.bob.android.persistence.SavedConnectionPreferences
import com.bob.android.persistence.SavedManualEndpoint
import com.bob.android.persistence.TransferJournalException
import com.bob.android.transfer.OnlineTransferCoordinator
import com.bob.android.transfer.OnlineTransferState
import com.bob.android.transfer.copyTransferStream
import java.io.ByteArrayInputStream
import java.io.File
import java.io.IOException
import java.lang.reflect.InvocationTargetException
import java.security.MessageDigest
import java.time.Instant
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.json.JSONObject

/** Framework-only device checks; neither real BOB journals nor existing media are visible. */
class BobDeviceChecks : Instrumentation() {
    override fun onCreate(arguments: Bundle?) {
        super.onCreate(arguments)
        start()
    }

    override fun onStart() {
        val runId = UUID.randomUUID().toString()
        val prefix = "BOB_QA_${runId}_"
        val media = FixtureMediaProvider(targetContext, prefix)
        media.attachInfo(targetContext, ProviderInfo().apply { authority = "bob.device.checks.$runId" })
        val root = File(targetContext.cacheDir, "device-checks/$runId").apply { mkdirs() }
        var passed = 0
        var failure: Throwable? = null
        fun checkCase(name: String, action: () -> Unit) {
            action()
            passed++
            sendStatus(0, Bundle().apply { putString("stream", "BOB_QA PASS $name\n") })
        }
        try {
            checkCase("connection_preferences_restore_identity_and_disconnect_intent") {
                val context = FixtureContext(targetContext, root, "connection-preferences", media)
                try {
                    check(SavedConnectionPreferences(context).read() == SavedConnectionPreference())
                    val connected = SavedConnectionPreference(
                        SavedManualEndpoint("2001:db8::1", UUID.randomUUID()), preferManual = true)
                    SavedConnectionPreferences(context).write(connected)
                    check(SavedConnectionPreferences(context).read() == connected)
                    val disconnected = connected.copy(autoConnect = false)
                    SavedConnectionPreferences(context).write(disconnected)
                    check(SavedConnectionPreferences(context).read() == disconnected)
                    val discovery = disconnected.copy(preferManual = false, autoConnect = true)
                    SavedConnectionPreferences(context).write(discovery)
                    check(SavedConnectionPreferences(context).read() == discovery)
                } finally { context.deleteSharedPreferences("bob_connection_preferences_v1") }
            }
            checkCase("corrupt_connection_preferences_fail_closed_without_rewriting") {
                val context = FixtureContext(targetContext, root, "corrupt-preferences", media)
                try {
                    val store = SavedConnectionPreferences(context)
                    val saved = SavedConnectionPreference(
                        SavedManualEndpoint("192.0.2.1", UUID.randomUUID()), preferManual = true)
                    store.write(saved)
                    val preferences = context.getSharedPreferences("bob_connection_preferences_v1", Context.MODE_PRIVATE)
                    check(preferences.edit().putString("auto_connect", "true").commit())
                    val original = preferences.all.toMap()
                    try {
                        store.read()
                        error("Corrupt connection intent must not enable automatic trust or recovery.")
                    } catch (_: SavedConnectionPreferenceException) { }
                    check(preferences.all == original)
                } finally { context.deleteSharedPreferences("bob_connection_preferences_v1") }
            }
            checkCase("atomic_journal_preserves_corrupt_original") {
                val context = FixtureContext(targetContext, root, "atomic", media)
                val journal = AtomicTransferJournal(context)
                check(journal.read().length() == 0)
                journal.write(JSONArray().put(JSONObject().put("fixture", prefix)))
                check(journal.read().getJSONObject(0).getString("fixture") == prefix)
                val file = File(context.filesDir, "transfers.v1.json")
                val interrupted = AtomicFile(file)
                interrupted.failWrite(interrupted.startWrite().also { it.write("partial snapshot".toByteArray()) })
                check(journal.read().getJSONObject(0).getString("fixture") == prefix)
                val corrupt = "{\"version\":\"1\",\"transfers\":[]}"
                file.writeText(corrupt)
                expectJournalFailure { journal.read() }
                check(file.readText() == corrupt)
                file.writeText("not-json")
                expectJournalFailure { journal.read() }
                check(file.readText() == "not-json")
            }
            listOf("known_file", "unknown_file", "zero_file", "image").forEach { label ->
                checkCase("pending_to_published_$label") {
                    val body = when (label) {
                        "zero_file" -> byteArrayOf()
                        "image" -> Base64.getDecoder().decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6eVQAAAAASUVORK5CYII=")
                        else -> ByteArray(193_731) { (it * 31).toByte() }
                    }
                    val kind = if (label == "image") BobTransferKind.Image else BobTransferKind.File
                    val context = FixtureContext(targetContext, root, label, media)
                    val fixture = createFixture(context, media, prefix + label, kind, body, label != "unknown_file")
                    val live = coordinator(context)
                    try {
                        invoke(live.value, "maybePublishIncoming", fixture.id)
                        await { live.value.items.value.singleOrNull()?.state == OnlineTransferState.Completed }
                        assertPublished(media, fixture, body)
                        val saved = AtomicTransferJournal(context).read().getJSONObject(0)
                        check(saved.isNull("pendingUri") && saved.getString("localUri") == fixture.uri.toString())
                        check(saved.getBoolean("terminalAckPending"))
                        check(saved.getString("terminalAt") == saved.getString("publicationAt"))
                        live.value.cancel(fixture.id)
                        invoke(live.value, "maybePublishIncoming", fixture.id)
                        Thread.sleep(150)
                        assertPublished(media, fixture, body)
                        check(!(invoke(live.value, "deleteBobPending", fixture.uri, kind) as Boolean))
                    } finally { live.scope.cancel() }
                }
            }
            checkCase("publish_metadata_failure_recovers_original_completed_payload") {
                val context = FixtureContext(targetContext, root, "publish-failure", media)
                val body = "BOB controlled publication crash fixture".toByteArray()
                val fixture = createFixture(context, media, prefix + "crash.bin", BobTransferKind.File, body)
                val live = coordinator(context)
                try {
                    media.failOneQueryAfterPublish = fixture.uri
                    invoke(live.value, "maybePublishIncoming", fixture.id)
                    await { media.failedPublicationQuery == fixture.uri }
                    val snapshot = AtomicTransferJournal(context).read().getJSONObject(0)
                    check(snapshot.getString("state") == "Verifying")
                    val originalTime = snapshot.getString("publicationAt")
                    check(originalTime.isNotBlank())
                    assertPublished(media, fixture, body)
                    live.scope.cancel()
                    val recovered = coordinator(context)
                    try {
                        check(recovered.value.items.value.single().state == OnlineTransferState.Completed)
                        val saved = AtomicTransferJournal(context).read().getJSONObject(0)
                        check(saved.getString("terminalAt") == originalTime)
                        check(saved.getString("publicationAt") == originalTime)
                        check(saved.getString("localSha256") == fixture.sha256)
                        assertPublished(media, fixture, body)
                        check(!(invoke(recovered.value, "deleteBobPending", fixture.uri, BobTransferKind.File) as Boolean))
                    } finally { recovered.scope.cancel() }
                } finally { live.scope.cancel() }
            }
            checkCase("published_content_corruption_preserves_media_and_original_journal") {
                val context = FixtureContext(targetContext, root, "published-corrupt", media)
                val original = "original".toByteArray()
                val modified = "modified".toByteArray()
                check(original.size == modified.size)
                val fixture = createFixture(context, media, prefix + "changed.bin", BobTransferKind.File, original)
                val journal = AtomicTransferJournal(context)
                val snapshot = journal.read()
                snapshot.getJSONObject(0).put("publicationAt", Instant.now().toString())
                journal.write(snapshot)
                val file = File(context.filesDir, "transfers.v1.json")
                val before = file.readText()
                media.publish(fixture.uri)
                media.real.openOutputStream(fixture.uri, "wt")!!.use { it.write(modified) }
                val error = expectJournalFailure { coordinator(context) }
                check(generateSequence<Throwable>(error) { it.cause }.any {
                    it.message == "Published content does not match its saved SHA-256."
                }) { "Recovery must reject the altered file because of its content digest." }
                check(file.readText() == before)
                check(media.pending(fixture.uri) == 0)
            }
            checkCase("digest_mismatch_deletes_only_unpublished_fixture") {
                val context = FixtureContext(targetContext, root, "mismatch", media)
                val fixture = createFixture(context, media, prefix + "mismatch.bin", BobTransferKind.File, "fixture".toByteArray())
                val journal = AtomicTransferJournal(context)
                val saved = journal.read()
                saved.getJSONObject(0).getJSONObject("remoteDigest").put("value", sha256("other".toByteArray()))
                journal.write(saved)
                val live = coordinator(context)
                try {
                    invoke(live.value, "maybePublishIncoming", fixture.id)
                    await { live.value.items.value.single().state == OnlineTransferState.Failed }
                    await { !media.exists(fixture.uri) &&
                        journal.read().getJSONObject(0).optString("failureCode") == "checksum_mismatch" }
                    check(!media.exists(fixture.uri))
                    check(journal.read().getJSONObject(0).getString("failureCode") == "checksum_mismatch")
                } finally { live.scope.cancel() }
            }
            checkCase("corrupt_later_record_keeps_earlier_pending_fixture") {
                val context = FixtureContext(targetContext, root, "invalid-tail", media)
                val fixture = createFixture(context, media, prefix + "kept.bin", BobTransferKind.File, "keep".toByteArray())
                val journal = AtomicTransferJournal(context)
                journal.write(journal.read().put(JSONObject().put("transferId", "invalid")))
                val file = File(context.filesDir, "transfers.v1.json")
                val before = file.readText()
                expectJournalFailure { coordinator(context) }
                check(file.readText() == before && media.pending(fixture.uri) == 1)
            }
            checkCase("interrupted_body_recovery_cleans_only_unpublished_fixture") {
                val context = FixtureContext(targetContext, root, "interrupted", media)
                val fixture = createFixture(context, media, prefix + "interrupted.bin", BobTransferKind.File, "partial".toByteArray())
                val journal = AtomicTransferJournal(context)
                val saved = journal.read()
                saved.getJSONObject(0).apply {
                    put("state", "Transferring").put("localBytes", JSONObject.NULL).put("localSha256", JSONObject.NULL)
                    put("verifyingSince", JSONObject.NULL).put("remoteDigest", JSONObject.NULL)
                }
                journal.write(saved)
                val live = coordinator(context)
                try {
                    check(live.value.items.value.single().state == OnlineTransferState.Failed)
                    check(journal.read().getJSONObject(0).getString("failureCode") == "network_interrupted")
                    check(!media.exists(fixture.uri))
                } finally { live.scope.cancel() }
            }
        } catch (error: Throwable) {
            failure = error
        } finally {
            try { media.cleanupFixtures() } catch (error: Throwable) { failure = failure ?: error }
            if (!root.deleteRecursively()) failure = failure ?: IOException("Could not remove isolated fixture journal directory.")
        }
        finish(if (failure == null) Activity.RESULT_OK else Activity.RESULT_CANCELED, Bundle().apply {
            putInt("passed", passed)
            putInt("failed", if (failure == null) 0 else 1)
            putString("stream", if (failure == null) "BOB_QA ALL PASS ($passed)\n" else
                "BOB_QA FAILED after $passed cases: ${failure.stackTraceToString()}\n")
        })
    }
}

private class FixtureContext(base: Context, root: File, private val caseName: String, media: FixtureMediaProvider) : ContextWrapper(base) {
    private val directory = File(root, caseName).apply { mkdirs() }
    private val fixtureResolver = ContentResolver.wrap(media)
    override fun getApplicationContext(): Context = this
    override fun getFilesDir(): File = directory
    override fun getCacheDir(): File = directory
    override fun getDataDir(): File = directory
    override fun getContentResolver(): ContentResolver = fixtureResolver
    override fun getSharedPreferences(name: String, mode: Int): SharedPreferences =
        baseContext.getSharedPreferences("bob_device_check_${directory.parentFile!!.name}_${caseName}_$name", mode)
    override fun deleteSharedPreferences(name: String): Boolean =
        baseContext.deleteSharedPreferences("bob_device_check_${directory.parentFile!!.name}_${caseName}_$name")
}

private data class LiveCoordinator(val value: OnlineTransferCoordinator, val scope: CoroutineScope)

private fun coordinator(context: Context): LiveCoordinator {
    // Expected failed recovery is observed via the actual initialization Deferred.
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO + CoroutineExceptionHandler { _, _ -> })
    val value = OnlineTransferCoordinator(context, scope)
    try {
        val initialized = OnlineTransferCoordinator::class.java.getDeclaredField("initialized").apply { isAccessible = true }
        @Suppress("UNCHECKED_CAST") val deferred = initialized.get(value) as Deferred<Unit>
        runBlocking { withTimeout(15_000) { deferred.await() } }
        return LiveCoordinator(value, scope)
    } catch (error: Throwable) {
        scope.cancel()
        throw error
    }
}

private data class Fixture(val id: UUID, val uri: Uri, val sha256: String)

private fun createFixture(context: Context, media: FixtureMediaProvider, name: String,
    kind: BobTransferKind, body: ByteArray, knownSize: Boolean = true): Fixture {
    val creator = coordinator(context)
    val id = UUID.randomUUID()
    val size = body.size.toLong().takeIf { knownSize }
    val mime = if (kind == BobTransferKind.Image) "image/png" else "application/octet-stream"
    val offer = BobTransferOffer(UUID.randomUUID(), id, null, kind, name, size, mime, Instant.now())
    val pending = try { invoke(creator.value, "createPendingMedia", offer)!! } finally { creator.scope.cancel() }
    val uri = pending.javaClass.getDeclaredField("uri").apply { isAccessible = true }.get(pending) as Uri
    val storedName = pending.javaClass.getDeclaredField("storedName").apply { isAccessible = true }.get(pending) as String
    check(media.pending(uri) == 1)
    val digest = context.contentResolver.openOutputStream(uri, "wt")!!.use { output ->
        copyTransferStream(ByteArrayInputStream(body), size, { buffer, count -> output.write(buffer, 0, count) })
    }
    check(digest.bytes == body.size.toLong() && digest.sha256 == sha256(body))
    val timestamp = Instant.now().toString()
    AtomicTransferJournal(context).write(JSONArray().put(JSONObject()
        .put("serverId", UUID.randomUUID().toString()).put("transferId", id.toString())
        .put("direction", "Incoming").put("kind", kind.wireValue).put("name", name).put("size", size ?: JSONObject.NULL)
        .put("mediaType", mime).put("state", "Verifying").put("bytes", digest.bytes).put("storedName", storedName)
        .put("localUri", JSONObject.NULL).put("createdAt", timestamp).put("recordedAt", timestamp)
        .put("error", JSONObject.NULL).put("sourceUri", JSONObject.NULL).put("pendingUri", uri.toString())
        .put("retryOf", JSONObject.NULL).put("localBytes", digest.bytes).put("localSha256", digest.sha256)
        .put("remoteDigest", JSONObject().put("algorithm", "sha-256").put("value", digest.sha256).put("bytes", digest.bytes))
        .put("verifyingSince", timestamp).put("terminalAt", JSONObject.NULL).put("terminalAckPending", false)
        .put("retryable", true).put("cancelPending", false).put("failureCode", JSONObject.NULL)
        .put("failureIsLocal", false).put("remoteFailure", JSONObject.NULL).put("publicationAt", JSONObject.NULL)))
    return Fixture(id, uri, digest.sha256)
}

private fun assertPublished(media: FixtureMediaProvider, fixture: Fixture, body: ByteArray) {
    check(media.pending(fixture.uri) == 0)
    media.real.openInputStream(fixture.uri)!!.use { input ->
        val digest = copyTransferStream(input, body.size.toLong(), { _, _ -> })
        check(digest.bytes == body.size.toLong() && digest.sha256 == fixture.sha256)
    }
}

private fun sha256(body: ByteArray): String =
    Base64.getUrlEncoder().withoutPadding().encodeToString(MessageDigest.getInstance("SHA-256").digest(body))

private fun invoke(target: Any, name: String, vararg args: Any): Any? {
    val method = target.javaClass.declaredMethods.single { it.name == name && it.parameterCount == args.size }
    method.isAccessible = true
    return try { method.invoke(target, *args) } catch (error: InvocationTargetException) { throw error.targetException }
}

private fun expectJournalFailure(action: () -> Any?): TransferJournalException {
    try { action() } catch (error: TransferJournalException) { return error }
    error("Expected the production journal to fail closed.")
}

private fun await(condition: () -> Boolean) {
    val deadline = System.nanoTime() + 15_000_000_000L
    while (!condition()) {
        check(System.nanoTime() < deadline) { "Timed out waiting for production coordinator state." }
        Thread.sleep(20)
    }
}

/** Actual MediaStore behind an exact fixture allowlist: even orphan scans cannot see user rows. */
private class FixtureMediaProvider(context: Context, private val prefix: String) : ContentProvider() {
    val real: ContentResolver = context.contentResolver
    private val packageName = context.packageName
    private val fixtures = mutableSetOf<Uri>()
    @Volatile var failOneQueryAfterPublish: Uri? = null
    @Volatile var failedPublicationQuery: Uri? = null
    private var publishedForFailure: Uri? = null
    override fun onCreate(): Boolean = true
    override fun getType(uri: Uri): String? { requireFixture(uri); return real.getType(uri) }
    override fun insert(uri: Uri, values: ContentValues?): Uri? {
        require(isCollection(uri))
        require(values?.getAsString(MediaStore.MediaColumns.DISPLAY_NAME)?.startsWith(prefix) == true)
        require(values.getAsInteger(MediaStore.MediaColumns.IS_PENDING) == 1)
        require(values.getAsString(MediaStore.MediaColumns.RELATIVE_PATH) in setOf("Download/BOB/", "Pictures/BOB/"))
        return real.insert(uri, values)?.also { synchronized(fixtures) { fixtures += it } }
    }
    override fun query(uri: Uri, projection: Array<out String>?, selection: String?,
        selectionArgs: Array<out String>?, sortOrder: String?): Cursor? {
        if (isCollection(uri)) {
            val ids = synchronized(fixtures) { fixtures.filter { it.toString().startsWith("$uri/") }.map(ContentUris::parseId) }
            if (ids.isEmpty()) return MatrixCursor(projection ?: arrayOf(MediaStore.MediaColumns._ID))
            val gate = "${MediaStore.MediaColumns._ID} IN (${ids.joinToString(",")})"
            return real.query(uri, projection, if (selection == null) gate else "($selection) AND ($gate)", selectionArgs, sortOrder)
        }
        requireFixture(uri)
        synchronized(this) {
            if (publishedForFailure == uri && failOneQueryAfterPublish == uri) {
                publishedForFailure = null
                failOneQueryAfterPublish = null
                failedPublicationQuery = uri
                throw IOException("Controlled fixture metadata read failure after publication.")
            }
        }
        return real.query(uri, projection, selection, selectionArgs, sortOrder)
    }
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int {
        requireFixture(uri)
        val count = real.update(uri, values, selection, selectionArgs)
        if (count == 1 && values?.getAsInteger(MediaStore.MediaColumns.IS_PENDING) == 0 && failOneQueryAfterPublish == uri) {
            synchronized(this) { publishedForFailure = uri }
        }
        return count
    }
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int {
        requireFixture(uri)
        return real.delete(uri, selection, selectionArgs)
    }
    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor? {
        requireFixture(uri)
        return real.openFileDescriptor(uri, mode)
    }
    fun publish(uri: Uri) {
        requireFixture(uri)
        check(real.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null) == 1)
    }
    fun pending(uri: Uri): Int {
        requireFixture(uri)
        return real.query(uri, arrayOf(MediaStore.MediaColumns.IS_PENDING), null, null, null)!!.use {
            check(it.moveToFirst()); it.getInt(0)
        }
    }
    fun exists(uri: Uri): Boolean {
        requireFixture(uri)
        return real.query(uri, arrayOf(MediaStore.MediaColumns._ID), null, null, null)!!.use { it.moveToFirst() }
    }
    fun cleanupFixtures() {
        synchronized(fixtures) { fixtures.toList() }.forEach { uri ->
            // Tests intentionally delete their own published fixtures after assertions.
            // Recheck the GUID name and owner on the exact row before removing anything.
            val owned = real.query(uri, arrayOf(MediaStore.MediaColumns.DISPLAY_NAME,
                MediaStore.MediaColumns.OWNER_PACKAGE_NAME), null, null, null)!!.use {
                !it.moveToFirst() || (it.getString(0).startsWith(prefix) && it.getString(1) == packageName)
            }
            check(owned) { "Fixture cleanup refused an unexpected MediaStore owner or name." }
            real.delete(uri, "${MediaStore.MediaColumns.DISPLAY_NAME} LIKE ? AND ${MediaStore.MediaColumns.OWNER_PACKAGE_NAME}=?",
                arrayOf("$prefix%", packageName))
        }
    }
    private fun requireFixture(uri: Uri) { check(synchronized(fixtures) { uri in fixtures }) { "Test blocked access outside its exact fixture allowlist." } }
    private fun isCollection(uri: Uri): Boolean = uri == MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY) ||
        uri == MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
}
