using System.Text.Json;
using BridgeTransfer.Core.Persistence;

namespace BridgeTransfer.Infrastructure.Persistence;

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning, bool ReadOnly);

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. An unreadable file is moved aside (never deleted) and
/// the last good backup is used, so connection profiles are not silently lost.
/// </summary>
public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private bool _readOnly;

    public SettingsStore(AppPaths paths) => _paths = paths;

    public SettingsLoadResult Load()
    {
        var file = _paths.SettingsFile;
        if (!File.Exists(file))
        {
            if (File.Exists(file + ".bak") && TryRead(file + ".bak", out var fromBak, out _))
            {
                return new SettingsLoadResult(fromBak!.Normalize(), "Settings were restored from the backup copy.", false);
            }

            return new SettingsLoadResult(new AppSettings(), null, false);
        }

        if (TryRead(file, out var settings, out var error))
        {
            if (settings!.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                _readOnly = true;
                return new SettingsLoadResult(settings.Normalize(), "The settings file was written by a newer version of BridgeTransfer. Changes will not be saved, to avoid damaging it.", true);
            }

            return new SettingsLoadResult(settings.Normalize(), null, false);
        }

        var aside = file + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Copy(file, aside, overwrite: false);
        }
        catch (IOException)
        {
        }

        if (TryRead(file + ".bak", out var backup, out _))
        {
            return new SettingsLoadResult(backup!.Normalize(), $"The settings file could not be read ({error}). The previous good copy was restored. The unreadable file was kept as {Path.GetFileName(aside)}.", false);
        }

        _readOnly = false;
        return new SettingsLoadResult(new AppSettings(), $"The settings file could not be read ({error}). It was kept as {Path.GetFileName(aside)} and default settings are used.", false);
    }

    private static bool TryRead(string path, out AppSettings? settings, out string? error)
    {
        settings = null;
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "missing";
                return false;
            }

            settings = AtomicJson.Read<AppSettings>(path);
            if (settings is null)
            {
                error = "empty file";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            if (_readOnly)
            {
                return;
            }

            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            AtomicJson.Write(_paths.SettingsFile, settings, keepBackup: true);
        }
    }
}
