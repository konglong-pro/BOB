package com.bob.android.security

import java.io.IOException
import java.security.cert.CertificateException
import java.util.UUID

open class BobSecurityException(message: String, cause: Throwable? = null) : IOException(message, cause)

class BobCertificateValidationException(message: String, cause: Throwable? = null) :
    CertificateException(message, cause)

class BobCertificatePinMismatchException(
    val serverId: UUID? = null,
) : CertificateException("The BOB server certificate does not match the saved pin.")

class BobTrustConflictException(
    val serverId: UUID,
) : BobSecurityException("The certificate for BOB server $serverId changed; trust was not overwritten.")

class BobTrustStoreException(message: String, cause: Throwable? = null) :
    BobSecurityException(message, cause)

class BobIdentityMismatchException(
    val expectedServerId: UUID,
    val actualServerId: UUID,
) : BobSecurityException(
    "BOB server identity mismatch: expected $expectedServerId, received $actualServerId.",
)

class BobServerInfoException(message: String, cause: Throwable? = null) : IOException(message, cause)

class BobHttpsException(message: String, cause: Throwable? = null) : IOException(message, cause)
