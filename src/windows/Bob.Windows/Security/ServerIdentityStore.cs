using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Bob.Windows.Security;

public sealed class ServerIdentityStore
{
    private const string CertificateFriendlyName = "BOB local HTTPS identity";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppPaths _paths;

    public ServerIdentityStore(AppPaths paths)
    {
        _paths = paths;
    }

    public ServerIdentity LoadOrCreate()
    {
        // Only identifiers are written to LocalAppData. The private key lives in
        // CurrentUser\My, where Windows protects key material for this user.
        Directory.CreateDirectory(_paths.IdentityDirectory);
        var settingsPath = Path.Combine(_paths.IdentityDirectory, "server.json");
        var settings = ReadSettings(settingsPath);

        if (settings is not null)
        {
            var certificate = FindCertificate(settings.CertificateThumbprint);
            if (certificate is not null)
            {
                return new ServerIdentity(settings.ServerId, certificate);
            }

            // Preserve serverId during repair. Android will still detect the changed pin.
            return CreateAndPersist(settings.ServerId, settingsPath);
        }

        return CreateAndPersist(Guid.NewGuid(), settingsPath);
    }

    private ServerIdentity CreateAndPersist(Guid serverId, string settingsPath)
    {
        using var newCertificate = ServerCertificateFactory.Create(
            serverId,
            Environment.MachineName,
            DateTimeOffset.UtcNow);

        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            newCertificate.FriendlyName = CertificateFriendlyName;
            store.Add(newCertificate);
        }

        var persistedCertificate = FindCertificate(newCertificate.Thumbprint)
            ?? throw new InvalidOperationException(
                "The certificate was created but could not be reloaded from the current user's certificate store.");

        WriteSettingsAtomically(
            settingsPath,
            new IdentitySettings(serverId, persistedCertificate.Thumbprint));

        return new ServerIdentity(serverId, persistedCertificate);
    }

    private static IdentitySettings? ReadSettings(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<IdentitySettings>(
                File.ReadAllText(path),
                JsonOptions);
            return settings is not null
                && settings.ServerId != Guid.Empty
                && !string.IsNullOrWhiteSpace(settings.CertificateThumbprint)
                ? settings
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static X509Certificate2? FindCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        return store.Certificates
            .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(certificate => certificate.HasPrivateKey);
    }

    private static void WriteSettingsAtomically(string path, IdentitySettings settings)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed record IdentitySettings(Guid ServerId, string CertificateThumbprint);
}
