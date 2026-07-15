package com.bob.android.discovery

import android.net.Network
import java.util.UUID

data class DiscoveredComputer(
    val serviceName: String,
    val deviceId: String,
    val displayName: String,
    val hosts: List<String>,
    val port: Int,
    val protocolVersion: String,
    val apiPath: String,
    val usesTls: Boolean,
    val network: Network?,
) {
    init {
        require(hosts.isNotEmpty()) { "A discovered BOB computer must have at least one address." }
        require(hosts.none(String::isBlank)) { "Discovered BOB addresses must not be blank." }
    }

    val preferredHost: String
        get() = hosts.first()

    val isCompatible: Boolean
        get() =
            protocolVersion == "1" &&
                usesTls &&
                apiPath == "/bob/v1" &&
                deviceId.isCanonicalUuid()
}

private fun String.isCanonicalUuid(): Boolean =
    runCatching { UUID.fromString(this).toString() == this }.getOrDefault(false)

enum class DiscoveryPhase {
    Idle,
    Searching,
    Failed,
}

data class DiscoverySnapshot(
    val phase: DiscoveryPhase = DiscoveryPhase.Idle,
    val devices: List<DiscoveredComputer> = emptyList(),
    val errorMessage: String? = null,
)
