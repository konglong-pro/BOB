package com.bob.android.security

import android.content.Context
import android.content.SharedPreferences
import java.util.Locale
import java.util.UUID

data class BobTrustRecord(
    val serverId: UUID,
    val certificatePin: String,
    val displayName: String,
    val createdAtEpochMillis: Long,
    val lastSeenAtEpochMillis: Long,
)

/** App-private, backup-excluded TOFU records. Each mutation uses one preferences commit. */
class BobTrustStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun find(serverId: UUID): BobTrustRecord? = synchronized(PROCESS_LOCK) {
        readLocked(serverId)
    }

    /** Stable across process restarts and app upgrades; it is not an authentication credential. */
    fun getOrCreateInstallationId(): UUID = synchronized(PROCESS_LOCK) {
        val existing = preferences.getString(INSTALLATION_ID_KEY, null)
        if (existing != null) {
            val parsed = try {
                UUID.fromString(existing)
            } catch (error: IllegalArgumentException) {
                throw BobTrustStoreException("Stored BOB installationId is invalid.", error)
            }
            if (parsed.toString() != existing || parsed == EMPTY_UUID) {
                throw BobTrustStoreException("Stored BOB installationId is not canonical.")
            }
            return@synchronized parsed
        }

        val created = UUID.randomUUID()
        commitOrThrow(preferences.edit().putString(INSTALLATION_ID_KEY, created.toString()))
        created
    }

    fun list(): List<BobTrustRecord> = synchronized(PROCESS_LOCK) {
        preferences.all.keys
            .asSequence()
            .filter { it.startsWith(KEY_PREFIX) && it.endsWith(PIN_SUFFIX) }
            .mapNotNull { key ->
                val id = key.removePrefix(KEY_PREFIX).removeSuffix(PIN_SUFFIX)
                runCatching { UUID.fromString(id) }.getOrNull()?.let(::readLocked)
            }
            .sortedBy { it.displayName.lowercase(Locale.ROOT) }
            .toList()
    }

    /** Saves a new trust tuple or confirms an existing tuple without replacing its pin. */
    @Throws(BobSecurityException::class)
    fun saveOrConfirm(
        serverId: UUID,
        certificatePin: String,
        displayName: String,
        nowEpochMillis: Long = System.currentTimeMillis(),
    ): BobTrustRecord = synchronized(PROCESS_LOCK) {
        val canonicalPin = try {
            BobCertificatePin.requireCanonical(certificatePin)
        } catch (error: IllegalArgumentException) {
            throw BobTrustStoreException("Refusing to persist an invalid BOB certificate pin.", error)
        }
        val safeDisplayName = displayName.trim()
        if (safeDisplayName.isEmpty()) {
            throw BobTrustStoreException("Refusing to persist a blank BOB display name.")
        }

        val existing = readLocked(serverId)
        if (existing != null && !BobCertificatePin.matches(existing.certificatePin, canonicalPin)) {
            throw BobTrustConflictException(serverId)
        }

        val record = BobTrustRecord(
            serverId = serverId,
            certificatePin = existing?.certificatePin ?: canonicalPin,
            displayName = safeDisplayName,
            createdAtEpochMillis = existing?.createdAtEpochMillis ?: nowEpochMillis,
            lastSeenAtEpochMillis = nowEpochMillis,
        )
        val prefix = keyPrefix(serverId)
        commitOrThrow(
            preferences.edit()
                .putString(prefix + PIN_SUFFIX, record.certificatePin)
                .putString(prefix + NAME_SUFFIX, record.displayName)
                .putLong(prefix + CREATED_SUFFIX, record.createdAtEpochMillis)
                .putLong(prefix + LAST_SEEN_SUFFIX, record.lastSeenAtEpochMillis),
        )
        record
    }

    fun reset(serverId: UUID): Boolean = synchronized(PROCESS_LOCK) {
        val existed = readLocked(serverId) != null
        val prefix = keyPrefix(serverId)
        commitOrThrow(
            preferences.edit()
                .remove(prefix + PIN_SUFFIX)
                .remove(prefix + NAME_SUFFIX)
                .remove(prefix + CREATED_SUFFIX)
                .remove(prefix + LAST_SEEN_SUFFIX),
        )
        existed
    }

    private fun readLocked(serverId: UUID): BobTrustRecord? {
        val prefix = keyPrefix(serverId)
        val pin = preferences.getString(prefix + PIN_SUFFIX, null) ?: return null
        val name = preferences.getString(prefix + NAME_SUFFIX, null)
            ?: throw BobTrustStoreException("BOB trust record $serverId is incomplete.")
        if (!preferences.contains(prefix + CREATED_SUFFIX) ||
            !preferences.contains(prefix + LAST_SEEN_SUFFIX)
        ) {
            throw BobTrustStoreException("BOB trust record $serverId is incomplete.")
        }
        try {
            BobCertificatePin.requireCanonical(pin)
        } catch (error: IllegalArgumentException) {
            throw BobTrustStoreException("BOB trust record $serverId contains an invalid pin.", error)
        }
        return BobTrustRecord(
            serverId = serverId,
            certificatePin = pin,
            displayName = name,
            createdAtEpochMillis = preferences.getLong(prefix + CREATED_SUFFIX, 0L),
            lastSeenAtEpochMillis = preferences.getLong(prefix + LAST_SEEN_SUFFIX, 0L),
        )
    }

    private fun commitOrThrow(editor: SharedPreferences.Editor) {
        if (!editor.commit()) {
            throw BobTrustStoreException("Could not atomically persist BOB certificate trust.")
        }
    }

    private fun keyPrefix(serverId: UUID): String = "$KEY_PREFIX$serverId"

    private companion object {
        const val PREFERENCES_NAME = "bob_certificate_trust_v1"
        const val INSTALLATION_ID_KEY = "meta.installation_id"
        const val KEY_PREFIX = "server."
        const val PIN_SUFFIX = ".pin"
        const val NAME_SUFFIX = ".name"
        const val CREATED_SUFFIX = ".created"
        const val LAST_SEEN_SUFFIX = ".last_seen"
        val EMPTY_UUID = UUID(0L, 0L)
        val PROCESS_LOCK = Any()
    }
}
