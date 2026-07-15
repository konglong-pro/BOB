package com.bob.android.network

import java.nio.charset.StandardCharsets
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeParseException
import java.util.UUID
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

/** Wire-level constants and strict JSON helpers for BOB transport protocol v1. */
object BobProtocol {
    const val VERSION = 1
    const val SUBPROTOCOL = "bob.v1"
    const val MAX_ENVELOPE_BYTES = 1024 * 1024
    const val MAX_SNAPSHOT_ENVELOPE_BYTES = 768 * 1024
    const val MAX_TEXT_BYTES = 256 * 1024
    const val HEARTBEAT_SECONDS = 20L
    const val HELLO_DEADLINE_SECONDS = 5L

    const val CLOSE_INVALID_MESSAGE = 4400
    const val CLOSE_UNSUPPORTED_VERSION = 4406
    const val CLOSE_HELLO_TIMEOUT = 4408
    const val CLOSE_PEER_BUSY = 4429

    const val ERROR_INVALID_MESSAGE = "invalid_message"
    const val ERROR_MESSAGE_TOO_LARGE = "message_too_large"
    const val ERROR_UNSUPPORTED_VERSION = "unsupported_version"
    const val ERROR_PEER_BUSY = "peer_busy"
    const val ERROR_INVALID_METADATA = "invalid_metadata"

    private val EmptyUuid = UUID(0L, 0L)

    fun hello(
        installationId: UUID,
        clientName: String,
        osVersion: String,
        appVersion: String,
        envelopeId: UUID = UUID.randomUUID(),
        sentAt: Instant = Instant.now(),
    ): BobEnvelope {
        require(installationId != EmptyUuid) { "installationId cannot be empty" }
        require(clientName.isNotBlank() && clientName.length <= 128) {
            "clientName must contain 1..128 non-blank characters"
        }

        return envelope(
            type = "session.hello",
            id = envelopeId,
            sentAt = sentAt,
            payload = JSONObject()
                .put("protocolMin", VERSION)
                .put("protocolMax", VERSION)
                .put(
                    "client",
                    JSONObject()
                        .put("installationId", installationId.toString())
                        .put("name", clientName.trim())
                        .put("platform", "android")
                        .put("osVersion", osVersion)
                        .put("appVersion", appVersion),
                ),
        )
    }

    fun textSend(
        textId: UUID,
        text: String,
        createdAt: Instant,
        envelopeId: UUID = UUID.randomUUID(),
        sentAt: Instant = Instant.now(),
    ): BobEnvelope {
        require(textId != EmptyUuid) { "textId cannot be empty" }
        require(text.isNotBlank()) { "text cannot be blank" }
        require(utf8Size(text) <= MAX_TEXT_BYTES) { "text exceeds 256 KiB" }

        return envelope(
            type = "text.send",
            id = envelopeId,
            sentAt = sentAt,
            payload = JSONObject()
                .put("textId", textId.toString())
                .put("text", text)
                .put("createdAt", createdAt.toString()),
        )
    }

    fun textAck(
        textId: UUID,
        storedAt: Instant,
        replyTo: UUID? = null,
        envelopeId: UUID = UUID.randomUUID(),
        sentAt: Instant = Instant.now(),
    ): BobEnvelope {
        require(textId != EmptyUuid) { "textId cannot be empty" }
        require(replyTo != EmptyUuid) { "replyTo cannot be an empty UUID" }

        return envelope(
            type = "text.ack",
            id = envelopeId,
            sentAt = sentAt,
            replyTo = replyTo,
            payload = JSONObject()
                .put("textId", textId.toString())
                .put("storedAt", storedAt.toString()),
        )
    }

    fun transferOffer(
        transferId: UUID,
        retryOf: UUID?,
        kind: BobTransferKind,
        name: String,
        size: Long?,
        mediaType: String,
        createdAt: Instant,
        envelopeId: UUID = UUID.randomUUID(),
        sentAt: Instant = Instant.now(),
    ): BobEnvelope {
        require(transferId != EmptyUuid)
        require(retryOf != EmptyUuid)
        require(name.isNotBlank())
        require(size == null || size >= 0L)
        require(mediaType.isNotBlank())
        return envelope(
            type = "transfer.offer",
            id = envelopeId,
            sentAt = sentAt,
            payload = JSONObject()
                .put("transferId", transferId.toString())
                .put("retryOf", retryOf?.toString() ?: JSONObject.NULL)
                .put("kind", kind.wireValue)
                .put("name", name)
                .put("size", size ?: JSONObject.NULL)
                .put("mediaType", mediaType)
                .put("createdAt", createdAt.toString()),
        )
    }

