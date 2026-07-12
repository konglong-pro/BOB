using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bob.Windows.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bob.Windows.Transport;

public sealed class WebSocketSessionHandler
{
    private const int ReceiveBufferBytes = 16 * 1024;
    private static readonly TimeSpan ControlFrameTimeout = TimeSpan.FromSeconds(2);
    private readonly SessionCoordinator _sessions;
    private readonly ServerIdentity _identity;
    private readonly ILogger<WebSocketSessionHandler> _logger;

    public WebSocketSessionHandler(
        SessionCoordinator sessions,
        ServerIdentity identity,
        ILogger<WebSocketSessionHandler> logger)
    {
        _sessions = sessions;
        _identity = identity;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest
            || !context.WebSockets.WebSocketRequestedProtocols.Contains(
                BobProtocol.Subprotocol,
                StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket subprotocol bob.v1 is required.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(
            BobProtocol.Subprotocol);
        using var lease = _sessions.TryOpen(socket);

        if (lease is null)
        {
            await SendErrorAsync(
                socket,
                "peer_busy",
                "Another Android session is already active.",
                retryable: true,
                replyTo: null,
                context.RequestAborted);
            await CloseAsync(socket, 4429, "peer_busy", context.RequestAborted);
            return;
        }

        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));

            var hello = await ReceiveEnvelopeAsync(socket, helloTimeout.Token);
            if (hello is null)
            {
                return;
            }

            if (hello.V != BobProtocol.Version
                || !string.Equals(hello.Type, "session.hello", StringComparison.Ordinal))
            {
                await SendErrorAsync(
                    lease,
                    "invalid_message",
                    "The first message must be session.hello.",
                    retryable: false,
                    hello.Id,
                    context.RequestAborted);
                await CloseAsync(lease, 4400, "invalid_message", context.RequestAborted);
                return;
            }

            if (!TryReadHello(hello.Payload, out var client, out var versionSupported))
            {
                await SendErrorAsync(
                    lease,
                    "invalid_message",
                    "session.hello payload is invalid.",
                    retryable: false,
                    hello.Id,
                    context.RequestAborted);
                await CloseAsync(lease, 4400, "invalid_message", context.RequestAborted);
                return;
            }

            if (!versionSupported)
            {
                await SendErrorAsync(
                    lease,
                    "unsupported_version",
                "The devices do not share a supported protocol version.",
                    retryable: false,
                    hello.Id,
                    context.RequestAborted);
                await CloseAsync(lease, 4406, "unsupported_version", context.RequestAborted);
                return;
            }

            await lease.SendAsync(CreateWelcome(hello.Id), context.RequestAborted);
            await lease.SendAsync(CreateEmptySnapshot(), context.RequestAborted);
            lease.CompleteHandshake(client.Name);

            while (socket.State == WebSocketState.Open
                && !context.RequestAborted.IsCancellationRequested)
            {
                var envelope = await ReceiveEnvelopeAsync(
                    socket,
                    context.RequestAborted);
                if (envelope is null)
                {
                    break;
                }

                await HandleEnvelopeAsync(
                    lease,
                    client.Name,
                    envelope,
                    context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Normal client disconnect or server shutdown.
        }
        catch (OperationCanceledException)
        {
            await CloseAsync(lease, 4408, "hello_timeout", CancellationToken.None);
        }
        catch (ProtocolException exception)
        {
            _logger.LogInformation("Rejected malformed WSS message: {Reason}", exception.Message);
            await SendErrorAsync(
                lease,
                "invalid_message",
                exception.Message,
                retryable: false,
                replyTo: null,
                CancellationToken.None);
            await CloseAsync(lease, 4400, "invalid_message", CancellationToken.None);
        }
        catch (WebSocketException exception)
        {
            _logger.LogDebug(exception, "BOB WSS peer disconnected.");
        }
        finally
        {
            if (socket.State == WebSocketState.CloseReceived)
            {
                await CloseAsync(
                    lease,
                    (int)WebSocketCloseStatus.NormalClosure,
                    "closed",
                    CancellationToken.None);
            }
        }
    }

