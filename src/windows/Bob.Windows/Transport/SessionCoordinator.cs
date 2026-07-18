using System.Net.WebSockets;
using Bob.Windows.Domain;
using Bob.Windows.Persistence;

namespace Bob.Windows.Transport;

public sealed record ConnectionSnapshot(
    SessionPhase Phase,
    string? PeerName,
    string Detail,
    long Revision)
{
    public bool IsConnected => Phase == SessionPhase.Connected;
}

public sealed record IncomingText(
    Guid TextId,
    string Text,
    DateTimeOffset CreatedAt,
    string PeerName);

internal enum IncomingTextRegistration
{
    Added,
    Duplicate,
    Conflict
}

public sealed class SessionCoordinator
{
    private readonly object _sync = new();
    private readonly SessionStateMachine _stateMachine = new();
    private readonly Dictionary<Guid, IncomingText> _receivedTexts = new();
    private readonly TextHistoryStore? _textHistory;
    private SessionConnection? _active;
    private string? _peerName;
    private string _detail = $"BOB server is ready on port {BobProtocol.DefaultPort}.";
    private long _revision;

    public event EventHandler<ConnectionSnapshot>? ConnectionChanged;

    public event EventHandler<IncomingText>? TextReceived;

    public event EventHandler<Guid>? TextAcknowledged;

    public SessionCoordinator()
    {
    }

    public SessionCoordinator(TextHistoryStore textHistory)
    {
        ArgumentNullException.ThrowIfNull(textHistory);
        _textHistory = textHistory;
        foreach (var record in textHistory.ReadAll().Where(record => !record.Outgoing))
        {
            _receivedTexts.TryAdd(
                record.TextId,
                new IncomingText(
                    record.TextId,
                    record.Text,
                    record.CreatedAt,
                    record.PeerName));
        }
    }

