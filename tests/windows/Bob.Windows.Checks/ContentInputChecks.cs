using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bob.Windows;
using Bob.Windows.Domain;
using Bob.Windows.Persistence;
using Bob.Windows.Security;
using Bob.Windows.Storage;
using Bob.Windows.Transport;
using Bob.Windows.UI;

internal static partial class RealTransportChecks
{
    private static readonly Lazy<Task<Dispatcher>> InputDispatcher = new(StartInputDispatcher);
    private static readonly byte[] ClipboardPixels =
    [
        0, 0, 255, 255, 0, 255, 0, 255,
        255, 0, 0, 255, 23, 45, 67, 128,
        100, 110, 120, 255, 11, 22, 33, 0
    ];

    public static async Task CheckClipboardImageCacheAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        var paths = InputAppPaths(host);
        var store = new ClipboardImageStore(paths);
        try
        {
            var image = CreateClipboardBitmap();
            var firstPath = await store.SaveAsync(image);
            var secondPath = await store.SaveAsync(image);
            Ensure(firstPath != secondPath, "Separate pasted images must have distinct cache filenames.");
            Ensure(
                Path.GetFullPath(firstPath).StartsWith(
                    paths.RootDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "Clipboard image content must be stored under the test's private app directory.");
            Ensure(Path.GetExtension(firstPath) == ".png", "Clipboard images must be saved as PNG.");
            CheckClipboardPng(await File.ReadAllBytesAsync(firstPath));
            using (new FileStream(firstPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            var unrelatedPath = Path.Combine(host.TemporaryRoot, "user-file.txt");
            await File.WriteAllTextAsync(unrelatedPath, "keep this file");
            store.Delete(firstPath);
            Ensure(!File.Exists(firstPath), "Explicit cache deletion did not remove the selected image.");
            Ensure(File.Exists(secondPath), "Deleting one image removed another queued image.");
            store.Dispose();
            Ensure(!File.Exists(secondPath), "Closing the cache store did not remove its retained image.");
            Ensure(File.Exists(unrelatedPath), "Cache cleanup removed an unrelated source file.");
        }
        finally
        {
            store.Dispose();
        }
    }

    public static Task CheckContentInputOfflineAsync() =>
        RunOnInputDispatcherAsync(CheckContentInputOfflineOnDispatcherAsync);

    private static async Task CheckContentInputOfflineOnDispatcherAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var store = new ClipboardImageStore(InputAppPaths(host));
        using var identity = CreateInputIdentity(host);
        var viewModel = CreateInputViewModel(host, store, identity);
        var changed = 0;
        host.Transfers.TransferChanged += (_, _) => changed++;
        var sourcePath = Path.Combine(host.TemporaryRoot, "offline.txt");
        await File.WriteAllTextAsync(sourcePath, "must not be queued");

        Ensure(!viewModel.CanSendContent, "Direct content input must be disabled while offline.");
        await viewModel.SendDroppedFilesAsync([sourcePath]);
        Ensure(changed == 0 && viewModel.Timeline.Count == 0, "Dropping a file offline created a transfer.");
        EnsureConnectionHint(viewModel);
        await viewModel.SendClipboardImageAsync(CreateClipboardBitmap());
        Ensure(changed == 0 && viewModel.Timeline.Count == 0, "Pasting an image offline created a transfer.");
        EnsureConnectionHint(viewModel);
        Ensure(
            !Directory.Exists(InputAppPaths(host).RootDirectory)
                || !Directory.EnumerateFiles(
                    InputAppPaths(host).RootDirectory,
                    "*.png",
                    SearchOption.AllDirectories).Any(),
            "Pasting an image offline wrote an unused image cache file.");
        Ensure(
            host.Transfers.TryOfferNextOutgoing() is null,
            "Offline direct content input left an item in the outgoing queue.");
    }

    public static Task CheckDroppedContentFifoAsync() =>
        RunOnInputDispatcherAsync(CheckDroppedContentFifoOnDispatcherAsync);

    private static async Task CheckDroppedContentFifoOnDispatcherAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var store = new ClipboardImageStore(InputAppPaths(host));
        using var identity = CreateInputIdentity(host);
        var viewModel = CreateInputViewModel(host, store, identity);
        var contentAvailabilityChanges = 0;
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MainWindowViewModel.CanSendContent))
            {
                contentAvailabilityChanges++;
            }
        };
        await OpenSessionAsync(host, socket, timeout.Token);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Ensure(viewModel.CanSendContent, "A connected phone did not enable direct content input.");
        Ensure(contentAvailabilityChanges > 0, "Phone connection did not notify the content input binding.");

        var firstPath = Path.Combine(host.TemporaryRoot, "first.txt");
        var imagePath = Path.Combine(host.TemporaryRoot, "middle.PNG");
        var lastPath = Path.Combine(host.TemporaryRoot, "last.bin");
        var folderPath = Path.Combine(host.TemporaryRoot, "folder");
        var missingPath = Path.Combine(host.TemporaryRoot, "missing.txt");
        await File.WriteAllTextAsync(firstPath, "first", timeout.Token);
        await File.WriteAllBytesAsync(imagePath, CreateClipboardPng(), timeout.Token);
        await File.WriteAllBytesAsync(lastPath, [1, 2, 3], timeout.Token);
        Directory.CreateDirectory(folderPath);
        var queued = new List<TransferSnapshot>();
        host.Transfers.TransferChanged += (_, snapshot) =>
        {
            if (snapshot.State == TransferState.Queued)
            {
                queued.Add(snapshot);
            }
        };

        await viewModel.SendDroppedFilesAsync([folderPath]);
        Ensure(queued.Count == 0, "A dropped directory entered the file queue.");
        Ensure(
            viewModel.ComposerHint.Contains("ZIP", StringComparison.OrdinalIgnoreCase),
            "A dropped directory must explain that it needs to be compressed as ZIP.");
        await viewModel.SendDroppedFilesAsync([firstPath, folderPath, missingPath, imagePath, lastPath]);
        Ensure(
            queued.Select(item => item.Name).SequenceEqual(["first.txt", "middle.PNG", "last.bin"]),
            "Invalid dropped items blocked valid files or changed their queue order.");
        Ensure(
            queued.Select(item => item.Kind).SequenceEqual(
                [TransferKind.File, TransferKind.Image, TransferKind.File]),
            "Dropped image extensions must be classified case-insensitively and other files remain files.");
        Ensure(
            viewModel.ComposerHint.Contains("ZIP", StringComparison.OrdinalIgnoreCase),
            "A mixed drop must retain the folder compression hint.");

        var first = await ReceiveAsync(socket, timeout.Token);
        EnsureOfferedItem(first, queued[0]);
        await CheckNoPrematureOfferAsync(socket, timeout.Token);
        for (var index = 0; index < queued.Count; index++)
        {
            await SendAsync(
                socket,
                ProtocolJson.Create("transfer.failed", new
                {
                    transferId = queued[index].TransferId,
                    code = "io_error",
                    message = "The test receiver rejected this item.",
                    retryable = true
                }),
                timeout.Token);
            var acknowledgment = await ReceiveAsync(socket, timeout.Token);
            Ensure(
                acknowledgment.Type == "transfer.terminalAck"
                    && acknowledgment.Payload.GetProperty("transferId").GetGuid() == queued[index].TransferId,
                "A remote failure must be acknowledged before advancing the dropped-content queue.");
            if (index + 1 < queued.Count)
            {
                EnsureOfferedItem(await ReceiveAsync(socket, timeout.Token), queued[index + 1]);
                await CheckNoPrematureOfferAsync(socket, timeout.Token);
            }
        }
        await CheckNoPrematureOfferAsync(socket, timeout.Token);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "check_complete", timeout.Token);
    }

    public static Task CheckClipboardImageDownloadAsync() =>
        RunOnInputDispatcherAsync(CheckClipboardImageDownloadOnDispatcherAsync);

    public static Task CheckWindowFileDropAsync() =>
        RunOnInputDispatcherAsync(CheckWindowFileDropOnDispatcherAsync);

    private static async Task CheckWindowFileDropOnDispatcherAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var store = new ClipboardImageStore(InputAppPaths(host));
        using var identity = CreateInputIdentity(host);
        var viewModel = CreateInputViewModel(host, store, identity);
        viewModel.DraftText = "keep this draft";
        var window = new MainWindow(viewModel);
        try
        {
            var composer = window.FindName("ComposerTextBox") as TextBox
                ?? throw new InvalidOperationException("The window composer was not found.");
            var sourcePath = Path.Combine(host.TemporaryRoot, "routed-drop.txt");
            await File.WriteAllTextAsync(sourcePath, "routed file content", timeout.Token);
            // A hidden window can still have its inherited DataContext waiting in the binding queue.
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            var draftBinding = composer.GetBindingExpression(TextBox.TextProperty)
                ?? throw new InvalidOperationException("The window composer has no draft text binding.");
            draftBinding.UpdateTarget();
            Ensure(composer.Text == "keep this draft", "The routed-drop check draft did not bind to the composer.");
            var data = new DataObject(DataFormats.FileDrop, new[] { sourcePath });
            var offlineDrag = CreateFileDragEvent(data, composer, DragDrop.PreviewDragOverEvent);
            composer.RaiseEvent(offlineDrag);
            Ensure(
                offlineDrag.Handled && offlineDrag.Effects == DragDropEffects.None,
                "An offline file drag must be intercepted without accepting a drop effect.");
            var offlineDrop = CreateFileDragEvent(data, composer, DragDrop.PreviewDropEvent);
            composer.RaiseEvent(offlineDrop);
            Ensure(
                offlineDrop.Handled && viewModel.Timeline.Count == 0,
                "Dropping on the composer while offline created a transfer or escaped the window handler.");
            EnsureConnectionHint(viewModel);
            Ensure(composer.Text == "keep this draft", "An offline file drop inserted its path into the draft.");

            await OpenSessionAsync(host, socket, timeout.Token);
            var onlineDrag = CreateFileDragEvent(data, composer, DragDrop.PreviewDragOverEvent);
            composer.RaiseEvent(onlineDrag);
            Ensure(
                onlineDrag.Handled && onlineDrag.Effects == DragDropEffects.Copy,
                "A connected file drag did not advertise the Copy drop effect.");
            var onlineDrop = CreateFileDragEvent(data, composer, DragDrop.PreviewDropEvent);
            composer.RaiseEvent(onlineDrop);
            var offered = await ReceiveAsync(socket, timeout.Token);
            Ensure(
                onlineDrop.Handled && onlineDrop.Effects == DragDropEffects.Copy
                    && offered.Type == "transfer.offer"
                    && offered.Payload.GetProperty("name").GetString() == "routed-drop.txt",
                "A file dropped on the composer did not reach the connected transfer route.");
            Ensure(
                composer.Text == "keep this draft" && viewModel.DraftText == "keep this draft",
                "A routed file drop replaced the draft or inserted a file path.");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "check_complete", timeout.Token);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task CheckClipboardImageDownloadOnDispatcherAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var store = new ClipboardImageStore(InputAppPaths(host));
        using var identity = CreateInputIdentity(host);
        var viewModel = CreateInputViewModel(host, store, identity);
        await OpenSessionAsync(host, socket, timeout.Token);

        await viewModel.SendClipboardImageAsync(CreateClipboardBitmap());
        var offered = await ReceiveAsync(socket, timeout.Token);
        Ensure(offered.Type == "transfer.offer", "Pasting an image did not immediately send a transfer.offer.");
        var payload = offered.Payload;
        var transferId = payload.GetProperty("transferId").GetGuid();
        var name = payload.GetProperty("name").GetString();
        var size = payload.GetProperty("size").GetInt64();
        Ensure(
            payload.GetProperty("kind").GetString() == "image"
                && payload.GetProperty("mediaType").GetString() == "image/png"
                && name?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true
                && size > 0,
            "A pasted image offer must describe nonempty PNG image content.");
        await SendAsync(
            socket,
            ProtocolJson.Create("transfer.accepted", new { transferId, plannedName = name }),
            timeout.Token);
        using var response = await client.GetAsync($"/bob/v1/transfers/{transferId:D}/content", timeout.Token);
        Ensure(response.StatusCode == HttpStatusCode.OK, "The pasted-image offer could not be downloaded.");
        Ensure(
            response.Content.Headers.ContentType?.MediaType == "image/png"
                && response.Content.Headers.ContentLength == size,
            "Pasted PNG response headers did not match the offer.");
        var content = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        Ensure(content.LongLength == size, "Pasted PNG offer size did not match the streamed bytes.");
        CheckClipboardPng(content);
        var digest = await ReceiveAsync(socket, timeout.Token);
        var sha256 = Base64Url(SHA256.HashData(content));
        Ensure(
            digest.Type == "transfer.digest"
                && digest.Payload.GetProperty("transferId").GetGuid() == transferId
                && digest.Payload.GetProperty("value").GetString() == sha256
                && digest.Payload.GetProperty("bytes").GetInt64() == size,
            "Pasted PNG download did not emit the matching byte count and SHA-256 digest.");
        await SendAsync(
            socket,
            ProtocolJson.Create("transfer.completed", new
            {
                transferId,
                bytes = size,
                sha256,
                storedName = name,
                completedAt = DateTimeOffset.UtcNow
            }),
            timeout.Token);
        var terminalAck = await ReceiveAsync(socket, timeout.Token);
        Ensure(
            terminalAck.Type == "transfer.terminalAck"
                && terminalAck.Payload.GetProperty("transferId").GetGuid() == transferId
                && terminalAck.Payload.GetProperty("state").GetString() == "completed",
            "Pasted PNG completion did not close the transfer protocol with a matching terminal acknowledgment.");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "check_complete", timeout.Token);
    }

    private static AppPaths InputAppPaths(TestBobHost host)
    {
        var appRoot = Path.Combine(host.TemporaryRoot, "app-data");
        return new AppPaths(appRoot, Path.Combine(appRoot, "identity"));
    }

    private static ServerIdentity CreateInputIdentity(TestBobHost host) =>
        new(host.ServerId, ServerCertificateFactory.Create(host.ServerId, "localhost", DateTimeOffset.UtcNow));

    private static MainWindowViewModel CreateInputViewModel(
        TestBobHost host,
        ClipboardImageStore store,
        ServerIdentity identity) =>
        new(
            host.Sessions,
            host.Transfers,
            new ReceiveDirectorySettings(InputAppPaths(host), host.ReceiveDirectory),
            new TextHistoryStore(InputAppPaths(host)),
            identity,
            store);

    private static BitmapSource CreateClipboardBitmap()
    {
        var image = BitmapSource.Create(2, 3, 96, 96, PixelFormats.Bgra32, null, ClipboardPixels, stride: 8);
        image.Freeze();
        return image;
    }

    private static byte[] CreateClipboardPng()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateClipboardBitmap()));
        using var content = new MemoryStream();
        encoder.Save(content);
        return content.ToArray();
    }

    private static void CheckClipboardPng(byte[] content)
    {
        Ensure(
            content.AsSpan(0, Math.Min(content.Length, 8)).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "Clipboard cache content does not carry a valid PNG signature.");
        using var stream = new MemoryStream(content);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Ensure(decoder.Frames.Count == 1, "Pasted bitmap must encode one PNG frame.");
        var frame = decoder.Frames[0];
        Ensure(frame.PixelWidth == 2 && frame.PixelHeight == 3, "Pasted image was resized before sending.");
        var image = frame.Format == PixelFormats.Bgra32
            ? (BitmapSource)frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[ClipboardPixels.Length];
        image.CopyPixels(pixels, stride: 8, offset: 0);
        Ensure(pixels.SequenceEqual(ClipboardPixels), "Pasted PNG did not preserve the original bitmap pixels.");
    }

    private static void EnsureConnectionHint(MainWindowViewModel viewModel) =>
        Ensure(
            viewModel.ComposerHint.Contains("connect", StringComparison.OrdinalIgnoreCase)
                && viewModel.ComposerHint.Contains("phone", StringComparison.OrdinalIgnoreCase),
            "Offline direct content input must tell the user to connect a phone first.");

    private static void EnsureOfferedItem(ProtocolEnvelope offered, TransferSnapshot expected) =>
        Ensure(
            offered.Type == "transfer.offer"
                && offered.Payload.GetProperty("transferId").GetGuid() == expected.TransferId
                && offered.Payload.GetProperty("name").GetString() == expected.Name
                && offered.Payload.GetProperty("size").GetInt64() == expected.Size
                && offered.Payload.GetProperty("kind").GetString()
                    == (expected.Kind == TransferKind.Image ? "image" : "file"),
            "The actual outgoing offer did not match the expected FIFO item and image/file classification.");

    private static async Task CheckNoPrematureOfferAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var textId = Guid.NewGuid();
        await SendAsync(
            socket,
            ProtocolJson.Create("text.send", new
            {
                textId,
                text = "queue ordering sentinel",
                createdAt = DateTimeOffset.UtcNow
            }),
            cancellationToken);
        var response = await ReceiveAsync(socket, cancellationToken);
        Ensure(
            response.Type == "text.ack" && response.Payload.GetProperty("textId").GetGuid() == textId,
            "Another dropped item was offered before the active item reached a terminal state.");
    }

    private static async Task RunOnInputDispatcherAsync(Func<Task> check)
    {
        var dispatcher = await InputDispatcher.Value;
        await dispatcher.InvokeAsync(check).Task.Unwrap();
    }

    private static DragEventArgs CreateFileDragEvent(IDataObject data, DependencyObject target, RoutedEvent routedEvent)
    {
        // WPF creates DragEventArgs internally, so construct only its event data through reflection.
        var constructor = typeof(DragEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)],
            modifiers: null)
            ?? throw new InvalidOperationException("The WPF drag event constructor was not found.");
        var args = (DragEventArgs)constructor.Invoke(
            [data, DragDropKeyStates.None, DragDropEffects.Copy, target, new Point(1, 1)]);
        args.RoutedEvent = routedEvent;
        return args;
    }

    private static Task<Dispatcher> StartInputDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // Window routing needs these resource keys; the check does not render or show the window.
                application.Resources["MutedBrush"] = Brushes.Gray;
                application.Resources["StrokeBrush"] = Brushes.Black;
                application.Resources["SurfaceBrush"] = Brushes.White;
                application.Resources["ButtonBrush"] = Brushes.White;
                ready.SetResult(application.Dispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "BOB content input checks"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
}
