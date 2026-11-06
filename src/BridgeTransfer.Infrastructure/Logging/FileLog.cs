using System.Globalization;
using System.IO.Compression;
using BridgeTransfer.Core.Security;

namespace BridgeTransfer.Infrastructure.Logging;

/// <summary>
/// Small rotating log (5 files of 1 MB). Every line passes through <see cref="SecretRedactor"/>.
/// File contents and private keys are never logged. Paths and host names may appear.
/// </summary>
public sealed class FileLog
{
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly object _gate = new();

    public FileLog(string directory, long maxBytes = 1024 * 1024, int maxFiles = 5)
    {
        _directory = directory;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
    }

    public string CurrentFile => Path.Combine(_directory, "bridgetransfer.log");

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : message + " | " + ex);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level,-5} {SecretRedactor.Redact(message).Replace("\r", " ").Replace("\n", " | ")}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var file = CurrentFile;
                if (File.Exists(file) && new FileInfo(file).Length > _maxBytes)
                {
                    Rotate();
                }

                File.AppendAllText(file, line);
            }
            catch (IOException)
            {
                // Logging must never break the application.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Rotate()
    {
        for (var i = _maxFiles - 1; i >= 1; i--)
        {
            var src = Path.Combine(_directory, i == 1 ? "bridgetransfer.log" : $"bridgetransfer.{i - 1}.log");
            var dst = Path.Combine(_directory, $"bridgetransfer.{i}.log");
            if (File.Exists(src))
            {
                File.Move(src, dst, overwrite: true);
            }
        }
    }

    /// <summary>Copies the logs into a zip file chosen by the user. Nothing is uploaded.</summary>
    public void Export(string zipPath)
    {
        lock (_gate)
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            using var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var f in Directory.Exists(_directory) ? Directory.GetFiles(_directory, "bridgetransfer*.log") : Array.Empty<string>())
            {
                zip.CreateEntryFromFile(f, Path.GetFileName(f));
            }
        }
    }
}
