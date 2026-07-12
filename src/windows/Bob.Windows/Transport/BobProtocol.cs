using System.Text.Json;

namespace Bob.Windows.Transport;

public static class BobProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 42424;
    public const string Subprotocol = "bob.v1";
    public const int MaxEnvelopeBytes = 1024 * 1024;
    public const int MaxTextBytes = 256 * 1024;
    public const int HeartbeatSeconds = 20;
    public const int PeerTimeoutSeconds = 60;
}

public sealed record ProtocolEnvelope(
    int V,
    string Type,
    Guid Id,
    DateTimeOffset SentAt,
    Guid? ReplyTo,
    JsonElement Payload);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public static ProtocolEnvelope Create(
        string type,
        object payload,
        Guid? replyTo = null) =>
        new(
            BobProtocol.Version,
            type,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            replyTo,
            JsonSerializer.SerializeToElement(payload, Options));

    public static byte[] Serialize(ProtocolEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    public static ProtocolEnvelope Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        var envelope = JsonSerializer.Deserialize<ProtocolEnvelope>(utf8Json, Options)
            ?? throw new JsonException("Envelope cannot be null.");

        if (string.IsNullOrWhiteSpace(envelope.Type)
            || envelope.Id == Guid.Empty
            || envelope.SentAt == default
            || envelope.SentAt.Offset != TimeSpan.Zero
            || envelope.ReplyTo == Guid.Empty
            || envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Envelope is missing a required protocol field.");
        }

        return envelope;
    }
}
