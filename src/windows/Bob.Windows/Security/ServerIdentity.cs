using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bob.Windows.Security;

public sealed class ServerIdentity : IDisposable
{
    public ServerIdentity(Guid serverId, X509Certificate2 certificate)
    {
        ServerId = serverId;
        Certificate = certificate;
        CertificatePin = CalculatePin(certificate);
    }

    public Guid ServerId { get; }

    public X509Certificate2 Certificate { get; }

    public string CertificatePin { get; }

    public static string CalculatePin(X509Certificate2 certificate)
    {
        var hash = SHA256.HashData(certificate.RawData);
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public void Dispose() => Certificate.Dispose();
}
