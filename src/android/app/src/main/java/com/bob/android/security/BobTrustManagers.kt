package com.bob.android.security

import android.annotation.SuppressLint
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.X509TrustManager

@SuppressLint("CustomX509TrustManager") // Deliberately validates the complete BOB certificate profile.
internal class BobProbeTrustManager : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        BobCertificateProfileValidator.validate(chain)
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
) : X509TrustManager {
    init {
        BobCertificatePin.requireCanonical(expectedPin)
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = BobCertificateProfileValidator.validate(chain)
        val observedPin = BobCertificatePin.fromCertificate(leaf)
        if (!BobCertificatePin.matches(expectedPin, observedPin)) {
            throw BobCertificatePinMismatchException(serverId)
        }
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("BOB does not accept client certificates.")
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
