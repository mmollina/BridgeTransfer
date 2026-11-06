using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeTransfer.Infrastructure.Persistence;

/// <summary>Atomic JSON persistence: write a temporary file, flush it to disk, then rename over the target.</summary>
public static class AtomicJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void Write<T>(string path, T value, bool keepBackup = false)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(fs, value, Options);
                fs.Flush(flushToDisk: true);
            }

            if (keepBackup && File.Exists(path))
            {
                try
                {
                    // Only back up a file that is itself valid, so a corrupt file never overwrites a good backup.
                    using (var check = File.OpenRead(path))
                    {
                        JsonDocument.Parse(check).Dispose();
                    }

                    File.Copy(path, path + ".bak", overwrite: true);
                }
                catch (JsonException)
                {
                }
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    public static T? Read<T>(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<T>(fs, Options);
    }
}