    private async Task HandleEnvelopeAsync(
        SessionLease lease,
        string peerName,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (envelope.V != BobProtocol.Version)
        {
            await SendErrorAsync(
                lease,
                "unsupported_version",
                "Envelope version is not supported.",
                retryable: false,
                envelope.Id,
                cancellationToken);
            return;
        }

        switch (envelope.Type)
        {
            case "text.send":
                await HandleTextSendAsync(
                    lease,
                    peerName,
                    envelope,
                    cancellationToken);
                break;
            case "text.ack":
                await HandleTextAckAsync(
                    lease,
                    envelope,
                    cancellationToken);
                break;
            default:
                await SendErrorAsync(
                    lease,
                    "invalid_message",
                    $"Unsupported message type: {envelope.Type}",
                    retryable: false,
                    envelope.Id,
                    cancellationToken);
                break;
        }
    }

    private async Task HandleTextSendAsync(
        SessionLease lease,
        string peerName,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!TryReadText(envelope.Payload, out var textId, out var text, out var createdAt))
        {
            await SendErrorAsync(
                lease,
                "invalid_message",
                "text.send payload is invalid.",
                retryable: false,
                envelope.Id,
                cancellationToken);
            return;
        }

        var registration = _sessions.RegisterIncomingText(
            textId,
            text,
            createdAt,
            peerName);
        if (registration == IncomingTextRegistration.Conflict)
        {
            await SendErrorAsync(
                lease,
                "invalid_metadata",
                "The textId was already used with different content.",
                retryable: false,
                envelope.Id,
                cancellationToken);
            return;
        }

