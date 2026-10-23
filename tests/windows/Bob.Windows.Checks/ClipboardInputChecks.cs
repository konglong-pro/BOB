using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bob.Windows;
using Bob.Windows.Storage;
using Bob.Windows.Transport;
using Bob.Windows.UI;

internal static partial class RealTransportChecks
{
    public static Task CheckClipboardImageFormatsAsync() =>
        RunOnInputDispatcherAsync(CheckClipboardImageFormatsOnDispatcherAsync);

    public static Task CheckCopiedImageFilesPasteAsync() =>
        RunOnInputDispatcherAsync(CheckCopiedImageFilesPasteOnDispatcherAsync);

    public static Task CheckPngClipboardPasteAsync() =>
        RunOnInputDispatcherAsync(CheckPngClipboardPasteOnDispatcherAsync);

    public static Task CheckTextClipboardRoutingAsync() =>
        RunOnInputDispatcherAsync(CheckTextClipboardRoutingOnDispatcherAsync);

    private static async Task CheckClipboardImageFormatsOnDispatcherAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var store = new ClipboardImageStore(InputAppPaths(host));
        var bytes = CreateClipboardPng();
        using var pngStream = new MemoryStream(bytes);
        pngStream.Position = pngStream.Length;
        var png = new DataObject();
        png.SetData("PNG", pngStream, autoConvert: false);
        Ensure(ClipboardImageReader.CanRead(png), "A PNG-only clipboard representation was not recognized.");
        Ensure(pngStream.Position == pngStream.Length, "Clipboard probing consumed the PNG stream.");
        png.SetData(DataFormats.Bitmap, CreateOtherClipboardBitmap(), autoConvert: false);
        png.SetText("Image caption", TextDataFormat.UnicodeText);
        var image = ClipboardImageReader.Read(png);
        Ensure(
            image is { Files.Count: 0, Bitmap.IsFrozen: true },
            "PNG input did not produce a frozen bitmap without file paths.");
        Ensure(pngStream.CanRead && pngStream.Position == pngStream.Length, "PNG parsing closed or repositioned the source stream.");
        try
        {
            var openSourceCache = await store.SaveAsync(image!.Bitmap!);
            CheckClipboardPng(await File.ReadAllBytesAsync(openSourceCache));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Saving a decoded PNG with an open source stream failed: {exception}", exception);
        }
        pngStream.Dispose();
        CheckClipboardPng(EncodeClipboardBitmap(image!.Bitmap!));
        var cachePath = await store.SaveAsync(image.Bitmap!);
        CheckClipboardPng(await File.ReadAllBytesAsync(cachePath));

        var mimePng = new DataObject();
        mimePng.SetData("image/png", bytes, autoConvert: false);
        Ensure(ClipboardImageReader.CanRead(mimePng), "The image/png MIME clipboard format was not recognized.");
        var mimeImage = ClipboardImageReader.Read(mimePng);
        Ensure(mimeImage?.Bitmap is not null, "PNG byte-array input was not decoded.");
        CheckClipboardPng(EncodeClipboardBitmap(mimeImage!.Bitmap!));

        var bitmap = new DataObject();
        bitmap.SetData(DataFormats.Bitmap, CreateClipboardBitmap(), autoConvert: false);
        Ensure(ClipboardImageReader.CanRead(bitmap), "A native bitmap was not recognized.");
        var bitmapImage = ClipboardImageReader.Read(bitmap);
        Ensure(bitmapImage?.Bitmap?.IsFrozen == true, "Native bitmap input did not produce a frozen image.");
        CheckClipboardPng(EncodeClipboardBitmap(bitmapImage!.Bitmap!));

        using var invalidPngStream = new MemoryStream([1, 2, 3, 4]);
        invalidPngStream.Position = 2;
        var invalidPng = new DataObject();
        invalidPng.SetData("PNG", invalidPngStream, autoConvert: false);
        invalidPng.SetData(DataFormats.Bitmap, CreateClipboardBitmap(), autoConvert: false);
        var recovered = ClipboardImageReader.Read(invalidPng);
        Ensure(recovered?.Bitmap is not null, "Malformed PNG prevented fallback to a valid bitmap.");
        Ensure(
            invalidPngStream.CanRead && invalidPngStream.Position == 2,
            "Malformed PNG fallback closed or repositioned the source stream.");
        CheckClipboardPng(EncodeClipboardBitmap(recovered!.Bitmap!));

