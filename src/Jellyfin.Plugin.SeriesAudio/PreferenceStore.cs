using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeriesAudio;

public sealed class PreferenceStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _unreadable;
    private Dictionary<string, AudioPreference> _preferences;

    public PreferenceStore(string path, ILogger<PreferenceStore> logger)
    {
        _path = path;
        try
        {
            _preferences = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, AudioPreference>>(File.ReadAllText(path))
                    ?? throw new JsonException("Preference file is null")
                : new();
            if (_preferences.Values.Any(p => p is null || p.Title is null || p.Language is null))
                throw new JsonException("Invalid preference entry");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Cannot read Series Audio preferences. Defaults remain unchanged; writes disabled to protect the original file.");
            _preferences = new();
            _unreadable = true;
        }
    }

    public AudioPreference? Get(Guid userId, Guid seasonId)
    {
        lock (_gate) return _preferences.GetValueOrDefault(Key(userId, seasonId));
    }

    public void Set(Guid userId, Guid seasonId, AudioPreference? preference)
    {
        if (userId == Guid.Empty || seasonId == Guid.Empty) throw new ArgumentException("User and season are required");
        lock (_gate)
        {
            if (_unreadable) throw new InvalidOperationException("Файл предпочтений не читается. Восстановите его из резервной копии и перезапустите сервер.");
            var updated = new Dictionary<string, AudioPreference>(_preferences);
            if (preference is null) updated.Remove(Key(userId, seasonId));
            else updated[Key(userId, seasonId)] = preference;

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, updated);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
                _preferences = updated;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private static string Key(Guid userId, Guid seasonId) => $"season/{userId:N}/{seasonId:N}";
}
