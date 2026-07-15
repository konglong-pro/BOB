using System.Text;
using Bob.Windows.Domain;

namespace Bob.Windows.Storage;

internal static class TransferFileNames
{
    private const int MaximumUtf8Bytes = 200;
    private static readonly HashSet<char> InvalidCharacters =
    [
        '/', '\\', '\0', '<', '>', ':', '"', '|', '?', '*'
    ];

    public static string Sanitize(string untrustedName)
    {
        var basename = Path.GetFileName(untrustedName.Replace('/', Path.DirectorySeparatorChar))
            .Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(basename.Length);
        foreach (var rune in basename.EnumerateRunes())
        {
            if (Rune.IsControl(rune)
                || (rune.IsAscii && InvalidCharacters.Contains((char)rune.Value)))
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        var safeName = builder.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(safeName))
        {
            safeName = "unnamed";
        }

        var reservedStem = safeName
            .Split('.', 2, StringSplitOptions.None)[0]
            .TrimEnd(' ', '.');
        if (IsReserved(reservedStem))
        {
            safeName = "_" + safeName;
        }

        return TruncatePreservingExtension(safeName, MaximumUtf8Bytes);
    }

    public static string ChooseAvailableName(string rootDirectory, string sanitizedName)
    {
        var root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);

        var extension = Path.GetExtension(sanitizedName);
        var stem = Path.GetFileNameWithoutExtension(sanitizedName);
        for (var suffix = 0; suffix < int.MaxValue; suffix++)
        {
            var candidateName = sanitizedName;
            if (suffix > 0)
            {
                var suffixMarker = $" ({suffix})";
                var extensionBudget = MaximumUtf8Bytes
                    - Encoding.UTF8.GetByteCount(suffixMarker);
                var suffixAndExtension = suffixMarker
                    + TruncateUtf8(extension, Math.Max(extensionBudget, 0));
                var remainingBytes = MaximumUtf8Bytes
                    - Encoding.UTF8.GetByteCount(suffixAndExtension);
                candidateName = TruncateUtf8(stem, Math.Max(remainingBytes, 0))
                    + suffixAndExtension;
            }
            var candidatePath = EnsureInsideRoot(root, candidateName);
            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                return candidateName;
            }
        }

        throw new IOException("Could not allocate a unique destination filename.");
    }

    public static string GetPathInsideRoot(string rootDirectory, string basename) =>
        EnsureInsideRoot(Path.GetFullPath(rootDirectory), basename);

    public static string GuessMediaType(string path, TransferKind kind)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".avif" => "image/avif",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".heic" => "image/heic",
            ".jpeg" or ".jpg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            _ when kind == TransferKind.Image => "application/octet-stream",
            _ => "application/octet-stream"
        };
    }

    private static string EnsureInsideRoot(string root, string basename)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, basename));
        var rootedPrefix = root.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The destination filename escaped the receive directory.");
        }

        return candidate;
    }

    private static bool IsReserved(string stem)
    {
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9';
    }

    private static string TruncatePreservingExtension(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var extension = Path.GetExtension(value);
        var extensionBytes = Encoding.UTF8.GetByteCount(extension);
        if (string.IsNullOrEmpty(extension) || extensionBytes >= maximumBytes)
        {
            return TruncateUtf8(value, maximumBytes);
        }

        var stem = Path.GetFileNameWithoutExtension(value);
        var truncatedStem = TruncateUtf8(stem, maximumBytes - extensionBytes);
        if (string.IsNullOrEmpty(truncatedStem))
        {
            truncatedStem = TruncateUtf8("unnamed", maximumBytes - extensionBytes);
        }

        return truncatedStem + extension;
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        var builder = new StringBuilder(value.Length);
        var usedBytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (usedBytes + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            usedBytes += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }
}
