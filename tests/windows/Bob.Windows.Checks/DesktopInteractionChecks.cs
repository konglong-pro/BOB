using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Input;
using Bob.Windows;
using Bob.Windows.Domain;
using Bob.Windows.Persistence;
using Bob.Windows.Platform;
using Bob.Windows.Security;
using Bob.Windows.Storage;
using Bob.Windows.Transport;
using Bob.Windows.UI;

internal static class DesktopInteractionChecks
{
    public static void Check()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CheckHistoryCategoriesAndPagination();
                CheckExactTextExpansion();
                CheckLazyThumbnails();
                CheckShortcutValidation();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(30)), "desktop checks did not finish within 30 seconds");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckHistoryCategoriesAndPagination() => InTemporaryDirectory(root =>
    {
        var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "identity"));
        var text = new TextHistoryStore(paths);
        var transfers = new TransferHistoryStore(paths);
        var now = DateTimeOffset.UtcNow;
        var sharedId = Guid.NewGuid();
        for (var index = 0; index < 60; index++)
        {
            text.Save(new TextHistoryRecord(index == 0 ? sharedId : Guid.NewGuid(), $"text-{index}",
                now.AddDays(-index), now.AddMinutes(index), index % 2 == 0, "Delivered", "Phone"));
        }

        transfers.Save(Snapshot(sharedId, TransferKind.Image, now.AddMinutes(100)));
        transfers.Save(Snapshot(Guid.NewGuid(), TransferKind.File, now.AddMinutes(101)));
        using var identity = CreateIdentity();
        var settings = new ReceiveDirectorySettings(paths, Path.Combine(root, "receive"));
        var sessions = new SessionCoordinator(text);
        var coordinator = new TransferCoordinator(settings, transfers, sessions);
        var model = new MainWindowViewModel(sessions, coordinator, settings, text, identity);
        var chat = Visible(model);
        Assert(chat.Length == 50 && model.HasEarlierHistory, "initial chat history did not expose a bounded page");
        Assert(chat[0].Text == "text-12" && chat[^1].Category == "Files",
            "chat did not merge text, image and file records by local record time");
        Assert(chat.Select(item => item.RecordedAt).SequenceEqual(chat.Select(item => item.RecordedAt).Order()),
            "chat order used remote timestamps instead of local history order");

        model.HistoryFilter = "Images";
        var images = Visible(model);
        Assert(images.Length == 1 && images[0].Category == "Images" && images[0].TextId == sharedId,
            "a text record sharing an image UUID leaked into the Images category");
        Assert(!model.HasEarlierHistory, "category pagination counted unrelated text records");
        model.HistoryFilter = "Files";
        Assert(Visible(model).Length == 1 && Visible(model)[0].Category == "Files",
            "the Files category included another content kind");
        model.HistoryFilter = "Text";
        var firstPage = Visible(model);
        Assert(firstPage.Length == 50 && firstPage[0].Text == "text-10" && model.HasEarlierHistory,
            "the Text category did not reset its own history page");
        Assert(model.LoadEarlierCommand.CanExecute(null), "earlier text history was not reachable");
        model.LoadEarlierCommand.Execute(null);
        var expandedPage = Visible(model);
        Assert(expandedPage.Length == 60 && expandedPage[0].Text == "text-0",
            "loading earlier records lost old text history");
        Assert(!model.HasEarlierHistory && !model.LoadEarlierCommand.CanExecute(null),
            "the earlier-record command stayed enabled after all records were shown");
        model.HistoryFilter = "Chat";
        Assert(Visible(model).Length == 50, "changing categories did not reset the history page limit");
    });

    private static void CheckExactTextExpansion()
    {
        var text = new string('x', 1199) + "\U0001F600\r\n  preserve whitespace  \nend";
        string? copied = null;
        var item = new TimelineItemViewModel(Guid.NewGuid(), text, DateTimeOffset.UtcNow, false,
            "Received", "Phone", copyText: value => copied = value);
        Assert(item.HasLongText && item.DisplayedText == new string('x', 1199) + "…",
            "collapsed text split a Unicode surrogate pair or changed the preview boundary");
        Assert(!char.IsHighSurrogate(item.DisplayedText[^2]), "collapsed text ended with an unmatched surrogate");
        item.IsExpanded = true;
        Assert(item.DisplayedText == text && item.Text == text,
            "inline expansion changed line endings, whitespace, or text content");
        item.CopyTextCommand.Execute(null);
        Assert(copied == text, "copying expanded text did not preserve the complete original body");
        item.IsExpanded = false;
        item.CopyTextCommand.Execute(null);
        Assert(copied == text, "the full-text action copied a truncated preview");
        var shortText = new TimelineItemViewModel(Guid.NewGuid(), new string('x', 1200), DateTimeOffset.UtcNow,
            false, "Received", "Phone");
        Assert(!shortText.HasLongText && shortText.DisplayedText.Length == 1200,
            "text at the preview limit was unnecessarily truncated");
    }

    private static void CheckLazyThumbnails() => InTemporaryDirectory(root =>
    {
        var imagePath = Path.Combine(root, "pixel.png");
        File.WriteAllBytes(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        var item = new TimelineItemViewModel(Guid.NewGuid(), "pixel.png", DateTimeOffset.UtcNow,
            true, "Completed", "Phone", kind: TransferKind.Image, localPath: imagePath,
            copyImage: _ => { }, loadThumbnail: false);
        Assert(item.ThumbnailLoadTask.IsCompletedSuccessfully && item.Thumbnail is null,
            "an off-page archived image decoded eagerly");
        Assert(!item.CopyImageCommand.CanExecute(null), "an undecoded image allowed clipboard copying");
        item.SetThumbnailEnabled(true);
        item.ThumbnailLoadTask.GetAwaiter().GetResult();
        Assert(item.Thumbnail is not null && item.CopyImageCommand.CanExecute(null),
            "a visible archived image did not become available after decoding");
        item.SetThumbnailEnabled(false);
        Assert(item.Thumbnail is null && !item.CopyImageCommand.CanExecute(null),
            "an image retained its decoded cache or copy action after leaving the visible page");
        item.SetThumbnailEnabled(true);
        var pending = item.ThumbnailLoadTask;
        item.SetThumbnailEnabled(false);
        pending.GetAwaiter().GetResult();
        Assert(item.Thumbnail is null, "a stale thumbnail completion repopulated an off-page image");

        var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "identity"));
        var archive = new TransferHistoryStore(paths);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 52; index++)
        {
            archive.Save(Snapshot(Guid.NewGuid(), TransferKind.Image, now.AddMinutes(index)) with
            {
                LocalPath = imagePath
            });
        }
        var text = new TextHistoryStore(paths);
        var settings = new ReceiveDirectorySettings(paths, Path.Combine(root, "receive"));
        var sessions = new SessionCoordinator(text);
        using var identity = CreateIdentity();
        var model = new MainWindowViewModel(sessions, new TransferCoordinator(settings, archive, sessions),
            settings, text, identity);
        var oldImages = model.Timeline.OrderBy(record => record.RecordedAt).Take(2).ToArray();
        Assert(oldImages.All(record => record.Thumbnail is null && record.ThumbnailLoadTask.IsCompletedSuccessfully),
            "constructing the initial history page decoded older hidden image records");
        Task.WhenAll(model.Timeline.Select(record => record.ThumbnailLoadTask)).GetAwaiter().GetResult();
        Assert(Visible(model).All(record => record.Thumbnail is not null),
            "visible image-history records did not decode");
        model.HistoryFilter = "Text";
        Assert(model.Timeline.All(record => record.Thumbnail is null),
            "switching to text history retained the image-page thumbnail cache");
    });

    private static void CheckShortcutValidation()
    {
        var shortcut = new DesktopLifetime.Shortcut(Key.B, ModifierKeys.Control | ModifierKeys.Alt);
        Assert(shortcut.IsValid && shortcut.Label == "Ctrl+Alt+B", "the default global shortcut was not represented correctly");
        Assert(new DesktopLifetime.Shortcut(Key.F12, ModifierKeys.Control | ModifierKeys.Shift).IsValid,
            "a function-key shortcut with modifiers was rejected");
        Assert(new DesktopLifetime.Shortcut(Key.D9, ModifierKeys.Control).Label == "Ctrl+9",
            "a digit shortcut was labeled using its enum identifier");
        Assert(!new DesktopLifetime.Shortcut(Key.B, ModifierKeys.None).IsValid
            && !new DesktopLifetime.Shortcut(Key.B, ModifierKeys.Shift).IsValid
            && !new DesktopLifetime.Shortcut(Key.B, ModifierKeys.Windows).IsValid
            && !new DesktopLifetime.Shortcut(Key.LeftCtrl, ModifierKeys.Control).IsValid
            && !new DesktopLifetime.Shortcut(Key.B, (ModifierKeys)16 | ModifierKeys.Control).IsValid,
            "shortcut validation accepted an unmodified key, modifier-only key, or invalid modifier mask");
    }

    private static ServerIdentity CreateIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=BOB desktop checks", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        // UI checks only need the public certificate pin; no certificate-store or persisted key writes.
        return new ServerIdentity(Guid.NewGuid(), request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1)));
    }

    private static TransferSnapshot Snapshot(Guid id, TransferKind kind, DateTimeOffset recordedAt) =>
        new(id, 1, TransferDirection.AndroidToWindows, kind,
            kind == TransferKind.Image ? "image.png" : "file.bin", 1, "application/octet-stream",
            recordedAt.AddYears(-1), TransferState.Completed, 1, null, null, null, null, null,
            recordedAt, "Phone");

    private static TimelineItemViewModel[] Visible(MainWindowViewModel model) =>
        model.HistoryView.Cast<TimelineItemViewModel>().ToArray();

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void InTemporaryDirectory(Action<string> run)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bob-desktop-interaction-checks"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { run(root); }
        finally
        {
            if (!root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to delete a directory outside the test root.");
            Directory.Delete(root, recursive: true);
        }
    }
}
