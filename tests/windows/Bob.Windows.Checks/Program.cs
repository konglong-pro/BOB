using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bob.Windows.Discovery;
using Bob.Windows.Domain;
using Bob.Windows.Security;
using Bob.Windows.Transport;

var checks = new (string Name, Action Run)[]
{
    ("protocol JSON shape", CheckProtocolJson),
    ("malformed protocol envelope rejection", CheckMalformedProtocolEnvelope),
    ("incoming text process idempotency", CheckIncomingTextIdempotency),
    ("session state transitions", CheckSessionStateTransitions),
    ("server certificate profile", CheckCertificateProfile),
    ("mDNS service descriptor", CheckMdnsServiceDescriptor),
    ("bounded WebSocket close", CheckBoundedWebSocketClose)
};

var failures = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {check.Name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void CheckProtocolJson()
{
    var textId = Guid.Parse("067522eb-eb29-42f1-9b8d-f627414a8fa6");
    var envelope = ProtocolJson.Create(
        "text.send",
        new
        {
            textId,
            text = "hello",
            createdAt = DateTimeOffset.Parse("2026-07-11T12:01:00Z")
        });
    var bytes = ProtocolJson.Serialize(envelope);
    var json = Encoding.UTF8.GetString(bytes);

    Assert(json.Contains("\"v\":1", StringComparison.Ordinal), "v must be camel-case JSON.");
    Assert(json.Contains("\"type\":\"text.send\"", StringComparison.Ordinal), "type is missing.");
    Assert(json.Contains("\"replyTo\":null", StringComparison.Ordinal), "replyTo must be explicit.");

    var roundTrip = ProtocolJson.Deserialize(bytes);
    Assert(roundTrip.V == BobProtocol.Version, "version did not round-trip.");
    Assert(roundTrip.Type == "text.send", "type did not round-trip.");
    Assert(roundTrip.Payload.GetProperty("textId").GetGuid() == textId, "textId did not round-trip.");
}

static void CheckMalformedProtocolEnvelope()
{
    const string json = """
        {
          "v": 1,
          "type": "text.send",
          "id": "d8290645-426e-48d6-a99f-794437b0da10",
          "sentAt": "2026-07-11T12:01:00Z",
          "replyTo": null,
          "payload": []
        }
        """;

    var rejected = false;
    try
    {
        ProtocolJson.Deserialize(Encoding.UTF8.GetBytes(json));
    }
    catch (JsonException)
    {
        rejected = true;
    }

    Assert(rejected, "non-object payload must be rejected as invalid protocol JSON.");
}

static void CheckIncomingTextIdempotency()
{
    var coordinator = new SessionCoordinator();
    var receivedCount = 0;
    coordinator.TextReceived += (_, _) => receivedCount++;

    var textId = Guid.Parse("b586e6b6-9184-482a-9048-1d9744de6208");
    var createdAt = DateTimeOffset.Parse("2026-07-11T12:01:00Z");

    Assert(
        coordinator.RegisterIncomingText(textId, "hello", createdAt, "Pixel")
            == IncomingTextRegistration.Added,
        "first text must be registered.");
    Assert(
        coordinator.RegisterIncomingText(textId, "hello", createdAt, "Pixel")
            == IncomingTextRegistration.Duplicate,
        "same textId and content must be treated as a duplicate.");
    Assert(receivedCount == 1, "duplicate text must not be published twice.");
    Assert(
        coordinator.RegisterIncomingText(textId, "changed", createdAt, "Pixel")
            == IncomingTextRegistration.Conflict,
        "same textId with different content must be rejected as a conflict.");
}

static void CheckSessionStateTransitions()
{
    var state = new SessionStateMachine();
    Assert(state.Phase == SessionPhase.Offline, "initial state must be offline.");

    state.BeginHandshake();
    Assert(state.Phase == SessionPhase.AwaitingHello, "handshake state was not entered.");

    state.CompleteHandshake();
    Assert(state.Phase == SessionPhase.Connected, "connected state was not entered.");

    var invalidTransitionRejected = false;
    try
    {
        state.CompleteHandshake();
    }
    catch (InvalidOperationException)
    {
        invalidTransitionRejected = true;
    }

    Assert(invalidTransitionRejected, "invalid duplicate handshake completion was accepted.");
    state.Reset();
    Assert(state.Phase == SessionPhase.Offline, "reset must return offline.");
}

static void CheckCertificateProfile()
{
    using var certificate = ServerCertificateFactory.Create(
        Guid.Parse("2d8f0aa9-1cd5-4f64-a81e-169eedb115cb"),
        "DESKTOP-BOB",
        DateTimeOffset.UtcNow);

    Assert(certificate.HasPrivateKey, "certificate must carry a private key.");

    var basicConstraints = certificate.Extensions
        .OfType<X509BasicConstraintsExtension>()
        .SingleOrDefault()
        ?? throw new InvalidOperationException("Basic Constraints is missing.");
    Assert(!basicConstraints.CertificateAuthority, "server certificate must not be a CA.");

    var keyUsage = certificate.Extensions
        .OfType<X509KeyUsageExtension>()
        .SingleOrDefault()
        ?? throw new InvalidOperationException("Key Usage is missing.");
    Assert(
        keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature),
        "digitalSignature usage is required.");

    var enhancedKeyUsage = certificate.Extensions
        .OfType<X509EnhancedKeyUsageExtension>()
        .SingleOrDefault()
        ?? throw new InvalidOperationException("Extended Key Usage is missing.");
    Assert(
        enhancedKeyUsage.EnhancedKeyUsages
            .OfType<Oid>()
            .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"),
        "serverAuth EKU is required.");

    var pin = ServerIdentity.CalculatePin(certificate);
    Assert(pin.Length == 43, "SHA-256 base64url pin must contain 43 characters.");
    Assert(!pin.Contains('=') && !pin.Contains('+') && !pin.Contains('/'), "pin is not unpadded base64url.");
}

