using System.Windows.Media.Imaging;

namespace Bob.Windows.Storage;

public sealed class ClipboardImageStore : IDisposable
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ClipboardImageStore(AppPaths paths)
    {
        _directory = Path.Combine(paths.RootDirectory, "clipboard");
        // The single-instance application owns this cache. Remove leftovers from a crash.
        if (Directory.Exists(_directory))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(_directory, "Clipboard-*.png"))
                {
                    TryDelete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unavailable cache is reported if the user later tries to paste an image.
            }
        }
    }

    public Task<string> SaveAsync(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        // Decoder-backed frames can retain UI-thread metadata even after freezing.
        // Copy their pixels into an independent bitmap before the background encoder runs.
        var snapshot = new WriteableBitmap(image);
        snapshot.Freeze();
        return Task.Run(() =>
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(
                    _directory,
                    $"Clipboard-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
                try
                {
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(snapshot));
                    encoder.Save(stream);
                    _files.Add(path);
                    return path;
                }
                catch
                {
                    TryDelete(path);
                    throw;
                }
            }
        });
    }

    public void Delete(string path)
    {
        lock (_sync)
        {
            if (_files.Contains(path))
            {
                TryDelete(path);
                if (!File.Exists(path))
                {
                    _files.Remove(path);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            foreach (var path in _files)
            {
                TryDelete(path);
            }
            _files.Clear();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked file can be cleaned on the next application start.
        }
    }
}