        await lease.SendAsync(
            ProtocolJson.Create(
                "text.ack",
                new
                {
                    textId,
                    storedAt = DateTimeOffset.UtcNow
                },
                envelope.Id),
            cancellationToken);
    }

    private async Task HandleTextAckAsync(
        SessionLease lease,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!TryReadTextAck(envelope.Payload, out var textId))
        {
            await SendErrorAsync(
                lease,
                "invalid_message",
                "text.ack payload is invalid.",
                retryable: false,
                envelope.Id,
                cancellationToken);
            return;
        }

        _sessions.PublishTextAcknowledged(textId);
    }

    private ProtocolEnvelope CreateWelcome(Guid helloId) =>
        ProtocolJson.Create(
            "session.welcome",
            new
            {
                protocol = BobProtocol.Version,
                sessionId = Guid.NewGuid(),
                server = new
                {
                    id = _identity.ServerId,
                    name = Environment.MachineName,
                    platform = "windows",
                    appVersion = AppVersion.Current
                },
                heartbeatSeconds = BobProtocol.HeartbeatSeconds,
                limits = new
                {
                    maxEnvelopeBytes = BobProtocol.MaxEnvelopeBytes,
                    maxTextBytes = BobProtocol.MaxTextBytes
                }
            },
            helloId);

    private static ProtocolEnvelope CreateEmptySnapshot() =>
        ProtocolJson.Create(
            "session.snapshot",
            new
            {
                snapshotId = Guid.NewGuid(),
                page = 0,
                isLast = true,
                transfers = Array.Empty<object>()
            });

    private static async Task<ProtocolEnvelope?> ReceiveEnvelopeAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferBytes];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new ProtocolException("Only UTF-8 JSON text frames are accepted.");
            }

            if (message.Length + result.Count > BobProtocol.MaxEnvelopeBytes)
            {
                throw new ProtocolException("Envelope exceeds 1 MiB.");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        try
        {
            return ProtocolJson.Deserialize(message.ToArray());
        }
        catch (JsonException exception)
        {
            throw new ProtocolException("Envelope is not valid protocol JSON.", exception);
        }
    }

    private static bool TryReadHello(
        JsonElement payload,
        out ClientDescriptor client,
        out bool versionSupported)
    {
        client = default;
        versionSupported = false;

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("protocolMin", out var minValue)
            || !minValue.TryGetInt32(out var minimum)
            || !payload.TryGetProperty("protocolMax", out var maxValue)
            || !maxValue.TryGetInt32(out var maximum)
            || minimum > maximum
            || !payload.TryGetProperty("client", out var clientValue)
            || clientValue.ValueKind != JsonValueKind.Object
            || !clientValue.TryGetProperty("name", out var nameValue)
            || nameValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var name = nameValue.GetString();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            return false;
        }

        versionSupported = minimum <= BobProtocol.Version
            && maximum >= BobProtocol.Version;
        client = new ClientDescriptor(name.Trim());
        return true;
    }

    private static bool TryReadText(
        JsonElement payload,
        out Guid textId,
        out string text,
        out DateTimeOffset createdAt)
    {
        textId = Guid.Empty;
        text = string.Empty;
        createdAt = default;

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("textId", out var idValue)
            || idValue.ValueKind != JsonValueKind.String
            || !idValue.TryGetGuid(out textId)
            || textId == Guid.Empty
            || !payload.TryGetProperty("text", out var textValue)
            || textValue.ValueKind != JsonValueKind.String
            || !payload.TryGetProperty("createdAt", out var createdAtValue)
            || createdAtValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = textValue.GetString() ?? string.Empty;
        var createdAtText = createdAtValue.GetString();
        return !string.IsNullOrWhiteSpace(text)
            && Encoding.UTF8.GetByteCount(text) <= BobProtocol.MaxTextBytes
            && DateTimeOffset.TryParse(
                createdAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out createdAt);
    }

    private static bool TryReadTextAck(JsonElement payload, out Guid textId)
    {
        textId = Guid.Empty;

        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("textId", out var idValue)
            && idValue.ValueKind == JsonValueKind.String
            && idValue.TryGetGuid(out textId)
            && textId != Guid.Empty
            && payload.TryGetProperty("storedAt", out var storedAtValue)
            && storedAtValue.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                storedAtValue.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _);
    }

    private static async Task SendErrorAsync(
        SessionLease lease,
        string code,
        string message,
        bool retryable,
        Guid? replyTo,
        CancellationToken cancellationToken)
    {
        if (lease.Socket.State != WebSocketState.Open)
        {
            return;
        }

        using var timeout = CreateControlFrameTimeout(cancellationToken);
        try
        {
            await lease.SendAsync(
                CreateError(code, message, retryable, replyTo),
                timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            lease.Socket.Abort();
        }
        catch (WebSocketException)
        {
            lease.Socket.Abort();
        }
    }

    private static async Task SendErrorAsync(
        WebSocket socket,
        string code,
        string message,
        bool retryable,
        Guid? replyTo,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        using var timeout = CreateControlFrameTimeout(cancellationToken);
        try
        {
            var bytes = ProtocolJson.Serialize(
                CreateError(code, message, retryable, replyTo));
            await socket.SendAsync(
                bytes.AsMemory(),
                WebSocketMessageType.Text,
                endOfMessage: true,
                timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            socket.Abort();
        }
        catch (WebSocketException)
        {
            socket.Abort();
        }
    }

    private static ProtocolEnvelope CreateError(
        string code,
        string message,
        bool retryable,
        Guid? replyTo) =>
        ProtocolJson.Create(
            "error",
            new
            {
                code,
                message,
                retryable,
                transferId = (Guid?)null
            },
            replyTo);

    private static async Task CloseAsync(
        WebSocket socket,
        int closeStatus,
        string description,
        CancellationToken cancellationToken)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = CreateControlFrameTimeout(cancellationToken);
        try
        {
            await socket.CloseOutputAsync(
                (WebSocketCloseStatus)closeStatus,
                description,
                timeout.Token);
        }
        catch (WebSocketException)
        {
            socket.Abort();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            socket.Abort();
        }
    }

    private static async Task CloseAsync(
        SessionLease lease,
        int closeStatus,
        string description,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateControlFrameTimeout(cancellationToken);
        await lease.CloseAsync(closeStatus, description, timeout.Token);
    }

    private static CancellationTokenSource CreateControlFrameTimeout(
        CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ControlFrameTimeout);
        return timeout;
    }

    private readonly record struct ClientDescriptor(string Name);

    private sealed class ProtocolException : Exception
    {
        public ProtocolException(string message)
            : base(message)
        {
        }

        public ProtocolException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
