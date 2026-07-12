using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bob.Windows.Security;

public static class ServerCertificateFactory
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    public static X509Certificate2 Create(
        Guid serverId,
        string machineName,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        using var rsa = RSA.Create(3072);
        var subject = new X500DistinguishedName($"CN=BOB-{EscapeRdn(machineName)}");
        var request = new CertificateRequest(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(ServerAuthenticationOid) },
                true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(machineName);
        subjectAlternativeNames.AddDnsName($"bob-{serverId:N}.local");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        using var generated = request.CreateSelfSigned(
            now.AddDays(-1),
            now.AddYears(10));

        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            string.Empty,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
    }

    private static string EscapeRdn(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("+", "\\+", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("<", "\\<", StringComparison.Ordinal)
            .Replace(">", "\\>", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal);
}