static void CheckMdnsServiceDescriptor()
{
    var serverId = Guid.Parse("2d8f0aa9-1cd5-4f64-a81e-169eedb115cb");
    var descriptor = MdnsServiceDescriptor.Create(serverId, "DESKTOP-BOB");
    var properties = descriptor.Properties.ToDictionary(property => property.Key, property => property.Value);

    Assert(
        descriptor.ServiceName == "DESKTOP-BOB._bob._tcp.local",
        "mDNS service instance name is incorrect.");
    Assert(descriptor.HostName == "DESKTOP-BOB.local", "mDNS host name is incorrect.");
    Assert(descriptor.Port == BobProtocol.DefaultPort, "mDNS port is incorrect.");
    Assert(properties.Count == 5, "mDNS must publish exactly five TXT properties in v1.");
    Assert(properties["id"] == serverId.ToString("D"), "mDNS id TXT value is incorrect.");
    Assert(properties["name"] == "DESKTOP-BOB", "mDNS name TXT value is incorrect.");
    Assert(properties["pv"] == "1", "mDNS pv TXT value is incorrect.");
    Assert(properties["tls"] == "1", "mDNS tls TXT value is incorrect.");
    Assert(properties["api"] == "/bob/v1", "mDNS api TXT value is incorrect.");
}

static void CheckBoundedWebSocketClose()
{
    using var socket = new BlockingCloseWebSocket();
    var connection = new SessionCoordinator.SessionConnection(socket);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

    connection.CloseAsync(4400, "test", timeout.Token).GetAwaiter().GetResult();

    Assert(socket.CloseOutputCalled, "CloseOutputAsync must send the close frame without waiting for a peer handshake.");
    Assert(socket.AbortCalled, "A timed-out close must abort the socket and release the session.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class BlockingCloseWebSocket : WebSocket
{
    public bool AbortCalled { get; private set; }

    public bool CloseOutputCalled { get; private set; }

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => AbortCalled ? WebSocketState.Aborted : WebSocketState.Open;

    public override string? SubProtocol => null;

    public override void Abort() => AbortCalled = true;

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("CloseAsync must not wait for the peer handshake.");

    public override async Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        CloseOutputCalled = true;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public override void Dispose()
    {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
