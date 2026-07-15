package com.bob.android.network

import java.util.UUID
import javax.net.SocketFactory
import okhttp3.HttpUrl

/**
 * A single resolved BOB service endpoint.
 *
 * [displayNameHint] and [expectedServerId] can originate from mDNS. The name is
 * presentation-only and the ID is not trusted until the HTTPS identity checks complete.
 */
data class BobEndpoint(
    val host: String,
    val port: Int = DEFAULT_PORT,
    val apiPath: String = DEFAULT_API_PATH,
    val expectedServerId: UUID? = null,
    val displayNameHint: String? = null,
    val socketFactory: SocketFactory? = null,
) {
    init {
        require(host.isNotBlank()) { "BOB endpoint host must not be blank." }
        require(host == host.trim()) { "BOB endpoint host must not contain surrounding whitespace." }
        require(port in 1..65535) { "BOB endpoint port is out of range." }
        require(apiPath == DEFAULT_API_PATH) { "BOB v1 requires API path $DEFAULT_API_PATH." }
    }

    fun infoUrl(): HttpUrl = urlFor("info")

    /** OkHttp upgrades this pinned HTTPS URL to a secure WebSocket connection. */
    fun webSocketUrl(): HttpUrl = urlFor("ws")

    fun transferContentUrl(transferId: UUID): HttpUrl =
        urlFor("transfers/${transferId}/content")

    private fun urlFor(relativePath: String): HttpUrl = HttpUrl.Builder()
        .scheme("https")
        .host(host.removeSurrounding("[", "]"))
        .port(port)
        .encodedPath("$apiPath/$relativePath")
        .build()

    companion object {
        const val DEFAULT_PORT = 42424
        const val DEFAULT_API_PATH = "/bob/v1"
    }
}