    fun transferAccepted(
        transferId: UUID,
        plannedName: String,
        replyTo: UUID? = null,
    ): BobEnvelope = envelope(
        type = "transfer.accepted",
        id = UUID.randomUUID(),
        sentAt = Instant.now(),
        replyTo = replyTo,
        payload = JSONObject()
            .put("transferId", transferId.toString())
            .put("plannedName", plannedName),
    )

    fun transferDigest(
        transferId: UUID,
        sha256: String,
        bytes: Long,
    ): BobEnvelope = envelope(
        type = "transfer.digest",
        id = UUID.randomUUID(),
        sentAt = Instant.now(),
        payload = JSONObject()
            .put("transferId", transferId.toString())
            .put("algorithm", "sha-256")
            .put("value", sha256)
            .put("bytes", bytes),
    )

    fun transferProgress(
        transferId: UUID,
        bytes: Long,
        total: Long?,
    ): BobEnvelope = envelope(
        type = "transfer.progress",
        id = UUID.randomUUID(),
        sentAt = Instant.now(),
        payload = JSONObject()
            .put("transferId", transferId.toString())
            .put("bytes", bytes)
            .put("total", total ?: JSONObject.NULL),
    )

    fun transferCompleted(
        transferId: UUID,
        bytes: Long,
        sha256: String,
        storedName: String,
        completedAt: Instant = Instant.now(),
    ): BobEnvelope = envelope(
        type = "transfer.completed",
        id = UUID.randomUUID(),
        sentAt = Instant.now(),
        payload = JSONObject()
            .put("transferId", transferId.toString())
            .put("bytes", bytes)
            .put("sha256", sha256)
            .put("storedName", storedName)
            .put("completedAt", completedAt.toString()),
    )

    fun transferTerminalAck(
        transferId: UUID,
        state: BobTransferTerminalState,
    ): BobEnvelope = envelope(
        type = "transfer.terminalAck",
        id = UUID.randomUUID(),
        sentAt = Instant.now(),
        payload = JSONObject()
            .put("transferId", transferId.toString())
            .put("state", state.wireValue),
    )

    fun transferFailed(
        transferId: UUID,
        code: String,
        message: String,
        retryable: Boolean,
    ): BobEnvelope = envelope(
        type = "transfer.failed",
        id = UUID.randomUUID(),
        sentAt = Instant.now(),
        payload = JSONObject()
            .put("transferId", transferId.toString())
            .put("code", code)
            .put("message", truncateUtf8(message, 4 * 1024))
            .put("retryable", retryable),
    )

    fun error(
        code: String,
        message: String,
        retryable: Boolean,
        replyTo: UUID?,
        envelopeId: UUID = UUID.randomUUID(),
        sentAt: Instant = Instant.now(),
    ): BobEnvelope =
        envelope(
            type = "error",
            id = envelopeId,
            sentAt = sentAt,
            replyTo = replyTo,
            payload = JSONObject()
                .put("code", code)
                .put("message", message.take(1024))
                .put("retryable", retryable)
                .put("transferId", JSONObject.NULL),
        )

    fun encode(envelope: BobEnvelope, maximumBytes: Int = MAX_ENVELOPE_BYTES): String {
        require(maximumBytes in 1..MAX_ENVELOPE_BYTES) { "invalid envelope limit" }
        val encoded = JSONObject()
            .put("v", envelope.version)
            .put("type", envelope.type)
            .put("id", envelope.id.toString())
            .put("sentAt", envelope.sentAt.toString())
            .put("replyTo", envelope.replyTo?.toString() ?: JSONObject.NULL)
            .put("payload", envelope.payload)
            .toString()
        if (utf8Size(encoded) > maximumBytes) {
            throw BobProtocolException(ERROR_MESSAGE_TOO_LARGE, "Envelope exceeds $maximumBytes bytes")
        }
        return encoded
    }

