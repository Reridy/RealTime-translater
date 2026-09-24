using System.Collections.Concurrent;
using System.Text.Json;

namespace RealTimeTranslater.Core.Translation;

public sealed class TranslationCache
{
    private readonly ConcurrentDictionary<string, string> _cache =
        new(StringComparer.Ordinal);

    private readonly string? _persistencePath;
    private readonly object _persistenceGate = new();
    private int _persistScheduled;
    private int _persistVersion;
    private int _lastPersistedVersion;

    public TranslationCache(string? persistencePath = null)
    {
        _persistencePath =
            string.IsNullOrWhiteSpace(persistencePath)
                ? null
                : persistencePath;

        LoadPersistedEntries();
    }

    public bool TryGet(
        string sourceLanguage,
        string targetLanguage,
        string text,
        out string translation)
        => _cache.TryGetValue(
            Key(
                sourceLanguage,
                targetLanguage,
                text),
            out translation!);

    public void Set(
        string sourceLanguage,
        string targetLanguage,
        string text,
        string translation)
    {
        _cache[
            Key(
                sourceLanguage,
                targetLanguage,
                text)] =
            translation;

        SchedulePersistBestEffort();
    }

    public bool Remove(
        string sourceLanguage,
        string targetLanguage,
        string text)
    {
        var removed =
            _cache.TryRemove(
                Key(
                    sourceLanguage,
                    targetLanguage,
                    text),
                out _);

        if (removed)
            SchedulePersistBestEffort();

        return removed;
    }

    public int Count => _cache.Count;

    private void LoadPersistedEntries()
    {
        if (_persistencePath is null ||
            !File.Exists(_persistencePath))
        {
            return;
        }

        try
        {
            var json =
                File.ReadAllText(
                    _persistencePath);

            var values =
                JsonSerializer.Deserialize<
                    Dictionary<string, string>>(
                    json);

            if (values is null)
                return;

            foreach (var pair in values)
            {
                if (!string.IsNullOrWhiteSpace(
                        pair.Value))
                {
                    _cache[pair.Key] =
                        pair.Value;
                }
            }
        }
        catch
        {
            // A corrupt or inaccessible cache must never prevent startup.
        }
    }

    private void SchedulePersistBestEffort()
    {
        if (_persistencePath is null)
            return;

        Interlocked.Increment(
            ref _persistVersion);

        if (Interlocked.Exchange(
                ref _persistScheduled,
                1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(450));

                    var versionToPersist =
                        Volatile.Read(
                            ref _persistVersion);

                    PersistSnapshotBestEffort();

                    Volatile.Write(
                        ref _lastPersistedVersion,
                        versionToPersist);

                    if (versionToPersist ==
                        Volatile.Read(
                            ref _persistVersion))
                    {
                        break;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(
                    ref _persistScheduled,
                    0);

                // Close the race where a new Set arrives immediately after
                // the last snapshot but before the scheduled flag is cleared.
                if (Volatile.Read(
                        ref _lastPersistedVersion) !=
                    Volatile.Read(
                        ref _persistVersion))
                {
                    SchedulePersistBestEffort();
                }
            }
        });
    }

    private void PersistSnapshotBestEffort()
    {
        if (_persistencePath is null)
            return;

        lock (_persistenceGate)
        {
            try
            {
                var directory =
                    Path.GetDirectoryName(
                        _persistencePath);

                if (!string.IsNullOrWhiteSpace(
                        directory))
                {
                    Directory.CreateDirectory(
                        directory);
                }

                var temporary =
                    _persistencePath +
                    ".tmp";

                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(
                        _cache,
                        new JsonSerializerOptions
                        {
                            WriteIndented = false
                        }));

                File.Move(
                    temporary,
                    _persistencePath,
                    overwrite: true);
            }
            catch
            {
                // Translation must keep working even if persistence fails.
            }
        }
    }

    private static string Key(
        string sourceLanguage,
        string targetLanguage,
        string text)
        => $"{sourceLanguage}\u001f{targetLanguage}\u001f{NormalizeText(text)}";

    private static string NormalizeText(
        string text)
        => string.Join(
            " ",
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
}
