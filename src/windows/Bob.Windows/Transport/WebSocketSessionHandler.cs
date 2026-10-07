using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bob.Windows.Domain;
using Bob.Windows.Persistence;
using Bob.Windows.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bob.Windows.Transport;

public sealed class WebSocketSessionHandler
{
    private const int ReceiveBufferBytes = 16 * 1024;
    private static readonly TimeSpan ControlFrameTimeout = TimeSpan.FromSeconds(2);
    private readonly SessionCoordinator _sessions;
    private readonly TransferCoordinator _transfers;
    private readonly ServerIdentity _identity;
    private readonly ILogger<WebSocketSessionHandler> _logger;

    public WebSocketSessionHandler(
        SessionCoordinator sessions,
        TransferCoordinator transfers,
        ServerIdentity identity,
        ILogger<WebSocketSessionHandler> logger)
    {
        _sessions = sessions;
        _transfers = transfers;
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
                lease.SetDisconnectReason("Phone closed the connection before sending session.hello.");
                return;
            }

            if (hello.V != BobProtocol.Version
                || !string.Equals(hello.Type, "session.hello", StringComparison.Ordinal))
            {
                lease.SetDisconnectReason("Phone did not send a valid BOB session hello.");
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
                lease.SetDisconnectReason("Phone sent invalid BOB session details.");
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
                lease.SetDisconnectReason("Phone and computer use incompatible BOB protocol versions.");
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
            foreach (var snapshot in CreateSnapshots())
            {
                await lease.SendAsync(snapshot, context.RequestAborted);
            }
            lease.CompleteHandshake(client.Name);
            await _sessions.ReplayPendingTextsAsync(context.RequestAborted);
            foreach (var replay in _transfers.ReadReplayEnvelopes())
            {
                await lease.SendAsync(replay, context.RequestAborted);
            }
            await SendNextOutgoingOfferAsync(lease, context.RequestAborted);

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
            lease.SetDisconnectReason("Phone disconnected.");
        }
        catch (OperationCanceledException)
        {
            lease.SetDisconnectReason("Phone did not send session.hello within 5 seconds.");
            await CloseAsync(lease, 4408, "hello_timeout", CancellationToken.None);
        }
        catch (ProtocolException exception)
        {
            lease.SetDisconnectReason("Phone sent an invalid BOB protocol message.");
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
            lease.SetDisconnectReason("Phone WebSocket connection was interrupted.");
            _logger.LogDebug(exception, "BOB WSS peer disconnected.");
        }
        catch (IOException)
        {
            lease.SetDisconnectReason("BOB could not persist the session state. Reconnect to try again.");
            await CloseAsync(lease, 4500, "storage_failure", CancellationToken.None);
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
            _transfers.HandleSessionDisconnected();
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

        try
        {
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
                case "transfer.offer":
                    await HandleTransferOfferAsync(lease, envelope, cancellationToken);
                    break;
                case "transfer.accepted":
                    await HandleTransferAcceptedAsync(lease, envelope, cancellationToken);
                    break;
                case "transfer.digest":
                    await HandleTransferDigestAsync(lease, envelope, cancellationToken);
                    break;
                case "transfer.completed":
                    await HandleTransferCompletedAsync(lease, envelope, cancellationToken);
                    break;
                case "transfer.terminalAck":
                    await HandleTransferTerminalAckAsync(
                        lease,
                        envelope,
                        cancellationToken);
                    break;
                case "transfer.failed":
                    await HandleTransferFailedAsync(lease, envelope, cancellationToken);
                    break;
                case "transfer.progress":
                    HandleTransferProgress(envelope);
                    break;
                case "transfer.cancel":
                    await HandleTransferCancelAsync(lease, envelope, cancellationToken);
                    break;
                case "transfer.canceled":
                    await HandleTransferCanceledAsync(lease, envelope, cancellationToken);
                    break;
                case "error":
                    HandlePeerError(envelope.Payload);
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
        catch (TransferProtocolException exception)
        {
            await SendErrorAsync(
                lease,
                exception.Code,
                exception.Message,
                exception.Retryable,
                envelope.Id,
                cancellationToken,
                exception.TransferId);
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

        // Persistence errors leave the original ID unacknowledged and terminate
        // this session through the outer storage_failure handler. A fresh session
        // can safely replay it after storage becomes writable again.
        var registration = _sessions.RegisterIncomingText(textId, text, createdAt, peerName);
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

    private void HandlePeerError(JsonElement payload)
    {
        var code = payload.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        // Only a small protocol identifier is safe to log. The peer's diagnostic
        // message may contain text or local paths and is intentionally never logged.
        if (code is null || code.Length is < 1 or > 64
            || !code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
        {
            code = "invalid_error";
        }
        _logger.LogInformation("BOB peer returned protocol error {Code}.", code);
        // An error describes a rejected message; it is not a transfer terminal.
        // Replying with another generic error would create an error echo loop.
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

    private async Task HandleTransferOfferAsync(
        SessionLease lease,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var offer = TransferProtocolParser.ParseOffer(envelope.Payload);
        var decision = _transfers.RegisterIncomingOffer(offer);
        var response = decision.Reply switch
        {
            IncomingOfferReply.Accepted => TransferProtocolMessages.Accepted(
                offer.TransferId,
                decision.PlannedName!),
            IncomingOfferReply.Completed => TransferProtocolMessages.Completed(
                decision.Completed!),
            IncomingOfferReply.Failed => TransferProtocolMessages.Failed(
                decision.Failure!),
            IncomingOfferReply.Canceled => _transfers.ReadTerminalOrCancelEnvelope(offer.TransferId)!,
            _ => throw new InvalidOperationException("Unknown incoming offer decision.")
        };
        await lease.SendAsync(response, cancellationToken);
    }

    private Task HandleTransferAcceptedAsync(
        SessionLease lease,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var accepted = TransferProtocolParser.ParseAccepted(envelope.Payload);
        _transfers.AcceptOutgoing(accepted.TransferId, accepted.PlannedName);
        return Task.CompletedTask;
    }

    private async Task HandleTransferDigestAsync(
        SessionLease lease,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var digest = TransferProtocolParser.ParseDigest(envelope.Payload);
        var decision = _transfers.ApplyIncomingDigest(digest);
        if (decision.Completed is not null)
        {
            await lease.SendAsync(TransferProtocolMessages.Completed(decision.Completed), cancellationToken);
        }
        else if (decision.Failure is not null)
        {
            await lease.SendAsync(TransferProtocolMessages.Failed(decision.Failure), cancellationToken);
        }
        else if (decision.Canceled is not null)
        {
            await lease.SendAsync(_transfers.ReadTerminalOrCancelEnvelope(digest.TransferId)!, cancellationToken);
        }
    }

    private async Task HandleTransferCompletedAsync(
        SessionLease lease,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var completed = TransferProtocolParser.ParseCompleted(envelope.Payload);
        await _transfers.ApplyRemoteCompletedAsync(completed, cancellationToken);
        await lease.SendAsync(
            TransferProtocolMessages.TerminalAck(
                completed.TransferId,
                TransferState.Completed),
            cancellationToken);
        await SendNextOutgoingOfferAsync(lease, cancellationToken);
    }

    private async Task HandleTransferTerminalAckAsync(
        SessionLease lease,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var acknowledgement = TransferProtocolParser.ParseTerminalAck(envelope.Payload);
        _transfers.AcknowledgeTerminal(
            acknowledgement.TransferId,
            acknowledgement.State);
        await SendNextOutgoingOfferAsync(lease, cancellationToken);
    }

    private async Task HandleTransferFailedAsync(
        SessionLease lease,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var failure = TransferProtocolParser.ParseFailure(envelope.Payload);
        _transfers.ApplyRemoteFailure(failure);
        await lease.SendAsync(
            TransferProtocolMessages.TerminalAck(
                failure.TransferId,
                TransferState.Failed),
            cancellationToken);
        await SendNextOutgoingOfferAsync(lease, cancellationToken);
    }

    private void HandleTransferProgress(ProtocolEnvelope envelope)
    {
        var progress = TransferProtocolParser.ParseProgress(envelope.Payload);
        _transfers.ApplyRemoteProgress(
            progress.TransferId,
            progress.Bytes,
            progress.Total);
    }

    private async Task HandleTransferCancelAsync(SessionLease lease, ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var transferId = TransferProtocolParser.ParseCancel(envelope.Payload);
        _transfers.Cancel(transferId, requestedByPeer: true);
        await lease.SendAsync(_transfers.ReadTerminalOrCancelEnvelope(transferId)!, cancellationToken);
        await SendNextOutgoingOfferAsync(lease, cancellationToken);
    }

    private async Task HandleTransferCanceledAsync(SessionLease lease, ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var canceled = TransferProtocolParser.ParseCanceled(envelope.Payload);
        var existing = _transfers.ReadTerminalOrCancelEnvelope(canceled.TransferId);
        if (existing?.Type == "transfer.completed")
        {
            await lease.SendAsync(existing, cancellationToken);
            return;
        }
        _transfers.ApplyRemoteCanceled(canceled);
        await lease.SendAsync(TransferProtocolMessages.TerminalAck(canceled.TransferId,
            TransferState.Canceled), cancellationToken);
        await SendNextOutgoingOfferAsync(lease, cancellationToken);
    }

    private async Task SendNextOutgoingOfferAsync(
        SessionLease lease,
        CancellationToken cancellationToken)
    {
        var next = _transfers.TryOfferNextOutgoing();
        if (next is not null)
        {
            try
            {
                await lease.SendAsync(
                TransferProtocolMessages.Offer(next),
                cancellationToken);
            }
            catch
            {
                _transfers.ReturnOutgoingOfferToQueue(next.TransferId);
                throw;
            }
        }
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

    private IReadOnlyList<ProtocolEnvelope> CreateSnapshots()
    {
        var snapshotId = Guid.NewGuid();
        var pages = new List<List<object>> { new() };
        foreach (var item in _transfers.ReadSessionSnapshotItems())
        {
            var page = pages[^1];
            page.Add(item);
            if (ProtocolJson.Serialize(CreateSnapshotPage(snapshotId, pages.Count - 1,
                isLast: false, page)).Length <= BobProtocol.MaxSnapshotEnvelopeBytes) { continue; }
            page.RemoveAt(page.Count - 1);
            if (page.Count == 0)
            {
                throw new ProtocolException("Transfer metadata exceeds the snapshot page limit.");
            }
            pages.Add(new List<object> { item });
        }
        return pages.Select((page, index) => CreateSnapshotPage(snapshotId, index,
            index == pages.Count - 1, page)).ToArray();
    }

    private static ProtocolEnvelope CreateSnapshotPage(Guid snapshotId, int page, bool isLast,
        IReadOnlyList<object> transfers) => ProtocolJson.Create("session.snapshot", new
        {
            snapshotId,
            page,
            isLast,
            transfers
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
            || minValue.ValueKind != JsonValueKind.Number
            || !minValue.TryGetInt32(out var minimum)
            || !payload.TryGetProperty("protocolMax", out var maxValue)
            || maxValue.ValueKind != JsonValueKind.Number
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
        CancellationToken cancellationToken,
        Guid? transferId = null)
    {
        if (lease.Socket.State != WebSocketState.Open)
        {
            return;
        }

        using var timeout = CreateControlFrameTimeout(cancellationToken);
        try
        {
            await lease.SendAsync(
                CreateError(code, message, retryable, replyTo, transferId),
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
        Guid? replyTo,
        Guid? transferId = null) =>
        ProtocolJson.Create(
            "error",
            new
            {
                code,
                message,
                retryable,
                transferId
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
