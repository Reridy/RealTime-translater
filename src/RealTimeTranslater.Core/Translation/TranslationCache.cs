using System.Collections.Concurrent;
using System.Text.Json;

namespace RealTimeTranslater.Core.Translation;

public sealed class TranslationCache
{
    private readonly ConcurrentDictionary<string, string> _cache =
        new(StringComparer.Ordinal);

    private readonly string? _persistencePath;
    private readonly object _persistenceGate = new();

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

        PersistBestEffort();
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

    private void PersistBestEffort()
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
