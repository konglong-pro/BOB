package com.bob.android.network

import android.content.Context
import com.bob.android.security.BobCertificatePin
import com.bob.android.security.BobCertificateProfileValidator
import com.bob.android.security.BobHttpsException
import com.bob.android.security.BobIdentityMismatchException
import com.bob.android.security.BobPinnedTrustManager
import com.bob.android.security.BobProbeTrustManager
import com.bob.android.security.BobServerInfoException
import com.bob.android.security.BobTrustRecord
import com.bob.android.security.BobTrustStore
import java.io.ByteArrayOutputStream
import java.io.Closeable
import java.net.Proxy
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.UUID
import java.util.concurrent.atomic.AtomicReference
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import okhttp3.Authenticator
import okhttp3.Call
import okhttp3.Callback
import okhttp3.ConnectionPool
import okhttp3.ConnectionSpec
import okhttp3.MediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.TlsVersion

class BobHttpsConnection internal constructor(
    val endpoint: BobEndpoint,
    val serverInfo: BobServerInfo,
    val trustRecord: BobTrustRecord,
    val client: OkHttpClient,
) : Closeable {
    override fun close() {
        client.shutdown()
    }
}

/** Establishes TOFU once, then returns a client that accepts only the saved full-DER pin. */
class BobHttpsConnector(
    context: Context,
    private val trustStore: BobTrustStore = BobTrustStore(context),
) {
    suspend fun establish(endpoint: BobEndpoint): BobHttpsConnection {
        // withContext has prompt cancellation: a blocking call may finish after the caller was
        // cancelled, before its result can be delivered. Retain and close that orphaned client.
        val undelivered = AtomicReference<BobHttpsConnection?>()
        try {
            val hintedTrust = withContext(Dispatchers.IO) {
                endpoint.expectedServerId?.let(trustStore::find)
            }
            val connection = if (hintedTrust != null) {
                establishStrict(endpoint, hintedTrust)
            } else {
                probeAndEstablishStrict(endpoint)
            }
            undelivered.set(connection)
            currentCoroutineContext().ensureActive()
            return connection.also { delivered ->
                undelivered.compareAndSet(delivered, null)
            }
        } finally {
            undelivered.getAndSet(null)?.close()
        }
    }

    fun trustedServers(): List<BobTrustRecord> = trustStore.list()

    /** The caller must disconnect and cancel reconnects for this ID before invoking reset. */
    fun resetTrust(serverId: UUID): Boolean = trustStore.reset(serverId)

    private suspend fun probeAndEstablishStrict(endpoint: BobEndpoint): BobHttpsConnection {
        val probeClient = buildClientOnIo(endpoint, BobProbeTrustManager(), isProbe = true)
        val observation = try {
            fetchInfo(probeClient, endpoint)
        } finally {
            probeClient.shutdown()
        }
        currentCoroutineContext().ensureActive()

        endpoint.expectedServerId?.let { expected ->
            if (observation.info.serverId != expected) {
                throw BobIdentityMismatchException(expected, observation.info.serverId)
            }
        }

        val trustRecord = withContext(Dispatchers.IO) {
            currentCoroutineContext().ensureActive()
            trustStore.saveOrConfirm(
                serverId = observation.info.serverId,
                certificatePin = observation.certificatePin,
                displayName = observation.info.name,
            )
        }
        currentCoroutineContext().ensureActive()
        return establishStrict(endpoint, trustRecord)
    }

    private suspend fun establishStrict(
        endpoint: BobEndpoint,
        trustRecord: BobTrustRecord,
    ): BobHttpsConnection {
        endpoint.expectedServerId?.let { expected ->
            if (expected != trustRecord.serverId) {
                throw BobIdentityMismatchException(expected, trustRecord.serverId)
            }
        }

        val strictClient = buildClientOnIo(
            endpoint = endpoint,
            trustManager = BobPinnedTrustManager(
                expectedPin = trustRecord.certificatePin,
                serverId = trustRecord.serverId,
            ),
            isProbe = false,
        )
        try {
            val observation = fetchInfo(strictClient, endpoint)
            if (!BobCertificatePin.matches(trustRecord.certificatePin, observation.certificatePin)) {
                throw BobHttpsException("Strict BOB response did not use the saved certificate pin.")
            }
            if (observation.info.serverId != trustRecord.serverId) {
                throw BobIdentityMismatchException(trustRecord.serverId, observation.info.serverId)
            }
            endpoint.expectedServerId?.let { expected ->
                if (observation.info.serverId != expected) {
                    throw BobIdentityMismatchException(expected, observation.info.serverId)
                }
            }
            requireProtocolV1(observation.info)
            currentCoroutineContext().ensureActive()

            val refreshedTrust = withContext(Dispatchers.IO) {
                currentCoroutineContext().ensureActive()
                trustStore.saveOrConfirm(
                    serverId = trustRecord.serverId,
                    certificatePin = trustRecord.certificatePin,
                    displayName = observation.info.name,
                )
            }
            currentCoroutineContext().ensureActive()
            return BobHttpsConnection(
                endpoint = endpoint,
                serverInfo = observation.info,
                trustRecord = refreshedTrust,
                client = strictClient,
            )
        } catch (error: Throwable) {
            strictClient.shutdown()
            throw error
        }
    }

    private fun buildClient(
        endpoint: BobEndpoint,
        trustManager: X509TrustManager,
        isProbe: Boolean,
    ): OkHttpClient {
        val sslContext = SSLContext.getInstance("TLS")
        sslContext.init(null, arrayOf(trustManager), SecureRandom())

        val builder = OkHttpClient.Builder()
            .sslSocketFactory(sslContext.socketFactory, trustManager)
            // Host continuity is supplied by this dedicated BOB trust manager. No global TLS
            // defaults are changed, and the strict client still requires the exact saved pin.
            .hostnameVerifier { _, _ -> true }
            .connectionSpecs(listOf(BOB_TLS))
            .followRedirects(false)
            .followSslRedirects(false)
            .retryOnConnectionFailure(false)
            .cache(null)
            .proxy(Proxy.NO_PROXY)
            .authenticator(Authenticator.NONE)
            .proxyAuthenticator(Authenticator.NONE)
            .connectTimeout(CONNECT_TIMEOUT_SECONDS, TimeUnit.SECONDS)
            .readTimeout(INFO_READ_TIMEOUT_SECONDS, TimeUnit.SECONDS)
            .writeTimeout(INFO_READ_TIMEOUT_SECONDS, TimeUnit.SECONDS)

        endpoint.socketFactory?.let(builder::socketFactory)
        if (isProbe) {
            builder.connectionPool(ConnectionPool(0, 1, TimeUnit.MILLISECONDS))
        }
        return builder.build()
    }

    private suspend fun buildClientOnIo(
        endpoint: BobEndpoint,
        trustManager: X509TrustManager,
        isProbe: Boolean,
    ): OkHttpClient {
        val undelivered = AtomicReference<OkHttpClient?>()
        try {
            val client = withContext(Dispatchers.IO) {
                buildClient(endpoint, trustManager, isProbe).also(undelivered::set)
            }
            currentCoroutineContext().ensureActive()
            undelivered.compareAndSet(client, null)
            return client
        } finally {
            undelivered.getAndSet(null)?.shutdown()
        }
    }

    private suspend fun fetchInfo(client: OkHttpClient, endpoint: BobEndpoint): InfoObservation {
        val request = Request.Builder()
            .url(endpoint.infoUrl())
            .get()
            .header("Accept", "application/json")
            .header("Cache-Control", "no-store")
            .build()

        val response = client.newCall(request).awaitResponse()
        return response.use { securedResponse ->
            withContext(Dispatchers.IO) { parseInfoResponse(securedResponse) }
        }
    }

    private fun parseInfoResponse(response: Response): InfoObservation {
            if (response.code != 200) {
                throw BobHttpsException("BOB /info returned HTTP ${response.code}.")
            }
            validateContentType(response)

            val peerCertificates = response.handshake?.peerCertificates
                ?: throw BobHttpsException("BOB /info response has no TLS handshake.")
            if (peerCertificates.size != 1 || peerCertificates.single() !is X509Certificate) {
                throw BobHttpsException("BOB /info response did not use one X.509 certificate.")
            }
            val leaf = peerCertificates.single() as X509Certificate
            // Bind the pin to this response, not to a separate preflight socket.
            BobCertificateProfileValidator.validate(arrayOf(leaf))
            val pin = BobCertificatePin.fromCertificate(leaf)

            val body = response.body
            val json = decodeUtf8(readBounded(body.byteStream()))
            val info = BobServerInfo.parse(json)
            requireProtocolV1(info)
            return InfoObservation(info = info, certificatePin = pin)
    }

    private fun requireProtocolV1(info: BobServerInfo) {
        if (!info.supportsProtocol(PROTOCOL_VERSION)) {
            throw BobServerInfoException("BOB server does not support protocol v$PROTOCOL_VERSION.")
        }
    }

    private fun validateContentType(response: Response) {
        val contentType: MediaType = response.body.contentType()
            ?: throw BobHttpsException("BOB /info response is missing Content-Type.")
        if (contentType.type != "application" || contentType.subtype != "json") {
            throw BobHttpsException("BOB /info response is not application/json.")
        }
        val charset = contentType.charset(StandardCharsets.UTF_8)
        if (charset != StandardCharsets.UTF_8) {
            throw BobHttpsException("BOB /info response is not UTF-8.")
        }
    }

    private fun readBounded(stream: java.io.InputStream): ByteArray = stream.use { input ->
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(8 * 1024)
        var total = 0
        while (true) {
            val read = input.read(buffer)
            if (read == -1) break
            total += read
            if (total > MAX_INFO_BYTES) {
                throw BobHttpsException("BOB /info response exceeds $MAX_INFO_BYTES bytes.")
            }
            output.write(buffer, 0, read)
        }
        output.toByteArray()
    }

    private fun decodeUtf8(bytes: ByteArray): String {
        return try {
            StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(bytes))
                .toString()
        } catch (error: Exception) {
            throw BobHttpsException("BOB /info response is not valid UTF-8.", error)
        }
    }

    private data class InfoObservation(
        val info: BobServerInfo,
        val certificatePin: String,
    )

    private companion object {
        const val PROTOCOL_VERSION = 1
        const val MAX_INFO_BYTES = 64 * 1024
        const val CONNECT_TIMEOUT_SECONDS = 5L
        const val INFO_READ_TIMEOUT_SECONDS = 10L
        val BOB_TLS: ConnectionSpec = ConnectionSpec.Builder(ConnectionSpec.MODERN_TLS)
            .tlsVersions(TlsVersion.TLS_1_3, TlsVersion.TLS_1_2)
            .build()
    }
}

private suspend fun Call.awaitResponse(): Response = suspendCancellableCoroutine { continuation ->
    continuation.invokeOnCancellation { cancel() }
    enqueue(
        object : Callback {
            override fun onFailure(call: Call, e: java.io.IOException) {
                continuation.resumeWith(Result.failure(e))
            }

            override fun onResponse(call: Call, response: Response) {
                continuation.resume(response) { _, unconsumedResponse, _ ->
                    unconsumedResponse.close()
                }
            }
        },
    )
}

private fun OkHttpClient.shutdown() {
    dispatcher.cancelAll()
    connectionPool.evictAll()
    dispatcher.executorService.shutdown()
    cache?.close()
}
