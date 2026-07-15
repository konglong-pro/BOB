using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bob.Windows.Domain;
using Bob.Windows.Security;
using Bob.Windows.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class RealTransportChecks
{
    public static async Task CheckTextRoundTripAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connectionSnapshots = new List<ConnectionSnapshot>();
        var disconnected = new TaskCompletionSource<ConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.Sessions.ConnectionChanged += (_, snapshot) =>
        {
            lock (connectionSnapshots)
            {
                connectionSnapshots.Add(snapshot);
            }

            if (snapshot.Phase == SessionPhase.Offline)
            {
                disconnected.TrySetResult(snapshot);
            }
        };
        await OpenSessionAsync(host, socket, timeout.Token);

        Ensure(
            host.Sessions.Snapshot.Phase == SessionPhase.Connected
                && host.Sessions.Snapshot.PeerName == "Windows integration probe",
            "A completed real handshake must expose the connected Android peer.");
        Ensure(
            host.Sessions.Snapshot.Detail == "Secure WSS session active.",
            "A connected session must expose an explicit healthy status detail.");
        lock (connectionSnapshots)
        {
            Ensure(
                connectionSnapshots.Any(snapshot => snapshot.Phase == SessionPhase.AwaitingHello)
                    && connectionSnapshots.Any(snapshot => snapshot.Phase == SessionPhase.Connected),
                "The real handshake must publish AwaitingHello then Connected.");
            Ensure(
                connectionSnapshots.Zip(
                    connectionSnapshots.Skip(1),
                    (before, after) => after.Revision > before.Revision)
                    .All(increases => increases),
                "Connection snapshots must carry a strictly increasing revision.");
        }

        var received = new TaskCompletionSource<IncomingText>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        host.Sessions.TextReceived += (_, incoming) => received.TrySetResult(incoming);

        var textId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        await SendAsync(
            socket,
            ProtocolJson.Create(
                "text.send",
                new
                {
                    textId,
                    text = "real WebSocket text round-trip",
                    createdAt
                }),
            timeout.Token);

        var acknowledgment = await ReceiveAsync(socket, timeout.Token);
        Ensure(acknowledgment.Type == "text.ack", "Expected text.ack.");
        Ensure(
            acknowledgment.Payload.GetProperty("textId").GetGuid() == textId,
            "text.ack must identify the submitted text.");

        var incoming = await received.Task.WaitAsync(timeout.Token);
        Ensure(incoming.TextId == textId, "The submitted text must reach the session coordinator.");
        Ensure(
            incoming.Text == "real WebSocket text round-trip",
            "The submitted text body must not be changed.");

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "check_complete",
            timeout.Token);
        var offline = await disconnected.Task.WaitAsync(timeout.Token);
        Ensure(
            offline.Detail == "Phone disconnected.",
            "A closed real session must retain a visible disconnect reason.");
    }

    public static async Task CheckUnknownUploadRejectedAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var transferId = Guid.NewGuid();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/bob/v1/transfers/{transferId:D}/content")
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        request.Headers.Add("X-Bob-Transfer-Id", transferId.ToString("D"));

        using var response = await client.SendAsync(request, timeout.Token);
        Ensure(
            response.StatusCode == HttpStatusCode.NotFound,
            $"Unknown transfer PUT must return 404, received {(int)response.StatusCode}.");

        var responseBytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        Ensure(responseBytes.Length > 0, "Unknown transfer PUT must return a protocol error body.");
        using var document = JsonDocument.Parse(responseBytes);
        var error = document.RootElement.GetProperty("error");
        Ensure(
            error.GetProperty("code").GetString() == "transfer_not_found",
            "Unknown transfer PUT must return transfer_not_found.");
        Ensure(
            error.GetProperty("transferId").GetGuid() == transferId,
            "Unknown transfer PUT error must identify the requested transfer.");
        Ensure(
            !error.GetProperty("retryable").GetBoolean(),
            "Unknown transfer PUT must not be retryable with the same transfer ID.");
    }

    public static async Task CheckZeroByteUploadAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var offer = new TransferOffer(
            Guid.NewGuid(),
            RetryOf: null,
            TransferKind.File,
            "empty.bin",
            Size: 0,
            "application/octet-stream",
            DateTimeOffset.UtcNow);
        var accepted = host.Transfers.RegisterIncomingOffer(offer);
        Ensure(
            accepted.Reply == IncomingOfferReply.Accepted
                && accepted.PlannedName == "empty.bin",
            "A valid zero-byte offer must be accepted with its safe basename.");

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/bob/v1/transfers/{offer.TransferId:D}/content")
        {
            Content = new ByteArrayContent([])
        };
        request.Headers.Add("X-Bob-Transfer-Id", offer.TransferId.ToString("D"));
        using var response = await client.SendAsync(request, timeout.Token);
        Ensure(
            response.StatusCode == HttpStatusCode.Accepted,
            $"Accepted zero-byte PUT must return 202, received {(int)response.StatusCode}.");
        using (var document = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(timeout.Token)))
        {
            Ensure(
                document.RootElement.GetProperty("transferId").GetGuid() == offer.TransferId,
                "PUT response must identify the transfer.");
            Ensure(
                document.RootElement.GetProperty("bytesReceived").GetInt64() == 0,
                "PUT response must report zero received bytes.");
            Ensure(
                document.RootElement.GetProperty("state").GetString() == "verifying",
                "PUT response must enter verifying state.");
        }

        var sha256 = Base64Url(SHA256.HashData([]));
        var digestDecision = host.Transfers.ApplyIncomingDigest(
            new TransferDigest(offer.TransferId, "sha-256", sha256, Bytes: 0));
        var completed = digestDecision.Completed
            ?? throw new InvalidOperationException(
                "A matching zero-byte digest must complete the transfer.");
        Ensure(completed.Sha256 == sha256, "Completed SHA-256 must match the empty content digest.");
        Ensure(completed.StoredName == "empty.bin", "Completed storedName must match the published file.");

        var publishedPath = Path.Combine(host.ReceiveDirectory, completed.StoredName);
        Ensure(File.Exists(publishedPath), "Verified zero-byte content must be published.");
        Ensure(new FileInfo(publishedPath).Length == 0, "Published zero-byte content must remain empty.");
        Ensure(
            !File.Exists(Path.Combine(
                host.ReceiveDirectory,
                $".bob-{offer.TransferId:D}.part")),
            "Publishing must remove the transfer temporary filename.");
    }

    public static async Task CheckOrdinaryUploadAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var content = Encoding.UTF8.GetBytes("BOB ordinary file payload\n");
        var storedName = $"escaped-{Guid.NewGuid():N}.txt";
        var offer = new TransferOffer(
            Guid.NewGuid(),
            RetryOf: null,
            TransferKind.File,
            $"../{storedName}",
            content.LongLength,
            "text/plain",
            DateTimeOffset.UtcNow);
        var escapedPath = Path.Combine(host.TemporaryRoot, storedName);
        Ensure(!File.Exists(escapedPath), "The traversal sentinel must start absent.");

        var accepted = host.Transfers.RegisterIncomingOffer(offer);
        Ensure(
            accepted.Reply == IncomingOfferReply.Accepted
                && accepted.PlannedName == storedName,
            "An untrusted path must be reduced to its basename before acceptance.");

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/bob/v1/transfers/{offer.TransferId:D}/content")
        {
            Content = new ByteArrayContent(content)
        };
        request.Headers.Add("X-Bob-Transfer-Id", offer.TransferId.ToString("D"));
        using var response = await client.SendAsync(request, timeout.Token);
        Ensure(
            response.StatusCode == HttpStatusCode.Accepted,
            $"Accepted ordinary PUT must return 202, received {(int)response.StatusCode}.");

        var sha256 = Base64Url(SHA256.HashData(content));
        var completed = host.Transfers.ApplyIncomingDigest(
                new TransferDigest(
                    offer.TransferId,
                    "sha-256",
                    sha256,
                    content.LongLength))
            .Completed
            ?? throw new InvalidOperationException(
                "A matching ordinary-file digest must complete the transfer.");
        Ensure(completed.Sha256 == sha256, "Completed SHA-256 must match the uploaded bytes.");
        Ensure(completed.StoredName == storedName, "Completed storedName must be the safe basename.");

        var publishedPath = Path.Combine(host.ReceiveDirectory, storedName);
        Ensure(File.Exists(publishedPath), "Verified ordinary content must be published.");
        Ensure(
            File.ReadAllBytes(publishedPath).SequenceEqual(content),
            "Published ordinary content must exactly match the upload.");
        Ensure(
            !File.Exists(escapedPath),
            "An untrusted path must never publish outside the receive directory.");
    }

    public static async Task CheckSameNameUploadAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var incomingBytes = Encoding.UTF8.GetBytes("new transfer content");
        var existingBytes = Encoding.UTF8.GetBytes("existing user content");
        var offer = new TransferOffer(
            Guid.NewGuid(),
            RetryOf: null,
            TransferKind.File,
            "duplicate.txt",
            incomingBytes.LongLength,
            "text/plain",
            DateTimeOffset.UtcNow);
        var accepted = host.Transfers.RegisterIncomingOffer(offer);
        Ensure(
            accepted.Reply == IncomingOfferReply.Accepted
                && accepted.PlannedName == "duplicate.txt",
            "The initially free filename must be planned without a suffix.");

        var originalPath = Path.Combine(host.ReceiveDirectory, "duplicate.txt");
        await File.WriteAllBytesAsync(originalPath, existingBytes, timeout.Token);

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/bob/v1/transfers/{offer.TransferId:D}/content")
        {
            Content = new ByteArrayContent(incomingBytes)
        };
        request.Headers.Add("X-Bob-Transfer-Id", offer.TransferId.ToString("D"));
        using var response = await client.SendAsync(request, timeout.Token);
        Ensure(
            response.StatusCode == HttpStatusCode.Accepted,
            $"Accepted same-name PUT must return 202, received {(int)response.StatusCode}.");

        var sha256 = Base64Url(SHA256.HashData(incomingBytes));
        var completed = host.Transfers.ApplyIncomingDigest(
                new TransferDigest(
                    offer.TransferId,
                    "sha-256",
                    sha256,
                    incomingBytes.LongLength))
            .Completed
            ?? throw new InvalidOperationException(
                "A matching same-name digest must complete the transfer.");
        Ensure(
            completed.StoredName == "duplicate (1).txt",
            "A late same-name file must force a suffixed storedName.");
        Ensure(
            File.ReadAllBytes(originalPath).SequenceEqual(existingBytes),
            "Publishing must never overwrite the existing user file.");
        Ensure(
            File.ReadAllBytes(Path.Combine(host.ReceiveDirectory, completed.StoredName))
                .SequenceEqual(incomingBytes),
            "The transferred content must be published under the suffixed name.");
    }

    public static async Task CheckWebSocketUploadRoundTripAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await OpenSessionAsync(host, socket, timeout.Token);

        var content = Encoding.UTF8.GetBytes("full WebSocket and HTTP upload");
        var transferId = Guid.NewGuid();
        await SendAsync(
            socket,
            ProtocolJson.Create(
                "transfer.offer",
                new
                {
                    transferId,
                    retryOf = (Guid?)null,
                    kind = "file",
                    name = "full-round-trip.txt",
                    size = content.LongLength,
                    mediaType = "text/plain",
                    createdAt = DateTimeOffset.UtcNow
                }),
            timeout.Token);

        var accepted = await ReceiveAsync(socket, timeout.Token);
        Ensure(accepted.Type == "transfer.accepted", "A valid offer must receive transfer.accepted.");
        Ensure(
            accepted.Payload.GetProperty("transferId").GetGuid() == transferId,
            "transfer.accepted must identify the offered transfer.");
        Ensure(
            accepted.Payload.GetProperty("plannedName").GetString() == "full-round-trip.txt",
            "transfer.accepted must return the planned safe filename.");

        using (var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/bob/v1/transfers/{transferId:D}/content")
        {
            Content = new ByteArrayContent(content)
        })
        {
            request.Headers.Add("X-Bob-Transfer-Id", transferId.ToString("D"));
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            Ensure(
                response.StatusCode == HttpStatusCode.Accepted,
                $"Accepted WebSocket-offered PUT must return 202, received {(int)response.StatusCode}.");
        }

        var sha256 = Base64Url(SHA256.HashData(content));
        await SendAsync(
            socket,
            ProtocolJson.Create(
                "transfer.digest",
                new
                {
                    transferId,
                    algorithm = "sha-256",
                    value = sha256,
                    bytes = content.LongLength
                }),
            timeout.Token);

        var completed = await ReceiveAsync(socket, timeout.Token);
        Ensure(completed.Type == "transfer.completed", "A matching digest must receive transfer.completed.");
        Ensure(
            completed.Payload.GetProperty("transferId").GetGuid() == transferId,
            "transfer.completed must identify the uploaded transfer.");
        Ensure(
            completed.Payload.GetProperty("bytes").GetInt64() == content.LongLength
                && completed.Payload.GetProperty("sha256").GetString() == sha256,
            "transfer.completed must report the verified byte count and SHA-256.");
        var storedName = completed.Payload.GetProperty("storedName").GetString();
        Ensure(storedName == "full-round-trip.txt", "transfer.completed storedName is incorrect.");
        Ensure(
            File.ReadAllBytes(Path.Combine(host.ReceiveDirectory, storedName!))
                .SequenceEqual(content),
            "The full protocol round-trip must publish the exact content.");

        await SendAsync(
            socket,
            ProtocolJson.Create(
                "transfer.terminalAck",
                new
                {
                    transferId,
                    state = "completed"
                }),
            timeout.Token);

        var sentinelTextId = Guid.NewGuid();
        await SendAsync(
            socket,
            ProtocolJson.Create(
                "text.send",
                new
                {
                    textId = sentinelTextId,
                    text = "terminal ack accepted",
                    createdAt = DateTimeOffset.UtcNow
                }),
            timeout.Token);
        var sentinelAck = await ReceiveAsync(socket, timeout.Token);
        Ensure(
            sentinelAck.Type == "text.ack"
                && sentinelAck.Payload.GetProperty("textId").GetGuid() == sentinelTextId,
            "A valid terminalAck must leave the session open without a queued protocol error.");

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "check_complete",
            timeout.Token);
    }

    public static async Task CheckWebSocketDownloadRoundTripAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await OpenSessionAsync(host, socket, timeout.Token);

        var sourceBytes = Encoding.UTF8.GetBytes("Windows to Android content");
        var sourcePath = Path.Combine(host.TemporaryRoot, "outgoing.txt");
        await File.WriteAllBytesAsync(sourcePath, sourceBytes, timeout.Token);
        var queuedOffer = host.Transfers.EnqueueOutgoing(sourcePath, TransferKind.File);
        Ensure(
            await host.Sessions.SendNextTransferOfferAsync(host.Transfers, timeout.Token),
            "A connected session must send the queued outgoing offer.");

        var offered = await ReceiveAsync(socket, timeout.Token);
        Ensure(offered.Type == "transfer.offer", "The client must receive transfer.offer.");
        Ensure(
            offered.Payload.GetProperty("transferId").GetGuid() == queuedOffer.TransferId
                && offered.Payload.GetProperty("name").GetString() == "outgoing.txt"
                && offered.Payload.GetProperty("size").GetInt64() == sourceBytes.LongLength,
            "transfer.offer must describe the queued source file.");

        using (var prematureResponse = await client.GetAsync(
            $"/bob/v1/transfers/{queuedOffer.TransferId:D}/content",
            timeout.Token))
        {
            Ensure(
                prematureResponse.StatusCode == HttpStatusCode.Conflict,
                $"GET before accepted must return 409, received {(int)prematureResponse.StatusCode}.");
            using var document = JsonDocument.Parse(
                await prematureResponse.Content.ReadAsByteArrayAsync(timeout.Token));
            var error = document.RootElement.GetProperty("error");
            Ensure(
                error.GetProperty("code").GetString() == "invalid_state"
                    && error.GetProperty("transferId").GetGuid() == queuedOffer.TransferId,
                "Premature GET must return an invalid_state protocol error.");
        }

        await SendAsync(
            socket,
            ProtocolJson.Create(
                "transfer.accepted",
                new
                {
                    transferId = queuedOffer.TransferId,
                    plannedName = "phone-copy.txt"
                }),
            timeout.Token);

        using var response = await client.GetAsync(
            $"/bob/v1/transfers/{queuedOffer.TransferId:D}/content",
            timeout.Token);
        Ensure(
            response.StatusCode == HttpStatusCode.OK,
            $"GET after accepted must return 200, received {(int)response.StatusCode}.");
        Ensure(
            response.Content.Headers.ContentLength == sourceBytes.LongLength
                && response.Content.Headers.ContentType?.MediaType == "text/plain",
            "Successful GET must preserve size and media type.");
        Ensure(
            response.Headers.TryGetValues("X-Bob-Transfer-Id", out var transferHeaders)
                && transferHeaders.Single() == queuedOffer.TransferId.ToString("D"),
            "Successful GET must identify the transfer in its response header.");
        Ensure(
            (await response.Content.ReadAsByteArrayAsync(timeout.Token)).SequenceEqual(sourceBytes),
            "Successful GET must stream the exact source bytes.");

        var sha256 = Base64Url(SHA256.HashData(sourceBytes));
        var digest = await ReceiveAsync(socket, timeout.Token);
        Ensure(digest.Type == "transfer.digest", "Completed GET must emit transfer.digest.");
        Ensure(
            digest.Payload.GetProperty("transferId").GetGuid() == queuedOffer.TransferId
                && digest.Payload.GetProperty("algorithm").GetString() == "sha-256"
                && digest.Payload.GetProperty("value").GetString() == sha256
                && digest.Payload.GetProperty("bytes").GetInt64() == sourceBytes.LongLength,
            "GET digest must match the streamed source bytes.");

        await SendAsync(
            socket,
            ProtocolJson.Create(
                "transfer.completed",
                new
                {
                    transferId = queuedOffer.TransferId,
                    bytes = sourceBytes.LongLength,
                    sha256,
                    storedName = "phone-copy.txt",
                    completedAt = DateTimeOffset.UtcNow
                }),
            timeout.Token);
        var terminalAck = await ReceiveAsync(socket, timeout.Token);
        Ensure(
            terminalAck.Type == "transfer.terminalAck"
                && terminalAck.Payload.GetProperty("transferId").GetGuid()
                    == queuedOffer.TransferId
                && terminalAck.Payload.GetProperty("state").GetString() == "completed",
            "A valid remote completion must receive a matching terminalAck.");

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "check_complete",
            timeout.Token);
    }

    private static async Task SendAsync(
        ClientWebSocket socket,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var bytes = ProtocolJson.Serialize(envelope);
        await socket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task OpenSessionAsync(
        TestBobHost host,
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        try
        {
            await socket.ConnectAsync(host.WebSocketUri, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Unable to connect to dynamic WebSocket endpoint {host.WebSocketUri}: {exception}",
                exception);
        }

        var hello = ProtocolJson.Create(
            "session.hello",
            new
            {
                protocolMin = BobProtocol.Version,
                protocolMax = BobProtocol.Version,
                client = new
                {
                    installationId = Guid.NewGuid(),
                    name = "Windows integration probe",
                    platform = "android",
                    osVersion = "14",
                    appVersion = "1.0.0"
                }
            });
        await SendAsync(socket, hello, cancellationToken);

        var welcome = await ReceiveAsync(socket, cancellationToken);
        Ensure(welcome.Type == "session.welcome", "Expected session.welcome.");
        Ensure(welcome.ReplyTo == hello.Id, "session.welcome replyTo must match hello.");
        Ensure(
            welcome.Payload.GetProperty("server").GetProperty("id").GetGuid()
                == host.ServerId,
            "session.welcome server ID must match the host identity.");

        var snapshot = await ReceiveAsync(socket, cancellationToken);
        Ensure(snapshot.Type == "session.snapshot", "Expected session.snapshot.");
        Ensure(
            snapshot.Payload.GetProperty("page").GetInt32() == 0
                && snapshot.Payload.GetProperty("isLast").GetBoolean(),
            "The initial snapshot must contain a final page zero.");
    }

    private static async Task<ProtocolEnvelope> ReceiveAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            Ensure(
                result.MessageType == WebSocketMessageType.Text,
                $"Expected a text frame, received {result.MessageType}.");
            message.Write(buffer, 0, result.Count);
            Ensure(
                message.Length <= BobProtocol.MaxEnvelopeBytes,
                "Received envelope exceeded the protocol limit.");
            if (result.EndOfMessage)
            {
                return ProtocolJson.Deserialize(message.ToArray());
            }
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class TestBobHost : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly ServerIdentity _identity;
        private readonly string _temporaryRoot;

        private TestBobHost(
            WebApplication application,
            ServerIdentity identity,
            Uri baseUri,
            SessionCoordinator sessions,
            string temporaryRoot)
        {
            _application = application;
            _identity = identity;
            _temporaryRoot = temporaryRoot;
            BaseUri = baseUri;
            Sessions = sessions;
        }

        public Guid ServerId => _identity.ServerId;

        public Uri BaseUri { get; }

        public Uri WebSocketUri => new UriBuilder(BaseUri)
        {
            Scheme = "ws",
            Path = "/bob/v1/ws"
        }.Uri;

        public SessionCoordinator Sessions { get; }

        public TransferCoordinator Transfers =>
            _application.Services.GetRequiredService<TransferCoordinator>();

        public string ReceiveDirectory => Transfers.ReceiveDirectory;

        public string TemporaryRoot => _temporaryRoot;

        public static async Task<TestBobHost> StartAsync()
        {
            var temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                $"bob-windows-checks-{Guid.NewGuid():N}");
            var receiveDirectory = Path.Combine(temporaryRoot, "received");
            Directory.CreateDirectory(temporaryRoot);
            var serverId = Guid.NewGuid();
            var identity = new ServerIdentity(
                serverId,
                ServerCertificateFactory.Create(serverId, "localhost", DateTimeOffset.UtcNow));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(BobEndpoints).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                });
            });
            builder.Services.AddSingleton(identity);
            builder.Services.AddSingleton<SessionCoordinator>();
            builder.Services.AddSingleton(new TransferCoordinator(receiveDirectory));
            builder.Services.AddSingleton<WebSocketSessionHandler>();

            var application = builder.Build();
            BobEndpoints.Map(application);

            try
            {
                await application.StartAsync();
                var addresses = application.Services
                    .GetRequiredService<IServer>()
                    .Features
                    .Get<IServerAddressesFeature>()
                    ?.Addresses;
                var address = addresses?.SingleOrDefault()
                    ?? throw new InvalidOperationException("Kestrel did not publish its dynamic address.");
                return new TestBobHost(
                    application,
                    identity,
                    new Uri(address),
                    application.Services.GetRequiredService<SessionCoordinator>(),
                    temporaryRoot);
            }
            catch
            {
                await application.DisposeAsync();
                TryDeleteDirectory(temporaryRoot);
                throw;
            }
        }

        public ClientWebSocket CreateWebSocketClient()
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(BobProtocol.Subprotocol);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _application.StopAsync(timeout.Token);
            await _application.DisposeAsync();
            TryDeleteDirectory(_temporaryRoot);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