    fun decode(encoded: String, maximumBytes: Int = MAX_ENVELOPE_BYTES): BobEnvelope {
        require(maximumBytes in 1..MAX_ENVELOPE_BYTES) { "invalid envelope limit" }
        if (utf8Size(encoded) > maximumBytes) {
            throw BobProtocolException(ERROR_MESSAGE_TOO_LARGE, "Envelope exceeds $maximumBytes bytes")
        }

        try {
            val json = JSONObject(encoded)
            val version = requiredInt(json, "v")
            val type = requiredString(json, "type").also {
                if (it.isBlank()) throw invalid("type cannot be blank")
            }
            val id = requiredUuid(json, "id")
            val sentAt = requiredUtcInstant(json, "sentAt")
            val replyTo = optionalUuid(json, "replyTo")
            val payload = requiredObject(json, "payload")
            return BobEnvelope(version, type, id, sentAt, replyTo, payload)
        } catch (error: BobProtocolException) {
            throw error
        } catch (error: JSONException) {
            throw invalid("Envelope is not valid JSON", error)
        } catch (error: IllegalArgumentException) {
            throw invalid("Envelope contains an invalid value", error)
        }
    }

    fun parseWelcome(envelope: BobEnvelope, helloId: UUID, expectedServerId: UUID): BobWelcome {
        if (envelope.version != VERSION || envelope.type != "session.welcome") {
            throw invalid("Expected session.welcome v1")
        }
        if (envelope.replyTo != helloId) throw invalid("session.welcome replyTo does not match hello")

        val payload = envelope.payload
        if (requiredInt(payload, "protocol") != VERSION) {
            throw BobProtocolException(ERROR_UNSUPPORTED_VERSION, "Server selected an unsupported protocol")
        }

        val sessionId = requiredUuid(payload, "sessionId")
        val server = requiredObject(payload, "server")
        val serverId = requiredUuid(server, "id")
        if (serverId != expectedServerId) throw invalid("session.welcome server.id changed")
        val serverName = requiredString(server, "name").also {
            if (it.isBlank() || it.length > 128) throw invalid("Invalid server name")
        }
        if (requiredString(server, "platform") != "windows") throw invalid("Server platform is not windows")
        val serverAppVersion = requiredString(server, "appVersion")
        val heartbeatSeconds = requiredInt(payload, "heartbeatSeconds")
        if (heartbeatSeconds !in 1..300) throw invalid("Invalid heartbeatSeconds")

        val advertisedLimits = requiredObject(payload, "limits")
        val maxEnvelopeBytes = requiredInt(advertisedLimits, "maxEnvelopeBytes")
        val maxTextBytes = requiredInt(advertisedLimits, "maxTextBytes")
        if (maxEnvelopeBytes !in 1..MAX_ENVELOPE_BYTES) {
            throw invalid("Invalid maxEnvelopeBytes")
        }
        if (maxTextBytes !in 1..MAX_TEXT_BYTES || maxTextBytes > maxEnvelopeBytes) {
            throw invalid("Invalid maxTextBytes")
        }

        return BobWelcome(
            envelopeId = envelope.id,
            sessionId = sessionId,
            serverId = serverId,
            serverName = serverName,
            serverAppVersion = serverAppVersion,
            heartbeatSeconds = heartbeatSeconds,
            limits = BobNegotiatedLimits(maxEnvelopeBytes, maxTextBytes),
        )
    }

    fun parseSnapshotPage(envelope: BobEnvelope): BobSnapshotPage {
        if (envelope.version != VERSION || envelope.type != "session.snapshot") {
            throw invalid("Expected session.snapshot v1")
        }
        val payload = envelope.payload
        val snapshotId = requiredUuid(payload, "snapshotId")
        val page = requiredInt(payload, "page")
        if (page < 0) throw invalid("Snapshot page cannot be negative")
        val isLast = requiredBoolean(payload, "isLast")
        val transfers = requiredArray(payload, "transfers")
        val transferJson = ArrayList<String>(transfers.length())
        for (index in 0 until transfers.length()) {
            val transfer = transfers.opt(index)
            if (transfer !is JSONObject) throw invalid("Snapshot transfers must be objects")
            transferJson += transfer.toString()
        }
        return BobSnapshotPage(snapshotId, page, isLast, transferJson)
    }

