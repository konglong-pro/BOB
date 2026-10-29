using System.Security.Cryptography;
using System.Text;
using Bob.Windows;
using Bob.Windows.Domain;
using Bob.Windows.Persistence;
using Bob.Windows.Storage;
using Bob.Windows.Transport;

internal static class TransferHistoryChecks
{
    public static void CheckRestart() => InTemporaryDirectory(root =>
    {
        var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "identity"));
        var settings = new ReceiveDirectorySettings(paths, Path.Combine(root, "receive"));
        var history = new TransferHistoryStore(paths);
        var coordinator = new TransferCoordinator(settings, history, new SessionCoordinator());
        var bytes = Encoding.UTF8.GetBytes("a verified image body");
        var hash = Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var completedId = Guid.NewGuid();
        var offer = new TransferOffer(completedId, null, TransferKind.Image, "image.png", bytes.LongLength,
            "image/png", DateTimeOffset.UtcNow);
        coordinator.RegisterIncomingOffer(offer);
        Assert(history.ReadAll().Single().LocalPath is null, "accepted incoming content exposed a path");
        using (var stream = coordinator.BeginIncomingContent(completedId, bytes.LongLength).Stream)
        {
            stream.Write(bytes);
        }
        coordinator.CompleteIncomingContent(completedId, bytes.LongLength, hash);
        coordinator.ApplyIncomingDigest(new TransferDigest(completedId, "sha-256", hash, bytes.LongLength));

        var interruptedId = Guid.NewGuid();
        coordinator.RegisterIncomingOffer(offer with
        {
            TransferId = interruptedId,
            Kind = TransferKind.File,
            Name = "unfinished.bin",
            MediaType = "application/octet-stream"
        });

        var restarted = new TransferCoordinator(settings, new TransferHistoryStore(paths), new SessionCoordinator());
        var restored = restarted.ReadHistory();
        Assert(restored.Count == 2, "completed and interrupted records did not both survive restart");
        var completed = restored.Single(item => item.TransferId == completedId);
        Assert(completed.State == TransferState.Completed && File.Exists(completed.LocalPath),
            "the completed image lost its published content path");
        Assert(completed.LocalPath == Path.Combine(settings.ReceiveDirectory, "image.png"),
            "the completed image path changed across restart");
        var interrupted = restored.Single(item => item.TransferId == interruptedId);
        Assert(interrupted.State == TransferState.Failed && interrupted.ErrorCode == "network_interrupted",
            "a previous in-process transfer appeared active after restart");
        Assert(interrupted.LocalPath is null, "an interrupted incoming file exposed unverified content");
        Assert(restarted.TryOfferNextOutgoing() is null, "history was restored into the active protocol queue");
        Assert(!Directory.EnumerateFiles(paths.RootDirectory, ".transfer-history-*.tmp").Any(),
            "atomic history writes left temporary files behind");
    });

    public static void CheckArchive() => InTemporaryDirectory(root =>
    {
        var paths = new AppPaths(Path.Combine(root, "app"), Path.Combine(root, "identity"));
        var history = new TransferHistoryStore(paths);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 30; index++)
        {
            history.Save(new TransferSnapshot(Guid.NewGuid(), 4,
                index % 2 == 0 ? TransferDirection.AndroidToWindows : TransferDirection.WindowsToAndroid,
                index % 3 == 0 ? TransferKind.Image : TransferKind.File,
                $"record-{index}.bin", index, "application/octet-stream", now.AddDays(-100),
                TransferState.Completed, index, null, $"record-{index}.bin", null, null,
                Path.Combine(root, $"record-{index}.bin"), now.AddMinutes(index),
                index % 2 == 0 ? "Phone A" : "Phone B"));
        }
        var oldest = history.ReadAll().First();
        var image = oldest with { Revision = 10, LocalPath = Path.Combine(root, "published.png") };
        history.Save(image);
        history.Save(image with { Revision = 9, State = TransferState.Verifying, LocalPath = null });
        var restored = new TransferHistoryStore(paths).ReadAll();
        Assert(restored.Count == 30, "older transfers were silently limited to the recent-message window");
        Assert(restored.Select(item => item.RecordedAt).SequenceEqual(restored.Select(item => item.RecordedAt).Order()),
            "archive ordering did not use stable local record times");
        Assert(restored.Count(item => item.Kind == TransferKind.Image) == 10,
            "the image/file distinction was lost across restart");
        Assert(restored.First().State == TransferState.Completed && restored.First().LocalPath == image.LocalPath,
            "a stale snapshot overwrote a completed image");
        Assert(restored.Select(item => item.PeerName).Distinct().Count() == 2,
            "peer labels were lost across restart");
        var unsafeSnapshot = oldest with
        {
            TransferId = Guid.NewGuid(),
            LocalPath = Path.Combine(root, $".bob-{Guid.NewGuid():D}.part")
        };
        history.Save(unsafeSnapshot);
        Assert(history.ReadAll().Single(item => item.TransferId == unsafeSnapshot.TransferId).LocalPath is null,
            "a staging path became user-visible through the archive");
        File.WriteAllText(Path.Combine(paths.RootDirectory, "transfer-history-v1.json"), "{broken json");
        var damaged = new TransferHistoryStore(paths);
        Assert(damaged.ReadAll().Count == 0 && damaged.ReadWarning is not null,
            "a damaged archive prevented safe startup");
        var rejected = false;
        try { damaged.Save(image with { Revision = 11 }); }
        catch (TransferHistoryStoreException) { rejected = true; }
        Assert(rejected && File.ReadAllText(Path.Combine(paths.RootDirectory, "transfer-history-v1.json")) == "{broken json",
            "a new transfer silently replaced the damaged archive");
        File.WriteAllText(Path.Combine(paths.RootDirectory, "transfer-history-v1.json"), "{\"schemaVersion\":1}");
        Assert(new TransferHistoryStore(paths).ReadWarning is not null,
            "an archive missing its transfers collection was accepted as empty history");
    });

    public static void CheckPersistenceFailure() => InTemporaryDirectory(root =>
    {
        var blockedRoot = Path.Combine(root, "app-data-blocked-by-file");
        File.WriteAllText(blockedRoot, "not a directory");
        var paths = new AppPaths(blockedRoot, Path.Combine(root, "identity"));
        var settingsPaths = new AppPaths(Path.Combine(root, "settings"), Path.Combine(root, "identity"));
        var settings = new ReceiveDirectorySettings(settingsPaths, Path.Combine(root, "receive"));
        var coordinator = new TransferCoordinator(settings, new TransferHistoryStore(paths), new SessionCoordinator());
        var errors = new List<string>();
        coordinator.HistoryPersistenceFailed += (_, message) => errors.Add(message);
        var source = Path.Combine(root, "source.txt");
        File.WriteAllText(source, "source");
        var queued = coordinator.EnqueueOutgoing(source, TransferKind.File);
        Assert(errors.Count == 1, "an archive write failure was not reported to the UI");
        Assert(coordinator.ReadHistory().Single().TransferId == queued.TransferId,
            "an archive write failure removed the live transfer");
        Assert(coordinator.TryOfferNextOutgoing()?.TransferId == queued.TransferId,
            "an archive write failure broke the live queue");
    });

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void InTemporaryDirectory(Action<string> run)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bob-transfer-history-checks"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            run(root);
        }
        finally
        {
            if (!root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to delete a directory outside the test root.");
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