    public ConnectionSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return CreateSnapshot();
            }
        }
    }

    internal SessionLease? TryOpen(WebSocket socket)
    {
        ConnectionSnapshot snapshot;
        SessionLease lease;

        lock (_sync)
        {
            if (_active is not null)
            {
                return null;
            }

            _active = new SessionConnection(socket);
            _stateMachine.BeginHandshake();
            _detail = "Secure WebSocket opened; waiting for Android hello.";
            _revision += 1;
            lease = new SessionLease(this, _active);
            snapshot = CreateSnapshot();
        }

        ConnectionChanged?.Invoke(this, snapshot);
        return lease;
    }

    public async Task SendTextAsync(
        Guid textId,
        string text,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        var envelope = ProtocolJson.Create(
            "text.send",
            new
            {
                textId,
                text,
                createdAt
            });
        await SendEnvelopeAsync(envelope, cancellationToken);
    }

    public async Task SendEnvelopeAsync(
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        SessionConnection connection;

        lock (_sync)
        {
            if (_stateMachine.Phase != SessionPhase.Connected || _active is null)
            {
                throw new InvalidOperationException("No phone is connected.");
            }

            connection = _active;
        }

        await connection.SendAsync(envelope, cancellationToken);
    }

    public async Task<bool> SendNextTransferOfferAsync(
        TransferCoordinator transfers,
        CancellationToken cancellationToken = default)
    {
        var offer = transfers.TryOfferNextOutgoing();
        if (offer is null)
        {
            return false;
        }

        try
        {
            await SendEnvelopeAsync(
                TransferProtocolMessages.Offer(offer),
                cancellationToken);
            return true;
        }
        catch
        {
            transfers.FailLocal(
                offer.TransferId,
                "network_interrupted",
                "The transfer offer could not be sent.",
                retryable: true);
            throw;
        }
    }

    internal void CompleteHandshake(SessionLease lease, string peerName)
    {
        ConnectionSnapshot snapshot;

        lock (_sync)
        {
            EnsureCurrent(lease);
            _peerName = peerName;
            _stateMachine.CompleteHandshake();
            _detail = "Secure WSS session active.";
            _revision += 1;
            snapshot = CreateSnapshot();
        }

        ConnectionChanged?.Invoke(this, snapshot);
    }

    internal IncomingTextRegistration RegisterIncomingText(
        Guid textId,
        string text,
        DateTimeOffset createdAt,
        string peerName)
    {
        IncomingText incoming;

        lock (_sync)
        {
            if (_receivedTexts.TryGetValue(textId, out var existing))
            {
                return existing.Text == text && existing.CreatedAt == createdAt
                    ? IncomingTextRegistration.Duplicate
                    : IncomingTextRegistration.Conflict;
            }

            incoming = new IncomingText(textId, text, createdAt, peerName);
            if (_textHistory is not null)
            {
                try
                {
                    var saved = _textHistory.Save(new TextHistoryRecord(
                        textId,
                        text,
                        createdAt,
                        DateTimeOffset.UtcNow,
                        Outgoing: false,
                        Status: "Received",
                        PeerName: peerName));
                    if (!saved.IsNew)
                    {
                        _receivedTexts.TryAdd(textId, incoming);
                        return IncomingTextRegistration.Duplicate;
                    }
                }
                catch (TextHistoryConflictException)
                {
                    return IncomingTextRegistration.Conflict;
                }
            }

            _receivedTexts.Add(textId, incoming);
        }

        TextReceived?.Invoke(this, incoming);
        return IncomingTextRegistration.Added;
    }

    internal void PublishTextAcknowledged(Guid textId) =>
        TextAcknowledged?.Invoke(this, textId);

    internal void Release(SessionLease lease)
    {
        ConnectionSnapshot? snapshot = null;

        lock (_sync)
        {
            if (ReferenceEquals(_active, lease.Connection))
            {
                var wasConnected = _stateMachine.Phase == SessionPhase.Connected;
                _active = null;
                _peerName = null;
                _stateMachine.Reset();
                _detail = lease.DisconnectReason
                    ?? (wasConnected
                        ? "Phone disconnected."
                        : "Phone connection ended before verification completed.");
                _revision += 1;
                snapshot = CreateSnapshot();
            }
        }

        if (snapshot is not null)
        {
            ConnectionChanged?.Invoke(this, snapshot);
        }
    }

    private ConnectionSnapshot CreateSnapshot() =>
        new(_stateMachine.Phase, _peerName, _detail, _revision);

    private void EnsureCurrent(SessionLease lease)
    {
        if (!ReferenceEquals(_active, lease.Connection))
        {
            throw new InvalidOperationException("Session lease is no longer active.");
        }
    }

    internal sealed class SessionConnection
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public SessionConnection(WebSocket socket)
        {
            Socket = socket;
        }

        public WebSocket Socket { get; }

        public async Task SendAsync(
            ProtocolEnvelope envelope,
            CancellationToken cancellationToken)
        {
            var bytes = ProtocolJson.Serialize(envelope);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Socket.SendAsync(
                    bytes.AsMemory(),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync(
            int closeStatus,
            string description,
            CancellationToken cancellationToken)
        {
            var lockTaken = false;
            try
            {
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockTaken = true;

                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await Socket.CloseOutputAsync(
                        (WebSocketCloseStatus)closeStatus,
                        description,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (WebSocketException)
            {
                Socket.Abort();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Socket.Abort();
            }
            finally
            {
                if (lockTaken)
                {
                    _sendLock.Release();
                }
            }
        }
    }
}

internal sealed class SessionLease : IDisposable
{
    private readonly SessionCoordinator _owner;
    private int _disposed;

    public SessionLease(
        SessionCoordinator owner,
        SessionCoordinator.SessionConnection connection)
    {
        _owner = owner;
        Connection = connection;
    }

    internal SessionCoordinator.SessionConnection Connection { get; }

    internal string? DisconnectReason { get; private set; }

    public WebSocket Socket => Connection.Socket;

    public Task SendAsync(
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken) =>
        Connection.SendAsync(envelope, cancellationToken);

    public Task CloseAsync(
        int closeStatus,
        string description,
        CancellationToken cancellationToken) =>
        Connection.CloseAsync(closeStatus, description, cancellationToken);

    public void CompleteHandshake(string peerName) =>
        _owner.CompleteHandshake(this, peerName);

    public void SetDisconnectReason(string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            DisconnectReason = reason;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Release(this);
        }
    }
}