    fun parseTextSend(envelope: BobEnvelope, maximumTextBytes: Int): BobIncomingText {
        if (envelope.version != VERSION || envelope.type != "text.send") {
            throw invalid("Expected text.send v1")
        }
        if (maximumTextBytes !in 1..MAX_TEXT_BYTES) throw invalid("Invalid negotiated text limit")
        val payload = envelope.payload
        val textId = requiredUuid(payload, "textId")
        val text = requiredString(payload, "text")
        if (text.isBlank()) throw invalid("text cannot be blank")
        if (utf8Size(text) > maximumTextBytes) {
            throw BobProtocolException(ERROR_MESSAGE_TOO_LARGE, "text exceeds $maximumTextBytes bytes")
        }
        val createdAt = requiredUtcInstant(payload, "createdAt")
        return BobIncomingText(envelope.id, textId, text, createdAt)
    }

    fun parseTextAck(envelope: BobEnvelope): BobTextAcknowledgement {
        if (envelope.version != VERSION || envelope.type != "text.ack") {
            throw invalid("Expected text.ack v1")
        }
        val payload = envelope.payload
        return BobTextAcknowledgement(
            envelopeId = envelope.id,
            replyTo = envelope.replyTo,
            textId = requiredUuid(payload, "textId"),
            storedAt = requiredUtcInstant(payload, "storedAt"),
        )
    }

    fun parseTransferOffer(envelope: BobEnvelope): BobTransferOffer {
        requireType(envelope, "transfer.offer")
        val payload = envelope.payload
        val kind = BobTransferKind.fromWire(requiredString(payload, "kind"))
        val name = requiredString(payload, "name")
        if (name.isBlank()) throw invalid("transfer name cannot be blank")
        val size = optionalLong(payload, "size")
        if (size != null && size < 0L) throw invalid("transfer size cannot be negative")
        val mediaType = requiredString(payload, "mediaType")
        if (mediaType.isBlank()) throw invalid("mediaType cannot be blank")
        return BobTransferOffer(
            envelopeId = envelope.id,
            transferId = requiredUuid(payload, "transferId"),
            retryOf = optionalUuid(payload, "retryOf"),
            kind = kind,
            name = name,
            size = size,
            mediaType = mediaType,
            createdAt = requiredUtcInstant(payload, "createdAt"),
        )
    }

    fun parseTransferAccepted(envelope: BobEnvelope): BobTransferAccepted {
        requireType(envelope, "transfer.accepted")
        val payload = envelope.payload
        val plannedName = requiredString(payload, "plannedName")
        if (plannedName.isBlank()) throw invalid("plannedName cannot be blank")
        return BobTransferAccepted(
            envelopeId = envelope.id,
            replyTo = envelope.replyTo,
            transferId = requiredUuid(payload, "transferId"),
            plannedName = plannedName,
        )
    }

    fun parseTransferDigest(envelope: BobEnvelope): BobTransferDigest {
        requireType(envelope, "transfer.digest")
        val payload = envelope.payload
        val algorithm = requiredString(payload, "algorithm")
        val value = requiredString(payload, "value")
        val bytes = requiredLong(payload, "bytes")
        if (bytes < 0L || value.isBlank()) throw invalid("transfer digest is invalid")
        return BobTransferDigest(
            envelopeId = envelope.id,
            transferId = requiredUuid(payload, "transferId"),
            algorithm = algorithm,
            value = value,
            bytes = bytes,
        )
    }

    fun parseTransferCompleted(envelope: BobEnvelope): BobTransferCompleted {
        requireType(envelope, "transfer.completed")
        val payload = envelope.payload
        val bytes = requiredLong(payload, "bytes")
        val sha256 = requiredString(payload, "sha256")
        val storedName = requiredString(payload, "storedName")
        if (bytes < 0L || sha256.isBlank() || storedName.isBlank()) {
            throw invalid("transfer.completed payload is invalid")
        }
        return BobTransferCompleted(
            envelopeId = envelope.id,
            transferId = requiredUuid(payload, "transferId"),
            bytes = bytes,
            sha256 = sha256,
            storedName = storedName,
            completedAt = requiredUtcInstant(payload, "completedAt"),
        )
    }

    fun parseTransferTerminalAck(envelope: BobEnvelope): BobTransferTerminalAck {
        requireType(envelope, "transfer.terminalAck")
        val payload = envelope.payload
        return BobTransferTerminalAck(
            envelopeId = envelope.id,
            transferId = requiredUuid(payload, "transferId"),
            state = BobTransferTerminalState.fromWire(requiredString(payload, "state")),
        )
    }

