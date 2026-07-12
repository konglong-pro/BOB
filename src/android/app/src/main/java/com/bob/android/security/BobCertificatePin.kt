package com.bob.android.security

import java.security.MessageDigest
import java.security.cert.X509Certificate
import java.util.Base64

object BobCertificatePin {
    private const val SHA256_BYTES = 32

    fun fromCertificate(certificate: X509Certificate): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(certificate.encoded)
        return Base64.getUrlEncoder().withoutPadding().encodeToString(digest)
    }

    fun matches(expected: String, observed: String): Boolean = MessageDigest.isEqual(
        decodeCanonical(expected),
        decodeCanonical(observed),
    )

    fun requireCanonical(pin: String): String {
        decodeCanonical(pin)
        return pin
    }

    private fun decodeCanonical(pin: String): ByteArray {
        if (pin.isBlank() || '=' in pin || pin.any(Char::isWhitespace)) {
            throw IllegalArgumentException("Certificate pin is not canonical base64url without padding.")
        }
        val decoded = try {
            Base64.getUrlDecoder().decode(pin)
        } catch (error: IllegalArgumentException) {
            throw IllegalArgumentException("Certificate pin is not valid base64url.", error)
        }
        if (decoded.size != SHA256_BYTES ||
            Base64.getUrlEncoder().withoutPadding().encodeToString(decoded) != pin
        ) {
            throw IllegalArgumentException("Certificate pin is not a canonical SHA-256 value.")
        }
        return decoded
    }
}
