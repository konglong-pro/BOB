using Bob.Windows.Transport;

namespace Bob.Windows.Discovery;

internal sealed record MdnsServiceDescriptor(
    string ServiceName,
    string HostName,
    ushort Port,
    IReadOnlyList<KeyValuePair<string, string>> Properties)
{
    public static MdnsServiceDescriptor Create(Guid serverId, string machineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("The server id cannot be empty.", nameof(serverId));
        }

        return new MdnsServiceDescriptor(
            $"{machineName}._bob._tcp.local",
            $"{machineName}.local",
            BobProtocol.DefaultPort,
            [
                new("id", serverId.ToString("D")),
                new("name", machineName),
                new("pv", BobProtocol.Version.ToString()),
                new("tls", "1"),
                new("api", "/bob/v1")
            ]);
    }
}