    fun parseTransferFailed(envelope: BobEnvelope): BobTransferFailed {
        requireType(envelope, "transfer.failed")
        val payload = envelope.payload
        val code = requiredString(payload, "code")
        val message = requiredString(payload, "message")
        if (code.isBlank() || utf8Size(message) > 4 * 1024) {
            throw invalid("transfer.failed payload is invalid")
        }
        return BobTransferFailed(
            envelopeId = envelope.id,
            transferId = requiredUuid(payload, "transferId"),
            code = code,
            message = message,
            retryable = requiredBoolean(payload, "retryable"),
        )
    }

    fun parseTransferProgress(envelope: BobEnvelope): BobTransferProgress {
        requireType(envelope, "transfer.progress")
        val payload = envelope.payload
        val bytes = requiredLong(payload, "bytes")
        val total = optionalLong(payload, "total")
        if (bytes < 0L || (total != null && total < 0L)) throw invalid("transfer progress is invalid")
        return BobTransferProgress(
            transferId = requiredUuid(payload, "transferId"),
            bytes = bytes,
            total = total,
        )
    }

    fun parseError(envelope: BobEnvelope): BobRemoteError {
        if (envelope.version != VERSION || envelope.type != "error") throw invalid("Expected error v1")
        val payload = envelope.payload
        val transferId = optionalUuid(payload, "transferId")
        return BobRemoteError(
            envelopeId = envelope.id,
            replyTo = envelope.replyTo,
            code = requiredString(payload, "code"),
            message = requiredString(payload, "message"),
            retryable = requiredBoolean(payload, "retryable"),
            transferId = transferId,
        )
    }

    internal fun utf8Size(value: String): Int = value.toByteArray(StandardCharsets.UTF_8).size

    private fun truncateUtf8(value: String, maximumBytes: Int): String {
        if (utf8Size(value) <= maximumBytes) return value
        val output = StringBuilder()
        var bytes = 0
        value.codePoints().forEach { codePoint ->
            val text = String(Character.toChars(codePoint))
            val encodedBytes = utf8Size(text)
            if (bytes + encodedBytes <= maximumBytes) {
                output.append(text)
                bytes += encodedBytes
            }
        }
        return output.toString()
    }

    private fun envelope(
        type: String,
        id: UUID,
        sentAt: Instant,
        payload: JSONObject,
        replyTo: UUID? = null,
    ): BobEnvelope {
        require(id != EmptyUuid) { "Envelope id cannot be empty" }
        return BobEnvelope(VERSION, type, id, sentAt, replyTo, payload)
    }

    private fun requiredObject(parent: JSONObject, key: String): JSONObject =
        parent.opt(key) as? JSONObject ?: throw invalid("$key must be an object")

    private fun requiredArray(parent: JSONObject, key: String): JSONArray =
        parent.opt(key) as? JSONArray ?: throw invalid("$key must be an array")

    private fun requiredString(parent: JSONObject, key: String): String =
        parent.opt(key) as? String ?: throw invalid("$key must be a string")

    private fun requiredBoolean(parent: JSONObject, key: String): Boolean =
        parent.opt(key) as? Boolean ?: throw invalid("$key must be a boolean")

    private fun requiredInt(parent: JSONObject, key: String): Int =
        when (val value = parent.opt(key)) {
            is Int -> value
            is Long -> value.takeIf { it in Int.MIN_VALUE..Int.MAX_VALUE }?.toInt()
                ?: throw invalid("$key is outside int32")
            else -> throw invalid("$key must be an integer")
        }

    private fun requiredLong(parent: JSONObject, key: String): Long =
        when (val value = parent.opt(key)) {
            is Int -> value.toLong()
            is Long -> value
            else -> throw invalid("$key must be an integer")
        }

    private fun optionalLong(parent: JSONObject, key: String): Long? {
        val value = parent.opt(key)
        if (value == null || value === JSONObject.NULL) return null
        return when (value) {
            is Int -> value.toLong()
            is Long -> value
            else -> throw invalid("$key must be an integer or null")
        }
    }

    private fun requireType(envelope: BobEnvelope, type: String) {
        if (envelope.version != VERSION || envelope.type != type) {
            throw invalid("Expected $type v1")
        }
    }

    private fun requiredUuid(parent: JSONObject, key: String): UUID {
        val value = requiredString(parent, key)
        val parsed = try {
            UUID.fromString(value)
        } catch (error: IllegalArgumentException) {
            throw invalid("$key must be a UUID", error)
        }
        if (parsed == EmptyUuid) throw invalid("$key cannot be an empty UUID")
        return parsed
    }

