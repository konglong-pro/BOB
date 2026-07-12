package com.bob.android.persistence

import android.content.Context
import android.util.AtomicFile
import java.io.File
import java.io.FileNotFoundException
import java.io.FileOutputStream
import java.io.InputStreamReader
import java.time.Clock
import java.time.Instant
import java.util.Collections
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject

/**
 * Single-process, durable text timeline.
 *
 * All mutations synchronously commit an AtomicFile snapshot before publishing a
 * new StateFlow value. Callers should invoke mutating methods from an I/O
 * dispatcher. A successful [persistIncoming] return is the durability boundary
 * after which the caller may send text.ack.
 */
class AtomicTextMessageStore private constructor(
    private val storage: AtomicFile,
    private val clock: Clock,
) {
    constructor(
        context: Context,
        clock: Clock = Clock.systemUTC(),
    ) : this(
        storage = AtomicFile(File(context.applicationContext.filesDir, FILE_NAME)),
        clock = clock,
    )

    private val lock = Any()
    private val mutableTimeline: MutableStateFlow<List<PersistedTextMessage>>
    val timeline: StateFlow<List<PersistedTextMessage>>

    init {
        val initialMessages = synchronized(lock) {
            val loaded = readSnapshot()
            val recovered = loaded.map { message ->
                if (
                    message.direction == TextDirection.Outgoing &&
                    message.status == TextDeliveryStatus.Sending
                ) {
                    message.copy(status = TextDeliveryStatus.Queued)
                } else {
                    message
                }
            }
            val ordered = orderMessages(recovered)
            if (recovered != loaded) {
                writeSnapshot(ordered)
            }
            ordered
        }
        mutableTimeline = MutableStateFlow(initialMessages)
        timeline = mutableTimeline.asStateFlow()
    }

    /**
     * Creates the durable outbox entry before returning its ID to the transport.
     * Repeating the same ID and immutable fields is idempotent.
     */
    fun queueOutgoing(
        serverId: String,
        text: String,
        textId: String = UUID.randomUUID().toString(),
        createdAt: Instant = clock.instant(),
    ): PersistedTextMessage = synchronized(lock) {
        val normalizedServerId = normalizeUuid(serverId, "serverId")
        val normalizedTextId = normalizeUuid(textId, "textId")
        val current = mutableTimeline.value
        val existing = current.findMessage(normalizedServerId, normalizedTextId)
        if (existing != null) {
            ensureSameImmutableText(
                existing = existing,
                expectedDirection = TextDirection.Outgoing,
                text = text,
                createdAt = createdAt,
            )
            return@synchronized existing
        }

        val message = PersistedTextMessage(
            serverId = normalizedServerId,
            textId = normalizedTextId,
            direction = TextDirection.Outgoing,
            text = text,
            createdAt = createdAt,
            localReceivedAt = clock.instant(),
            status = TextDeliveryStatus.Queued,
            storedAt = null,
        )
        commit(current + message)
        message
    }

    /**
     * Idempotently persists an incoming message.
     *
     * The returned storedAt value is stable for duplicates and can be used in
     * text.ack. A conflicting body or createdAt for the same business ID is
     * rejected without changing disk or memory state.
     */
    fun persistIncoming(
        serverId: String,
        textId: String,
        text: String,
        createdAt: Instant,
        localReceivedAt: Instant = clock.instant(),
    ): IncomingTextPersistenceResult = synchronized(lock) {
        val normalizedServerId = normalizeUuid(serverId, "serverId")
        val normalizedTextId = normalizeUuid(textId, "textId")
        val current = mutableTimeline.value
        val existing = current.findMessage(normalizedServerId, normalizedTextId)
        if (existing != null) {
            ensureSameImmutableText(
                existing = existing,
                expectedDirection = TextDirection.Incoming,
                text = text,
                createdAt = createdAt,
            )
            return@synchronized IncomingTextPersistenceResult(
                message = existing,
                isDuplicate = true,
            )
        }

        val storedAt = clock.instant()
        val message = PersistedTextMessage(
            serverId = normalizedServerId,
            textId = normalizedTextId,
            direction = TextDirection.Incoming,
            text = text,
            createdAt = createdAt,
            localReceivedAt = localReceivedAt,
            status = TextDeliveryStatus.Received,
            storedAt = storedAt,
        )
        commit(current + message)
        IncomingTextPersistenceResult(
            message = message,
            isDuplicate = false,
        )
    }

    fun markOutgoingSending(
        serverId: String,
        textId: String,
    ): PersistedTextMessage = updateOutgoing(serverId, textId) { message ->
        when (message.status) {
            TextDeliveryStatus.Queued ->
                message.copy(status = TextDeliveryStatus.Sending)
            TextDeliveryStatus.Sending -> message
            TextDeliveryStatus.Delivered ->
                throw stateError(message, "is already delivered.")
            TextDeliveryStatus.Received ->
                error("Outgoing invariant violated.")
        }
    }

    fun requeueOutgoing(
        serverId: String,
        textId: String,
    ): PersistedTextMessage = updateOutgoing(serverId, textId) { message ->
        when (message.status) {
            TextDeliveryStatus.Sending ->
                message.copy(status = TextDeliveryStatus.Queued)
            TextDeliveryStatus.Queued -> message
            TextDeliveryStatus.Delivered ->
                throw stateError(message, "is already delivered.")
            TextDeliveryStatus.Received ->
                error("Outgoing invariant violated.")
        }
    }

    /**
     * Applies text.ack. Duplicate acknowledgements keep the first durable
     * storedAt value and do not rewrite the snapshot.
     */
    fun markOutgoingDelivered(
        serverId: String,
        textId: String,
        storedAt: Instant,
    ): PersistedTextMessage = updateOutgoing(serverId, textId) { message ->
        if (message.status == TextDeliveryStatus.Delivered) {
            message
        } else {
            message.copy(
                status = TextDeliveryStatus.Delivered,
                storedAt = storedAt,
            )
        }
    }

    /**
     * Returns the server's durable outbox in local FIFO order. Both queued and
     * in-flight records are included so reconnect logic can reuse the original
     * textId; a process restart has already converted sending back to queued.
     */
    fun pendingOutgoing(serverId: String): List<PersistedTextMessage> =
        synchronized(lock) {
            val normalizedServerId = normalizeUuid(serverId, "serverId")
            mutableTimeline.value.filter { message ->
                message.serverId == normalizedServerId &&
                    message.direction == TextDirection.Outgoing &&
                    message.status != TextDeliveryStatus.Delivered
            }
        }

    /** Requeues only the previous socket generation's in-flight records. */
    fun prepareOutgoingForReconnect(serverId: String): Int = synchronized(lock) {
        val normalizedServerId = normalizeUuid(serverId, "serverId")
        var changed = 0
        val next = mutableTimeline.value.map { message ->
            if (
                message.serverId == normalizedServerId &&
                message.direction == TextDirection.Outgoing &&
                message.status == TextDeliveryStatus.Sending
            ) {
                changed += 1
                message.copy(status = TextDeliveryStatus.Queued)
            } else {
                message
            }
        }
        if (changed > 0) commit(next)
        changed
    }

    /** Returns only records that have not been queued on the current WSS generation. */
    fun queuedOutgoing(serverId: String): List<PersistedTextMessage> = synchronized(lock) {
        val normalizedServerId = normalizeUuid(serverId, "serverId")
        mutableTimeline.value.filter { message ->
            message.serverId == normalizedServerId &&
                message.direction == TextDirection.Outgoing &&
                message.status == TextDeliveryStatus.Queued
        }
    }

    private fun updateOutgoing(
        serverId: String,
        textId: String,
        transform: (PersistedTextMessage) -> PersistedTextMessage,
    ): PersistedTextMessage = synchronized(lock) {
        val normalizedServerId = normalizeUuid(serverId, "serverId")
        val normalizedTextId = normalizeUuid(textId, "textId")
        val current = mutableTimeline.value
        val index = current.indexOfFirst { message ->
            message.serverId == normalizedServerId &&
                message.textId == normalizedTextId
        }
        if (index < 0) {
            throw TextMessageNotFoundException(normalizedServerId, normalizedTextId)
        }

        val existing = current[index]
        if (existing.direction != TextDirection.Outgoing) {
            throw TextMessageConflictException(normalizedServerId, normalizedTextId)
        }
        val updated = transform(existing)
        if (updated == existing) {
            return@synchronized existing
        }

        val next = current.toMutableList()
        next[index] = updated
        commit(next)
        updated
    }

    private fun commit(messages: List<PersistedTextMessage>) {
        val ordered = orderMessages(messages)
        writeSnapshot(ordered)
        mutableTimeline.value = ordered
    }

    private fun readSnapshot(): List<PersistedTextMessage> {
        val serialized = try {
            storage.openRead().use { input ->
                InputStreamReader(input, Charsets.UTF_8).use(InputStreamReader::readText)
            }
        } catch (_: FileNotFoundException) {
            return emptyList()
        } catch (failure: Exception) {
            throw TextStoreException("Unable to read the text store.", failure)
        }

        try {
            val root = JSONObject(serialized)
            val version = root.getInt(KEY_SCHEMA_VERSION)
            if (version != SCHEMA_VERSION) {
                throw TextStoreException("Unsupported text store schema: $version")
            }

            val array = root.getJSONArray(KEY_MESSAGES)
            val messages = ArrayList<PersistedTextMessage>(array.length())
            val keys = HashSet<String>(array.length())
            for (index in 0 until array.length()) {
                val message = messageFromJson(array.getJSONObject(index))
                val key = "${message.serverId}:${message.textId}"
                if (!keys.add(key)) {
                    throw TextStoreException(
                        "Duplicate text ID ${message.textId} for server ${message.serverId}.",
                    )
                }
                messages += message
            }
            return orderMessages(messages)
        } catch (failure: TextStoreException) {
            throw failure
        } catch (failure: Exception) {
            throw TextStoreException("The text store is malformed.", failure)
        }
    }

    private fun writeSnapshot(messages: List<PersistedTextMessage>) {
        var output: FileOutputStream? = null
        try {
            val root = JSONObject()
                .put(KEY_SCHEMA_VERSION, SCHEMA_VERSION)
                .put(
                    KEY_MESSAGES,
                    JSONArray().also { array ->
                        messages.forEach { message ->
                            array.put(message.toJson())
                        }
                    },
                )
            val bytes = root.toString().toByteArray(Charsets.UTF_8)
            val stream = storage.startWrite()
            output = stream
            stream.write(bytes)
            stream.flush()
            storage.finishWrite(stream)
            output = null
            val committed = readSnapshot()
            if (committed != messages) {
                throw TextStoreException(
                    "Atomic text store verification did not match the committed snapshot.",
                )
            }
        } catch (failure: Exception) {
            output?.let { stream ->
                runCatching { storage.failWrite(stream) }
            }
            throw TextStoreException("Unable to persist the text store.", failure)
        }
    }

    private fun PersistedTextMessage.toJson(): JSONObject =
        JSONObject()
            .put(KEY_SERVER_ID, serverId)
            .put(KEY_TEXT_ID, textId)
            .put(KEY_DIRECTION, direction.persistedValue)
            .put(KEY_TEXT, text)
            .put(KEY_CREATED_AT, createdAt.toString())
            .put(KEY_LOCAL_RECEIVED_AT, localReceivedAt.toString())
            .put(KEY_STATUS, status.persistedValue)
            .put(KEY_STORED_AT, storedAt?.toString() ?: JSONObject.NULL)

    private fun messageFromJson(json: JSONObject): PersistedTextMessage =
        PersistedTextMessage(
            serverId = normalizeUuid(json.getString(KEY_SERVER_ID), KEY_SERVER_ID),
            textId = normalizeUuid(json.getString(KEY_TEXT_ID), KEY_TEXT_ID),
            direction = TextDirection.fromPersistedValue(json.getString(KEY_DIRECTION)),
            text = json.getString(KEY_TEXT),
            createdAt = Instant.parse(json.getString(KEY_CREATED_AT)),
            localReceivedAt = Instant.parse(json.getString(KEY_LOCAL_RECEIVED_AT)),
            status = TextDeliveryStatus.fromPersistedValue(json.getString(KEY_STATUS)),
            storedAt = if (json.isNull(KEY_STORED_AT)) {
                null
            } else {
                Instant.parse(json.getString(KEY_STORED_AT))
            },
        )

    private fun ensureSameImmutableText(
        existing: PersistedTextMessage,
        expectedDirection: TextDirection,
        text: String,
        createdAt: Instant,
    ) {
        if (
            existing.direction != expectedDirection ||
            existing.text != text ||
            existing.createdAt != createdAt
        ) {
            throw TextMessageConflictException(existing.serverId, existing.textId)
        }
    }

    private fun stateError(
        message: PersistedTextMessage,
        detail: String,
    ): TextMessageStateException =
        TextMessageStateException(message.serverId, message.textId, detail)

    private fun List<PersistedTextMessage>.findMessage(
        serverId: String,
        textId: String,
    ): PersistedTextMessage? =
        firstOrNull { message ->
            message.serverId == serverId && message.textId == textId
        }

    private fun orderMessages(
        messages: List<PersistedTextMessage>,
    ): List<PersistedTextMessage> =
        Collections.unmodifiableList(
            messages.sortedWith(
                compareBy<PersistedTextMessage>(
                    PersistedTextMessage::localReceivedAt,
                    PersistedTextMessage::createdAt,
                    PersistedTextMessage::textId,
                ),
            ),
        )

    private companion object {
        const val FILE_NAME = "text-messages-v1.json"
        const val SCHEMA_VERSION = 1

        const val KEY_SCHEMA_VERSION = "schemaVersion"
        const val KEY_MESSAGES = "messages"
        const val KEY_SERVER_ID = "serverId"
        const val KEY_TEXT_ID = "textId"
        const val KEY_DIRECTION = "direction"
        const val KEY_TEXT = "text"
        const val KEY_CREATED_AT = "createdAt"
        const val KEY_LOCAL_RECEIVED_AT = "localReceivedAt"
        const val KEY_STATUS = "status"
        const val KEY_STORED_AT = "storedAt"

        fun normalizeUuid(value: String, fieldName: String): String =
            PersistedTextMessage.normalizeUuid(value, fieldName)
    }
}
