using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Bob.Windows;
using Bob.Windows.Discovery;
using Bob.Windows.Domain;
using Bob.Windows.Persistence;
using Bob.Windows.Security;
using Bob.Windows.Storage;
using Bob.Windows.Transport;
using Bob.Windows.UI;

var checks = new (string Name, Action Run)[]
{
    ("protocol JSON shape", CheckProtocolJson),
    ("malformed protocol envelope rejection", CheckMalformedProtocolEnvelope),
    ("incoming text process idempotency", CheckIncomingTextIdempotency),
    ("session state transitions", CheckSessionStateTransitions),
    ("server certificate profile", CheckCertificateProfile),
    ("mDNS service descriptor", CheckMdnsServiceDescriptor),
    ("reserved transfer filenames", CheckReservedTransferFilenames),
    ("receive directory setting persists", CheckReceiveDirectorySettingPersists),
    ("text history persists and exposes the latest 20 messages", CheckTextHistoryPersistence),
    ("active incoming transfer freezes its receive directory", CheckFrozenReceiveDirectory),
    ("image timeline exposes an explicit local preview", CheckImageTimelinePreview),
    ("stale transfer snapshots cannot replace completed state", CheckTransferSnapshotRevisionRace),
    ("bounded WebSocket close", CheckBoundedWebSocketClose),
    ("real WebSocket text round-trip", () =>
        RealTransportChecks.CheckTextRoundTripAsync().GetAwaiter().GetResult()),
    ("unknown upload is rejected by the transfer endpoint", () =>
        RealTransportChecks.CheckUnknownUploadRejectedAsync().GetAwaiter().GetResult()),
    ("zero-byte upload is verified and published", () =>
        RealTransportChecks.CheckZeroByteUploadAsync().GetAwaiter().GetResult()),
    ("ordinary upload is hashed and confined to the receive directory", () =>
        RealTransportChecks.CheckOrdinaryUploadAsync().GetAwaiter().GetResult()),
    ("publish never overwrites a late same-name file", () =>
        RealTransportChecks.CheckSameNameUploadAsync().GetAwaiter().GetResult()),
    ("WebSocket and HTTP complete an upload protocol round-trip", () =>
        RealTransportChecks.CheckWebSocketUploadRoundTripAsync().GetAwaiter().GetResult()),
    ("download is state-gated and completes across WebSocket and HTTP", () =>
        RealTransportChecks.CheckWebSocketDownloadRoundTripAsync().GetAwaiter().GetResult())
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

static void CheckReservedTransferFilenames()
{
    Assert(
        TransferFileNames.Sanitize("CON.foo.bar") == "_CON.foo.bar",
        "A reserved device name must remain protected when multiple extensions are present.");
    Assert(
        TransferFileNames.Sanitize("LPT1.any.ext") == "_LPT1.any.ext",
        "A reserved numbered device name must remain protected when multiple extensions are present.");
}

static void CheckReceiveDirectorySettingPersists()
{
    var root = CreateCheckDirectory();
    try
    {
        var appRoot = Path.Combine(root, "local-app-data", "BOB");
        var defaultReceiveDirectory = Path.Combine(root, "default-receive");
        var selectedReceiveDirectory = Path.Combine(root, "selected-receive");
        var paths = new AppPaths(appRoot, Path.Combine(appRoot, "identity"));

        var settings = new ReceiveDirectorySettings(paths, defaultReceiveDirectory);
        Assert(
            settings.ReceiveDirectory == Path.GetFullPath(defaultReceiveDirectory),
            "missing settings must use the default Downloads\\BOB-equivalent directory.");

        settings.SetReceiveDirectory(selectedReceiveDirectory);
        Assert(
            File.Exists(Path.Combine(appRoot, "settings.json")),
            "the selected receive directory must be persisted under LocalAppData\\BOB.");

        var reloaded = new ReceiveDirectorySettings(paths, defaultReceiveDirectory);
        Assert(
            reloaded.ReceiveDirectory == Path.GetFullPath(selectedReceiveDirectory),
            "the selected receive directory did not survive a settings reload.");
        Assert(
            !Directory.EnumerateFiles(selectedReceiveDirectory, ".bob-write-test-*.tmp").Any(),
            "receive directory validation left a probe file behind.");

        var unusableDestination = Path.Combine(root, "not-a-directory");
        File.WriteAllText(unusableDestination, "file");
        var rejected = false;
        try
        {
            settings.SetReceiveDirectory(unusableDestination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            rejected = true;
        }
        Assert(rejected, "an unusable receive destination was accepted.");
        Assert(
            settings.ReceiveDirectory == Path.GetFullPath(selectedReceiveDirectory),
            "a failed receive directory change replaced the active setting.");
        var afterRejectedChange = new ReceiveDirectorySettings(paths, defaultReceiveDirectory);
        Assert(
            afterRejectedChange.ReceiveDirectory == Path.GetFullPath(selectedReceiveDirectory),
            "a failed receive directory change replaced the persisted setting.");

        File.WriteAllText(Path.Combine(appRoot, "settings.json"), "{not valid json");
        var recovered = new ReceiveDirectorySettings(paths, defaultReceiveDirectory);
        Assert(
            recovered.ReceiveDirectory == Path.GetFullPath(defaultReceiveDirectory),
            "corrupt settings must safely fall back to the default receive directory.");
    }
    finally
    {
        DeleteCheckDirectory(root);
    }
}

static void CheckFrozenReceiveDirectory()
{
    var root = CreateCheckDirectory();
    try
    {
        var firstReceiveDirectory = Path.Combine(root, "receive-a");
        var secondReceiveDirectory = Path.Combine(root, "receive-b");
        var appRoot = Path.Combine(root, "local-app-data", "BOB");
        var settings = new ReceiveDirectorySettings(
            new AppPaths(appRoot, Path.Combine(appRoot, "identity")),
            firstReceiveDirectory);
        var transfers = new TransferCoordinator(settings);
        TransferSnapshot? latest = null;
        transfers.TransferChanged += (_, snapshot) => latest = snapshot;

        var content = Encoding.UTF8.GetBytes("verified image bytes");
        var transferId = Guid.NewGuid();
        var offer = new TransferOffer(
            transferId,
            RetryOf: null,
            TransferKind.Image,
            "photo.png",
            content.LongLength,
            "image/png",
            DateTimeOffset.UtcNow);
        var accepted = transfers.RegisterIncomingOffer(offer);
        var firstPartPath = Path.Combine(
            firstReceiveDirectory,
            $".bob-{transferId:D}.part");

        Assert(
            accepted.Reply == IncomingOfferReply.Accepted,
            "the first incoming image offer was not accepted.");
        Assert(File.Exists(firstPartPath), "staging did not use the selected receive root.");
        Assert(latest?.LocalPath is null, "an unverified .part file was exposed to the timeline.");

        settings.SetReceiveDirectory(secondReceiveDirectory);
        using (var stream = transfers.BeginIncomingContent(
            transferId,
            content.LongLength).Stream)
        {
            stream.Write(content);
        }

        transfers.CompleteIncomingContent(
            transferId,
            content.LongLength,
            Base64Url(SHA256.HashData(content)));
        Assert(latest?.LocalPath is null, "verifying content was exposed before digest validation.");

        var completed = transfers.ApplyIncomingDigest(new TransferDigest(
            transferId,
            "sha-256",
            Base64Url(SHA256.HashData(content)),
            content.LongLength)).Completed
            ?? throw new InvalidOperationException("the verified incoming image was not completed.");
        var publishedPath = Path.Combine(firstReceiveDirectory, completed.StoredName);

        Assert(File.Exists(publishedPath), "the active transfer did not publish under its frozen root.");
        Assert(
            !File.Exists(Path.Combine(secondReceiveDirectory, completed.StoredName)),
            "changing settings moved the active transfer to the new receive root.");
        Assert(
            latest?.LocalPath == Path.GetFullPath(publishedPath),
            "the completed timeline path was not derived from the frozen root and storedName.");

        var nextTransferId = Guid.NewGuid();
        transfers.RegisterIncomingOffer(offer with
        {
            TransferId = nextTransferId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        Assert(
            File.Exists(Path.Combine(
                secondReceiveDirectory,
                $".bob-{nextTransferId:D}.part")),
            "the changed receive root did not apply to the next incoming offer.");
    }
    finally
    {
        DeleteCheckDirectory(root);
    }
}

static void CheckTextHistoryPersistence()
{
    var root = CreateCheckDirectory();
    try
    {
        var appRoot = Path.Combine(root, "local-app-data", "BOB");
        var paths = new AppPaths(appRoot, Path.Combine(appRoot, "identity"));
        var history = new TextHistoryStore(paths);
        var firstTimestamp = DateTimeOffset.Parse("2026-07-18T01:00:00Z");
        var ids = new List<Guid>();

        for (var index = 0; index < 25; index++)
        {
            var textId = Guid.NewGuid();
            ids.Add(textId);
            var saved = history.Save(new TextHistoryRecord(
                textId,
                $"message-{index}",
                firstTimestamp.AddMinutes(index),
                firstTimestamp.AddMinutes(index),
                Outgoing: true,
                Status: "Delivered",
                PeerName: "phone"));
            Assert(saved.IsNew, "a new text history record was treated as a duplicate.");
        }

        var reloaded = new TextHistoryStore(paths);
        Assert(reloaded.ReadAll().Count == 25, "text history did not survive a reload.");
        var recent = reloaded.ReadRecent(20);
        Assert(recent.Count == 20, "recent history did not return exactly 20 records.");
        Assert(recent[0].Text == "message-5", "recent history did not drop the five oldest records from the view.");
        Assert(recent[^1].Text == "message-24", "recent history did not keep the newest record.");

        Assert(reloaded.UpdateStatus(ids[^1], "Awaiting confirmation"), "saved history status was not updated.");
        var updated = new TextHistoryStore(paths).ReadRecent(20)[^1];
        Assert(updated.Status == "Awaiting confirmation", "updated history status did not survive a reload.");

        var duplicate = reloaded.Save(updated);
        Assert(!duplicate.IsNew, "an identical text ID was not treated as an idempotent duplicate.");
        var conflictRejected = false;
        try
        {
            reloaded.Save(updated with { Text = "changed" });
        }
        catch (TextHistoryConflictException)
        {
            conflictRejected = true;
        }
        Assert(conflictRejected, "conflicting immutable text history was accepted.");

        var incomingId = Guid.NewGuid();
        reloaded.Save(new TextHistoryRecord(
            incomingId,
            "received-once",
            firstTimestamp.AddHours(1),
            firstTimestamp.AddHours(1),
            Outgoing: false,
            Status: "Received",
            PeerName: "Pixel"));
        var coordinator = new SessionCoordinator(new TextHistoryStore(paths));
        Assert(
            coordinator.RegisterIncomingText(
                incomingId,
                "received-once",
                firstTimestamp.AddHours(1),
                "Pixel") == IncomingTextRegistration.Duplicate,
            "saved incoming history did not restore text ID deduplication.");

        File.WriteAllText(Path.Combine(appRoot, "text-messages-v1.json"), "{not valid json");
        Assert(
            new TextHistoryStore(paths).ReadAll().Count == 0,
            "corrupt text history did not recover to an empty timeline.");
    }
    finally
    {
        DeleteCheckDirectory(root);
    }
}

static void CheckImageTimelinePreview()
{
    var root = CreateCheckDirectory();
    try
    {
        var imagePath = Path.Combine(root, "pixel.png");
        File.WriteAllBytes(
            imagePath,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

        string? openedPath = null;
        string? copiedPath = null;
        var item = new TimelineItemViewModel(
            Guid.NewGuid(),
            "pixel.png",
            DateTimeOffset.UtcNow,
            outgoing: true,
            status: "Queued",
            peerName: "phone",
            kind: TransferKind.Image,
            localPath: imagePath,
            openImage: path => openedPath = path,
            copyImage: path => copiedPath = path);

        item.ThumbnailLoadTask.GetAwaiter().GetResult();
        Assert(item.Thumbnail is not null, "a decodable local image did not produce a thumbnail.");
        Assert(item.ViewImageCommand.CanExecute(null), "View must be enabled for an existing local image.");
        item.ViewImageCommand.Execute(null);
        Assert(openedPath == imagePath, "View did not request the decoded local image preview.");
        Assert(item.CopyImageCommand.CanExecute(null), "Copy must be enabled for an existing local image.");
        item.CopyImageCommand.Execute(null);
        Assert(copiedPath == imagePath, "Copy did not request the original local image.");
        Assert(
            TimelineItemViewModel.LoadClipboardImage(imagePath) is not null,
            "a decodable image could not be loaded for clipboard copying.");

        string? copiedText = null;
        var textItem = new TimelineItemViewModel(
            Guid.NewGuid(),
            "copy all of this",
            DateTimeOffset.UtcNow,
            outgoing: false,
            status: "Received",
            peerName: "phone",
            copyText: value => copiedText = value);
        Assert(textItem.CopyTextCommand.CanExecute(null), "Copy must be enabled for a text message.");
        textItem.CopyTextCommand.Execute(null);
        Assert(copiedText == "copy all of this", "Copy did not expose the full text body.");
        using (new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        var tallImagePath = Path.Combine(root, "tall.png");
        var tallBitmap = new System.Windows.Media.Imaging.WriteableBitmap(
            1,
            4_096,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            palette: null);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(tallBitmap));
        using (var stream = new FileStream(
            tallImagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            encoder.Save(stream);
        }

        var tallPreview = TimelineItemViewModel.LoadImagePreview(
            tallImagePath,
            maximumDimension: 360) as System.Windows.Media.Imaging.BitmapSource;
        Assert(
            tallPreview is not null
                && tallPreview.PixelWidth <= 360
                && tallPreview.PixelHeight <= 360,
            "thumbnail decoding did not bound both dimensions.");

        var disguisedExecutablePath = Path.Combine(root, "not-an-image.cmd");
        File.WriteAllText(disguisedExecutablePath, "@echo this must never run");
        var disguisedInvoked = false;
        var disguised = new TimelineItemViewModel(
            Guid.NewGuid(),
            "not-an-image.cmd",
            DateTimeOffset.UtcNow,
            outgoing: false,
            status: "Completed",
            peerName: "phone",
            kind: TransferKind.Image,
            localPath: disguisedExecutablePath,
            openImage: _ => disguisedInvoked = true);
        disguised.ThumbnailLoadTask.GetAwaiter().GetResult();
        Assert(disguised.Thumbnail is null, "non-image content produced an image thumbnail.");
        Assert(
            !disguised.ViewImageCommand.CanExecute(null),
            "View must stay disabled when image decoding fails.");
        disguised.ViewImageCommand.Execute(null);
        Assert(!disguisedInvoked, "non-image content was passed to the preview action.");

        var transfers = new TransferCoordinator(Path.Combine(root, "receive"));
        TransferSnapshot? queued = null;
        transfers.TransferChanged += (_, snapshot) => queued = snapshot;
        transfers.EnqueueOutgoing(imagePath, TransferKind.Image);
        Assert(
            queued?.LocalPath == Path.GetFullPath(imagePath),
            "an outgoing image snapshot did not expose its source path for preview.");
    }
    finally
    {
        DeleteCheckDirectory(root);
    }
}

static void CheckTransferSnapshotRevisionRace()
{
    var root = CreateCheckDirectory();
    var releaseVerifying = new ManualResetEventSlim(false);
    try
    {
        var transfers = new TransferCoordinator(Path.Combine(root, "receive"));
        var content = Encoding.UTF8.GetBytes("cross-channel revision order");
        var transferId = Guid.NewGuid();
        var offer = new TransferOffer(
            transferId,
            RetryOf: null,
            TransferKind.Image,
            "race.png",
            content.LongLength,
            "image/png",
            DateTimeOffset.UtcNow);
        transfers.RegisterIncomingOffer(offer);
        using (var stream = transfers.BeginIncomingContent(
            transferId,
            content.LongLength).Stream)
        {
            stream.Write(content);
        }

        using var verifyingPublished = new ManualResetEventSlim(false);
        var published = new List<TransferSnapshot>();
        transfers.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.State == TransferState.Verifying)
            {
                verifyingPublished.Set();
                releaseVerifying.Wait(TimeSpan.FromSeconds(5));
            }

            lock (published)
            {
                published.Add(snapshot);
            }
        };

        var sha256 = Base64Url(SHA256.HashData(content));
        var contentCompletion = Task.Run(() => transfers.CompleteIncomingContent(
            transferId,
            content.LongLength,
            sha256));
        Assert(
            verifyingPublished.Wait(TimeSpan.FromSeconds(5)),
            "the controlled verifying snapshot was not reached.");

        var completion = transfers.ApplyIncomingDigest(new TransferDigest(
            transferId,
            "sha-256",
            sha256,
            content.LongLength));
        Assert(completion.Completed is not null, "the matching digest did not complete the transfer.");
        releaseVerifying.Set();
        contentCompletion.GetAwaiter().GetResult();

        TransferSnapshot[] observed;
        lock (published)
        {
            observed = published.ToArray();
        }

        var completed = observed.Single(snapshot => snapshot.State == TransferState.Completed);
        var verifying = observed.Single(snapshot => snapshot.State == TransferState.Verifying);
        Assert(
            Array.IndexOf(observed, completed) < Array.IndexOf(observed, verifying),
            "the test did not reproduce the completed-before-verifying event order.");
        Assert(
            completed.Revision > verifying.Revision,
            "completed state must carry a newer per-transfer revision than verifying state.");

        long latestRevision = 0;
        TransferSnapshot? displayed = null;
        foreach (var snapshot in observed)
        {
            if (snapshot.Revision <= latestRevision)
            {
                continue;
            }

            latestRevision = snapshot.Revision;
            displayed = snapshot;
        }

        Assert(
            displayed?.State == TransferState.Completed && displayed.LocalPath is not null,
            "revision gating did not preserve the completed preview state.");
    }
    finally
    {
        releaseVerifying.Set();
        releaseVerifying.Dispose();
        DeleteCheckDirectory(root);
    }
}

static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

static string CreateCheckDirectory()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        "bob-windows-checks",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return Path.GetFullPath(root);
}

static void DeleteCheckDirectory(string root)
{
    var fullRoot = Path.GetFullPath(root);
    var expectedParent = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "bob-windows-checks"));
    var expectedPrefix = expectedParent.TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    if (!fullRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("refusing to delete a check directory outside the test root.");
    }

    if (Directory.Exists(fullRoot))
    {
        Directory.Delete(fullRoot, recursive: true);
    }
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
