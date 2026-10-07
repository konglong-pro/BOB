package com.bob.android.persistence

import android.content.Context
import android.net.InetAddresses
import java.io.File
import java.util.UUID

data class SavedManualEndpoint(val address: String, val serverId: UUID)

data class SavedConnectionPreference(
    val manualEndpoint: SavedManualEndpoint? = null,
    val preferManual: Boolean = false,
    val autoConnect: Boolean = true,
)

/** Connection intent only. Certificate pins remain exclusively in BobTrustStore. */
class SavedConnectionPreferences(context: Context) {
    private val applicationContext = context.applicationContext
    private val preferences = applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    fun read(): SavedConnectionPreference = synchronized(LOCK) {
        try {
            val entries = preferences.all
            if (entries.isEmpty()) {
                val path = File(applicationContext.dataDir, "shared_prefs/$NAME.xml")
                if (path.exists() || File(path.path + ".bak").exists()) {
                    throw IllegalStateException("Saved connection preferences could not be read.")
                }
                return@synchronized SavedConnectionPreference()
            }
            require(entries[VERSION] == 1) { "Unsupported saved connection preferences." }
            val autoConnect = entries[AUTO_CONNECT] as? Boolean
                ?: throw IllegalStateException("Missing saved connection intent.")
            val preferManual = entries[PREFER_MANUAL] as? Boolean
                ?: throw IllegalStateException("Missing saved connection mode.")
            val address = entries[ADDRESS]
            val identity = entries[SERVER_ID]
            val endpoint = if (address == null && identity == null) null else {
                require(address is String && identity is String) { "Incomplete saved manual endpoint." }
                val id = UUID.fromString(identity)
                require(id.toString() == identity && id != UUID(0L, 0L)) { "Invalid saved server identity." }
                require(address == address.trim() && address.length <= 64 && InetAddresses.isNumericAddress(address)) {
                    "Invalid saved manual address."
                }
                SavedManualEndpoint(address, id)
            }
            require(!preferManual || endpoint != null) { "Manual recovery has no saved endpoint." }
            SavedConnectionPreference(endpoint, preferManual, autoConnect)
        } catch (error: Exception) {
            throw SavedConnectionPreferenceException("Could not read saved connection preferences. Automatic connection was paused.", error)
        }
    }

    fun write(value: SavedConnectionPreference) = synchronized(LOCK) {
        try {
            require(!value.preferManual || value.manualEndpoint != null)
            val editor = preferences.edit().putInt(VERSION, 1)
                .putBoolean(AUTO_CONNECT, value.autoConnect)
                .putBoolean(PREFER_MANUAL, value.preferManual)
            val endpoint = value.manualEndpoint
            if (endpoint == null) {
                editor.remove(ADDRESS).remove(SERVER_ID)
            } else {
                require(endpoint.serverId != UUID(0L, 0L) && endpoint.address == endpoint.address.trim() &&
                    endpoint.address.length <= 64 && InetAddresses.isNumericAddress(endpoint.address))
                editor.putString(ADDRESS, endpoint.address).putString(SERVER_ID, endpoint.serverId.toString())
            }
            check(editor.commit()) { "Atomic preferences commit failed." }
        } catch (error: Exception) {
            throw SavedConnectionPreferenceException("Could not save the connection preference.", error)
        }
    }

    private companion object {
        const val NAME = "bob_connection_preferences_v1"
        const val VERSION = "version"
        const val ADDRESS = "manual_address"
        const val SERVER_ID = "manual_server_id"
        const val PREFER_MANUAL = "prefer_manual"
        const val AUTO_CONNECT = "auto_connect"
        val LOCK = Any()
    }
}

class SavedConnectionPreferenceException(message: String, cause: Throwable? = null) : IllegalStateException(message, cause)