        var originalPaths = new[] { @"C:\clipboard-checks\first.GIF", @"C:\clipboard-checks\notes.txt", @"C:\clipboard-checks\second.JpEg" };
        var copiedFiles = new DataObject();
        copiedFiles.SetData(DataFormats.FileDrop, originalPaths, autoConvert: false);
        copiedFiles.SetData("PNG", bytes, autoConvert: false);
        copiedFiles.SetData(DataFormats.Bitmap, CreateOtherClipboardBitmap(), autoConvert: false);
        var files = ClipboardImageReader.Read(copiedFiles);
        Ensure(
            ClipboardImageReader.CanRead(copiedFiles)
                && files is { Bitmap: null }
                && files.Files.SequenceEqual([originalPaths[0], originalPaths[2]]),
            "Copied image files must take precedence over rendered image formats and retain their original order and names.");

        var unsupported = new[]
        {
            new DataObject(DataFormats.UnicodeText, "ordinary text"),
            new DataObject(DataFormats.Html, "<p>ordinary HTML</p>"),
            new DataObject(DataFormats.FileDrop, new[] { @"C:\clipboard-checks\ordinary.txt" })
        };
        foreach (var data in unsupported)
        {
            Ensure(
                !ClipboardImageReader.CanRead(data) && ClipboardImageReader.Read(data) is null,
                "Text, HTML and non-image copied files must not be interpreted as images.");
        }
    }

    private static async Task CheckCopiedImageFilesPasteOnDispatcherAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var store = new ClipboardImageStore(InputAppPaths(host));
        using var identity = CreateInputIdentity(host);
        var viewModel = CreateInputViewModel(host, store, identity);
        viewModel.DraftText = "preserve this draft";
        var firstBytes = CreateClipboardPng();
        var bmpEncoder = new BmpBitmapEncoder();
        bmpEncoder.Frames.Add(BitmapFrame.Create(CreateClipboardBitmap()));
        using var bmpStream = new MemoryStream();
        bmpEncoder.Save(bmpStream);
        var secondBytes = bmpStream.ToArray();
        var firstPath = Path.Combine(host.TemporaryRoot, "copied-first.PNG");
        var secondPath = Path.Combine(host.TemporaryRoot, "copied-second.bmp");
        await File.WriteAllBytesAsync(firstPath, firstBytes, timeout.Token);
        await File.WriteAllBytesAsync(secondPath, secondBytes, timeout.Token);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { firstPath, secondPath }, autoConvert: false);
        data.SetData("PNG", CreateClipboardPng(), autoConvert: false);
        data.SetData(DataFormats.Bitmap, CreateOtherClipboardBitmap(), autoConvert: false);
        var window = new MainWindow(viewModel, () => data);
        try
        {
            var composer = await PrepareClipboardComposerAsync(window);
            await OpenSessionAsync(host, socket, timeout.Token);
            RaiseImagePaste(composer);
            var firstOffer = await ReceiveAsync(socket, timeout.Token);
            Ensure(
                firstOffer.Type == "transfer.offer"
                    && firstOffer.Payload.GetProperty("name").GetString() == "copied-first.PNG",
                "Explorer-style copied images did not preserve the first source filename.");
            await CheckNoPrematureOfferAsync(socket, timeout.Token);
            await CompleteClipboardDownloadAsync(
                socket, client, firstOffer,
                content => Ensure(content.SequenceEqual(firstBytes), "Pasting a copied image changed the original source bytes."),
                timeout.Token);
            var secondOffer = await ReceiveAsync(socket, timeout.Token);
            Ensure(
                secondOffer.Type == "transfer.offer"
                    && secondOffer.Payload.GetProperty("name").GetString() == "copied-second.bmp",
                "Copied images did not advance in their original selection order.");
            await CompleteClipboardDownloadAsync(
                socket, client, secondOffer,
                content => Ensure(content.SequenceEqual(secondBytes), "Pasting the second copied image changed its original bytes."),
                timeout.Token);
            await CheckNoPrematureOfferAsync(socket, timeout.Token);
            Ensure(
                composer.Text == "preserve this draft" && viewModel.DraftText == "preserve this draft",
                "Pasting copied image files changed the text draft.");
            Ensure(
                !Directory.Exists(InputAppPaths(host).RootDirectory)
                    || !Directory.EnumerateFiles(InputAppPaths(host).RootDirectory, "*.png", SearchOption.AllDirectories).Any(),
                "Copied image files were unnecessarily converted into clipboard PNG cache files.");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "check_complete", timeout.Token);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task CheckPngClipboardPasteOnDispatcherAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var client = new HttpClient { BaseAddress = host.BaseUri };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var store = new ClipboardImageStore(InputAppPaths(host));
        using var identity = CreateInputIdentity(host);
        var viewModel = CreateInputViewModel(host, store, identity);
        viewModel.DraftText = "preserve this draft";
        using var pngStream = new MemoryStream(CreateClipboardPng());
        pngStream.Position = pngStream.Length;
        var data = new DataObject();
        data.SetData("PNG", pngStream, autoConvert: false);
        data.SetData(DataFormats.Bitmap, CreateOtherClipboardBitmap(), autoConvert: false);
        data.SetText("must not replace the draft", TextDataFormat.UnicodeText);
        var window = new MainWindow(viewModel, () => data);
        var stage = "paste command offer";
        try
        {
            var composer = await PrepareClipboardComposerAsync(window);
            await OpenSessionAsync(host, socket, timeout.Token);
            RaiseImagePaste(composer);
            var offered = await ReceiveAsync(socket, timeout.Token);
            Ensure(
                offered.Type == "transfer.offer"
                    && offered.Payload.GetProperty("mediaType").GetString() == "image/png",
                "PNG clipboard input did not produce an immediate PNG image offer.");
            stage = "PNG body and completion";
            await CompleteClipboardDownloadAsync(socket, client, offered, CheckClipboardPng, timeout.Token);
            await CheckNoPrematureOfferAsync(socket, timeout.Token);
            Ensure(
                composer.Text == "preserve this draft" && viewModel.DraftText == "preserve this draft",
                "The text representation of a pasted PNG replaced the draft.");
            Ensure(
                pngStream.CanRead && pngStream.Position == pngStream.Length,
                "Paste command handling closed or repositioned the clipboard source stream.");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "check_complete", timeout.Token);
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException(
                $"PNG clipboard paste timed out during {stage}. Composer hint: {viewModel.ComposerHint}",
                exception);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task CheckTextClipboardRoutingOnDispatcherAsync()
    {
        await using var host = await TestBobHost.StartAsync();
        using var socket = host.CreateWebSocketClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var store = new ClipboardImageStore(InputAppPaths(host));
        using var identity = CreateInputIdentity(host);
        var viewModel = CreateInputViewModel(host, store, identity);
        viewModel.DraftText = "preserve this draft";
        IDataObject currentData = new DataObject(DataFormats.UnicodeText, "ordinary text");
        var window = new MainWindow(viewModel, () => currentData);
        try
        {
            var composer = await PrepareClipboardComposerAsync(window);
            await OpenSessionAsync(host, socket, timeout.Token);
            var hintBeforePaste = viewModel.ComposerHint;
            var plainSources = new[]
            {
                currentData,
                new DataObject(DataFormats.Html, "<p>ordinary HTML</p>"),
                new DataObject(DataFormats.FileDrop, new[] { Path.Combine(host.TemporaryRoot, "ordinary.txt") })
            };
            foreach (var data in plainSources)
            {
                currentData = data;
                var canExecute = CreatePasteCommandEvent<CanExecuteRoutedEventArgs>(CommandManager.PreviewCanExecuteEvent);
                composer.RaiseEvent(canExecute);
                var executed = CreatePasteCommandEvent<ExecutedRoutedEventArgs>(CommandManager.PreviewExecutedEvent);
                composer.RaiseEvent(executed);
                Ensure(
                    !canExecute.Handled && !executed.Handled,
                    "The image paste handlers intercepted text, HTML or a non-image file.");
            }
            Ensure(
                viewModel.Timeline.Count == 0
                    && composer.Text == "preserve this draft"
                    && viewModel.DraftText == "preserve this draft"
                    && viewModel.ComposerHint == hintBeforePaste,
                "Non-image preview commands triggered image sending or changed the draft/hint.");
            await CheckNoPrematureOfferAsync(socket, timeout.Token);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "check_complete", timeout.Token);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<TextBox> PrepareClipboardComposerAsync(MainWindow window)
    {
        var composer = window.FindName("ComposerTextBox") as TextBox
            ?? throw new InvalidOperationException("The clipboard check composer was not found.");
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        var binding = composer.GetBindingExpression(TextBox.TextProperty)
            ?? throw new InvalidOperationException("The clipboard check composer has no draft binding.");
        binding.UpdateTarget();
        Ensure(composer.Text == "preserve this draft", "The clipboard check draft did not bind.");
        return composer;
    }

    private static void RaiseImagePaste(TextBox composer)
    {
        var canExecute = CreatePasteCommandEvent<CanExecuteRoutedEventArgs>(CommandManager.PreviewCanExecuteEvent);
        composer.RaiseEvent(canExecute);
        Ensure(canExecute.Handled && canExecute.CanExecute, "Image clipboard formats did not enable the paste command.");
        var executed = CreatePasteCommandEvent<ExecutedRoutedEventArgs>(CommandManager.PreviewExecutedEvent);
        composer.RaiseEvent(executed);
        Ensure(executed.Handled, "The image paste command was not intercepted before reaching the text editor.");
    }

    private static async Task CompleteClipboardDownloadAsync(
        ClientWebSocket socket,
        HttpClient client,
        ProtocolEnvelope offered,
        Action<byte[]> verifyContent,
        CancellationToken cancellationToken)
    {
        Ensure(offered.Type == "transfer.offer", "Expected a clipboard image transfer offer.");
        var payload = offered.Payload;
        var transferId = payload.GetProperty("transferId").GetGuid();
        var size = payload.GetProperty("size").GetInt64();
        var name = payload.GetProperty("name").GetString();
        Ensure(payload.GetProperty("kind").GetString() == "image", "Clipboard image was offered as an ordinary file.");
        await SendAsync(socket, ProtocolJson.Create("transfer.accepted", new { transferId, plannedName = name }), cancellationToken);
        using var response = await client.GetAsync($"/bob/v1/transfers/{transferId:D}/content", cancellationToken);
        Ensure(
            response.StatusCode == HttpStatusCode.OK
                && response.Content.Headers.ContentLength == size
                && response.Content.Headers.ContentType?.MediaType == payload.GetProperty("mediaType").GetString(),
            "Clipboard image download did not match the advertised metadata.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        Ensure(bytes.LongLength == size, "Clipboard image download length differed from the offer.");
        verifyContent(bytes);
        var sha256 = Base64Url(SHA256.HashData(bytes));
        var digest = await ReceiveAsync(socket, cancellationToken);
        Ensure(
            digest.Type == "transfer.digest"
                && digest.Payload.GetProperty("transferId").GetGuid() == transferId
                && digest.Payload.GetProperty("value").GetString() == sha256
                && digest.Payload.GetProperty("bytes").GetInt64() == size,
            "Clipboard image body digest did not match the actual downloaded content.");
        await SendAsync(socket, ProtocolJson.Create("transfer.completed", new
        {
            transferId,
            bytes = size,
            sha256,
            storedName = name,
            completedAt = DateTimeOffset.UtcNow
        }), cancellationToken);
        var terminalAck = await ReceiveAsync(socket, cancellationToken);
        Ensure(
            terminalAck.Type == "transfer.terminalAck"
                && terminalAck.Payload.GetProperty("transferId").GetGuid() == transferId
                && terminalAck.Payload.GetProperty("state").GetString() == "completed",
            "Clipboard image completion did not receive a matching terminal acknowledgment.");
    }

    private static T CreatePasteCommandEvent<T>(RoutedEvent routedEvent) where T : RoutedEventArgs
    {
        // The command manager normally creates this event data internally.
        var args = Activator.CreateInstance(
            typeof(T),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [ApplicationCommands.Paste, null],
            culture: null) as T
            ?? throw new InvalidOperationException("The WPF paste command event could not be created.");
        args.RoutedEvent = routedEvent;
        return args;
    }

    private static BitmapSource CreateOtherClipboardBitmap()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, stride: 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodeClipboardBitmap(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
