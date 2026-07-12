package com.bob.android.discovery

import android.net.Network
import java.util.UUID

data class DiscoveredComputer(
    val serviceName: String,
    val deviceId: String,
    val displayName: String,
    val host: String,
    val port: Int,
    val protocolVersion: String,
    val apiPath: String,
    val usesTls: Boolean,
    val network: Network?,
) {
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
