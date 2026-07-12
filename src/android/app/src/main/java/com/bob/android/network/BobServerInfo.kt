package com.bob.android.network

import com.bob.android.security.BobServerInfoException
import java.util.UUID
import org.json.JSONException
import org.json.JSONObject

data class BobServerInfo(
    val serverId: UUID,
    val name: String,
    val appVersion: String,
    val protocolMin: Int,
    val protocolMax: Int,
) {
    fun supportsProtocol(version: Int): Boolean = version in protocolMin..protocolMax

    companion object {
        private const val MAX_NAME_UTF8_BYTES = 1_024
        private const val MAX_VERSION_UTF8_BYTES = 128

        @Throws(BobServerInfoException::class)
        fun parse(json: String): BobServerInfo {
            try {
                val root = JSONObject(json)
                val rawServerId = root.requiredString("serverId")
                val serverId = try {
                    UUID.fromString(rawServerId)
                } catch (error: IllegalArgumentException) {
                    throw BobServerInfoException("/info serverId is not a UUID.", error)
                }
                if (rawServerId != serverId.toString()) {
                    throw BobServerInfoException("/info serverId is not a canonical lowercase UUID.")
                }

                val name = root.requiredString("name")
                requireBoundedNonBlank(name, "name", MAX_NAME_UTF8_BYTES)
                val appVersion = root.requiredString("appVersion")
                requireBoundedNonBlank(appVersion, "appVersion", MAX_VERSION_UTF8_BYTES)

                val protocol = root.requiredObject("protocol")
                val protocolMin = protocol.requiredInt("min")
                val protocolMax = protocol.requiredInt("max")
                if (protocolMin < 0 || protocolMax < protocolMin) {
                    throw BobServerInfoException("/info protocol range is invalid.")
                }

                return BobServerInfo(
                    serverId = serverId,
                    name = name,
                    appVersion = appVersion,
                    protocolMin = protocolMin,
                    protocolMax = protocolMax,
                )
            } catch (error: BobServerInfoException) {
                throw error
            } catch (error: JSONException) {
                throw BobServerInfoException("/info is not valid BOB JSON.", error)
            }
        }

        private fun JSONObject.requiredString(name: String): String {
            val value = opt(name)
            if (value !is String) throw BobServerInfoException("/info.$name must be a string.")
            return value
        }

        private fun JSONObject.requiredObject(name: String): JSONObject {
            val value = opt(name)
            if (value !is JSONObject) throw BobServerInfoException("/info.$name must be an object.")
            return value
        }

        private fun JSONObject.requiredInt(name: String): Int {
            val value = opt(name)
            val number = when (value) {
                is Int -> value
                is Long -> value.takeIf { it in Int.MIN_VALUE..Int.MAX_VALUE }?.toInt()
                else -> null
            }
            return number ?: throw BobServerInfoException("/info protocol.$name must be an int32.")
        }

        private fun requireBoundedNonBlank(value: String, name: String, maxBytes: Int) {
            if (value.isBlank()) throw BobServerInfoException("/info $name must not be blank.")
            if (value.toByteArray(Charsets.UTF_8).size > maxBytes) {
                throw BobServerInfoException("/info $name is too large.")
            }
        }
    }
}
