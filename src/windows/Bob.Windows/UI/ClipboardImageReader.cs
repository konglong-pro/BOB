using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Bob.Windows.Domain;
using Bob.Windows.Storage;

namespace Bob.Windows.UI;

internal sealed record ClipboardImageContent(IReadOnlyList<string> Files, BitmapSource? Bitmap);

internal static class ClipboardImageReader
{
    private static readonly string[] PngFormats = ["PNG", "image/png"];

    public static bool CanRead(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ReadImageFiles(data).Count > 0
            || PngFormats.Any(format => data.GetDataPresent(format, autoConvert: false))
            || data.GetDataPresent(DataFormats.Bitmap, autoConvert: true);
    }

    public static ClipboardImageContent? Read(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var files = ReadImageFiles(data);
        if (files.Count > 0)
        {
            return new ClipboardImageContent(files, Bitmap: null);
        }

        foreach (var format in PngFormats)
        {
            if (!data.GetDataPresent(format, autoConvert: false))
            {
                continue;
            }

            try
            {
                if (ReadPng(data.GetData(format, autoConvert: false)) is { } png)
                {
                    return new ClipboardImageContent([], png);
                }
            }
            catch (Exception exception) when (IsInvalidImageData(exception))
            {
                // A clipboard source can also provide a usable bitmap when its PNG format is invalid.
            }
        }

        if (data.GetDataPresent(DataFormats.Bitmap, autoConvert: true)
            && data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource bitmap)
        {
            var snapshot = bitmap.Clone();
            snapshot.Freeze();
            return new ClipboardImageContent([], snapshot);
        }

        return null;
    }

    private static IReadOnlyList<string> ReadImageFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)
            || data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] paths)
        {
            return [];
        }

        return paths.Where(path =>
                !string.IsNullOrWhiteSpace(path)
                && TransferFileNames.GuessMediaType(path, TransferKind.File)
                    .StartsWith("image/", StringComparison.Ordinal))
            .ToArray();
    }

    private static BitmapSource? ReadPng(object? value)
    {
        if (value is byte[] bytes)
        {
            using var source = new MemoryStream(bytes, writable: false);
            return DecodePng(source);
        }

        if (value is not Stream stream)
        {
            return null;
        }

        if (!stream.CanSeek)
        {
            using var snapshot = new MemoryStream();
            stream.CopyTo(snapshot);
            snapshot.Position = 0;
            return DecodePng(snapshot);
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            return DecodePng(stream);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static BitmapSource DecodePng(Stream stream)
    {
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        image.Freeze();
        return image;
    }

    private static bool IsInvalidImageData(Exception exception) =>
        exception is IOException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or FormatException
            or COMException;
}
