package com.bob.android.security

import android.annotation.SuppressLint
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.X509TrustManager

internal interface BobObservedPinTrustManager : X509TrustManager {
    val observedServerPin: String?
}

@SuppressLint("CustomX509TrustManager") // Deliberately validates the complete BOB certificate profile.
internal class BobProbeTrustManager : BobObservedPinTrustManager {
    private val observedPin = AtomicReference<String?>(null)

    override val observedServerPin: String?
        get() = observedPin.get()

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = BobCertificateProfileValidator.validate(chain)
        observedPin.set(BobCertificatePin.fromCertificate(leaf))
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("BOB does not accept client certificates.")
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

@SuppressLint("CustomX509TrustManager") // Profile validation plus constant-time full-DER pin match.
internal class BobPinnedTrustManager(
    private val expectedPin: String,
    private val serverId: java.util.UUID,
) : BobObservedPinTrustManager {
    private val observedPin = AtomicReference<String?>(null)

    override val observedServerPin: String?
        get() = observedPin.get()

    init {
        BobCertificatePin.requireCanonical(expectedPin)
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = BobCertificateProfileValidator.validate(chain)
        val observedPin = BobCertificatePin.fromCertificate(leaf)
        if (!BobCertificatePin.matches(expectedPin, observedPin)) {
            throw BobCertificatePinMismatchException(serverId)
        }
        this.observedPin.set(observedPin)
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("BOB does not accept client certificates.")
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
