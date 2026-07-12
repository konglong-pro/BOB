package com.bob.android.security

import java.security.GeneralSecurityException
import java.security.cert.CertificateException
import java.security.cert.X509Certificate

object BobCertificateProfileValidator {
    private const val BASIC_CONSTRAINTS_OID = "2.5.29.19"
    private const val SERVER_AUTH_OID = "1.3.6.1.5.5.7.3.1"
    private const val DIGITAL_SIGNATURE_INDEX = 0

    @Throws(CertificateException::class)
    fun validate(chain: Array<out X509Certificate>?): X509Certificate {
        if (chain == null || chain.size != 1) {
            throw BobCertificateValidationException("BOB requires exactly one self-signed certificate.")
        }

        val leaf = chain.single()
        if (leaf.subjectX500Principal != leaf.issuerX500Principal) {
            throw BobCertificateValidationException("BOB certificate is not self-issued.")
        }

        try {
            leaf.verify(leaf.publicKey)
            leaf.checkValidity()
        } catch (error: GeneralSecurityException) {
            throw BobCertificateValidationException("BOB certificate self-signature or validity failed.", error)
        }

        if (leaf.getExtensionValue(BASIC_CONSTRAINTS_OID) == null || leaf.basicConstraints != -1) {
            throw BobCertificateValidationException("BOB certificate must explicitly set CA=false.")
        }

        val extendedKeyUsage = try {
            leaf.extendedKeyUsage
        } catch (error: CertificateException) {
            throw BobCertificateValidationException("BOB certificate EKU is malformed.", error)
        }
        if (extendedKeyUsage == null || SERVER_AUTH_OID !in extendedKeyUsage) {
            throw BobCertificateValidationException("BOB certificate must include serverAuth EKU.")
        }

        val keyUsage = leaf.keyUsage
        if (keyUsage == null || keyUsage.size <= DIGITAL_SIGNATURE_INDEX ||
            !keyUsage[DIGITAL_SIGNATURE_INDEX]
        ) {
            throw BobCertificateValidationException("BOB certificate must allow digitalSignature.")
        }

        return leaf
    }
}