    private fun optionalUuid(parent: JSONObject, key: String): UUID? {
        val value = parent.opt(key)
        if (value == null || value === JSONObject.NULL) return null
        if (value !is String) throw invalid("$key must be a UUID or null")
        val parsed = try {
            UUID.fromString(value)
        } catch (error: IllegalArgumentException) {
            throw invalid("$key must be a UUID", error)
        }
        if (parsed == EmptyUuid) throw invalid("$key cannot be an empty UUID")
        return parsed
    }

    private fun requiredUtcInstant(parent: JSONObject, key: String): Instant {
        val value = requiredString(parent, key)
        try {
            val parsed = OffsetDateTime.parse(value)
            if (parsed.offset != ZoneOffset.UTC) throw invalid("$key must use UTC")
            return parsed.toInstant()
        } catch (error: DateTimeParseException) {
            throw invalid("$key must be an RFC 3339 timestamp", error)
        }
    }

    private fun invalid(message: String, cause: Throwable? = null): BobProtocolException =
        BobProtocolException(ERROR_INVALID_MESSAGE, message, cause)
}

data class BobEnvelope(
    val version: Int,
    val type: String,
    val id: UUID,
    val sentAt: Instant,
    val replyTo: UUID?,
    val payload: JSONObject,
)

data class BobNegotiatedLimits(
    val maxEnvelopeBytes: Int,
    val maxTextBytes: Int,
)

data class BobWelcome(
    val envelopeId: UUID,
    val sessionId: UUID,
    val serverId: UUID,
    val serverName: String,
    val serverAppVersion: String,
    val heartbeatSeconds: Int,
    val limits: BobNegotiatedLimits,
)

data class BobSnapshotPage(
    val snapshotId: UUID,
    val page: Int,
    val isLast: Boolean,
    /** Raw JSON objects reserved for the later transfer slice. */
    val transfersJson: List<String>,
)

data class BobIncomingText(
    val envelopeId: UUID,
    val textId: UUID,
    val text: String,
    val createdAt: Instant,
)

data class BobTextAcknowledgement(
    val envelopeId: UUID,
    val replyTo: UUID?,
    val textId: UUID,
    val storedAt: Instant,
)

enum class BobTransferKind(val wireValue: String) {
    File("file"),
    Image("image"),
    ;

    companion object {
        fun fromWire(value: String): BobTransferKind = entries.firstOrNull { it.wireValue == value }
            ?: throw BobProtocolException(BobProtocol.ERROR_INVALID_MESSAGE, "Unsupported transfer kind")
    }
}

enum class BobTransferTerminalState(val wireValue: String) {
    Completed("completed"),
    Failed("failed"),
    Canceled("canceled"),
    ;

    companion object {
        fun fromWire(value: String): BobTransferTerminalState = entries.firstOrNull {
            it.wireValue == value
        } ?: throw BobProtocolException(BobProtocol.ERROR_INVALID_MESSAGE, "Invalid terminal state")
    }
}

data class BobTransferOffer(
    val envelopeId: UUID,
    val transferId: UUID,
    val retryOf: UUID?,
    val kind: BobTransferKind,
    val name: String,
    val size: Long?,
    val mediaType: String,
    val createdAt: Instant,
)

data class BobTransferAccepted(
    val envelopeId: UUID,
    val replyTo: UUID?,
    val transferId: UUID,
    val plannedName: String,
)

data class BobTransferDigest(
    val envelopeId: UUID,
    val transferId: UUID,
    val algorithm: String,
    val value: String,
    val bytes: Long,
)

data class BobTransferCompleted(
    val envelopeId: UUID,
    val transferId: UUID,
    val bytes: Long,
    val sha256: String,
    val storedName: String,
    val completedAt: Instant,
)

data class BobTransferTerminalAck(
    val envelopeId: UUID,
    val transferId: UUID,
    val state: BobTransferTerminalState,
)

data class BobTransferFailed(
    val envelopeId: UUID,
    val transferId: UUID,
    val code: String,
    val message: String,
    val retryable: Boolean,
)

data class BobTransferProgress(
    val transferId: UUID,
    val bytes: Long,
    val total: Long?,
)

data class BobRemoteError(
    val envelopeId: UUID,
    val replyTo: UUID?,
    val code: String,
    val message: String,
    val retryable: Boolean,
    val transferId: UUID?,
)

class BobProtocolException(
    val code: String,
    message: String,
    cause: Throwable? = null,
) : Exception(message, cause)
