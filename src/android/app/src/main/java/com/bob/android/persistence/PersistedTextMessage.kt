package com.bob.android.persistence

import java.nio.charset.StandardCharsets
import java.time.Instant
import java.util.UUID

enum class TextDirection(val persistedValue: String) {
    Incoming("incoming"),
    Outgoing("outgoing"),
    ;

    internal companion object {
        fun fromPersistedValue(value: String): TextDirection =
            entries.firstOrNull { it.persistedValue == value }
                ?: throw TextStoreException("Unknown text direction: $value")
    }
}

enum class TextDeliveryStatus(val persistedValue: String) {
    Queued("queued"),
    Sending("sending"),
    Delivered("delivered"),
    Received("received"),
    ;

    internal companion object {
        fun fromPersistedValue(value: String): TextDeliveryStatus =
            entries.firstOrNull { it.persistedValue == value }
                ?: throw TextStoreException("Unknown text status: $value")
    }
}

data class PersistedTextMessage(
    val serverId: String,
    val textId: String,
    val direction: TextDirection,
    val text: String,
    val createdAt: Instant,
    val localReceivedAt: Instant,
    val status: TextDeliveryStatus,
    val storedAt: Instant?,
) {
    init {
        requireUuid(serverId, "serverId")
        requireUuid(textId, "textId")
        require(text.trim().isNotEmpty()) { "Text must not be blank." }
        require(text.toByteArray(StandardCharsets.UTF_8).size <= MAX_TEXT_BYTES) {
            "Text must not exceed 256 KiB when encoded as UTF-8."
        }
        when (direction) {
            TextDirection.Incoming -> {
                require(status == TextDeliveryStatus.Received) {
                    "Incoming text must have received status."
                }
                require(storedAt != null) {
                    "Incoming text must record its durable storedAt timestamp."
                }
            }

            TextDirection.Outgoing -> {
                require(status != TextDeliveryStatus.Received) {
                    "Outgoing text cannot have received status."
                }
                require(
                    (status == TextDeliveryStatus.Delivered) == (storedAt != null),
                ) {
                    "Only delivered outgoing text may have a storedAt timestamp."
                }
            }
        }
    }

    internal companion object {
        const val MAX_TEXT_BYTES = 256 * 1024

        fun normalizeUuid(value: String, fieldName: String): String =
            try {
                UUID.fromString(value).toString()
            } catch (failure: IllegalArgumentException) {
                throw IllegalArgumentException("$fieldName must be a UUID.", failure)
            }

        private fun requireUuid(value: String, fieldName: String) {
            normalizeUuid(value, fieldName)
        }
    }
}

data class IncomingTextPersistenceResult(
    val message: PersistedTextMessage,
    val isDuplicate: Boolean,
)

open class TextStoreException(
    message: String,
    cause: Throwable? = null,
) : IllegalStateException(message, cause)

class TextMessageConflictException(
    serverId: String,
    textId: String,
) : TextStoreException(
    "Text ID $textId for server $serverId conflicts with an existing message.",
)

class TextMessageNotFoundException(
    serverId: String,
    textId: String,
) : TextStoreException(
    "Text ID $textId was not found for server $serverId.",
)

class TextMessageStateException(
    serverId: String,
    textId: String,
    detail: String,
) : TextStoreException(
    "Text ID $textId for server $serverId $detail",
)
