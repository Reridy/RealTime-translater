using System.Collections.Concurrent;

namespace RealTimeTranslater.Core.Translation;

public sealed class TranslationCache
{
    private readonly ConcurrentDictionary<string, string> _cache =
        new(StringComparer.Ordinal);

    public bool TryGet(
        string sourceLanguage,
        string targetLanguage,
        string text,
        out string translation)
        => _cache.TryGetValue(Key(sourceLanguage, targetLanguage, text), out translation!);

    public void Set(
        string sourceLanguage,
        string targetLanguage,
        string text,
        string translation)
        => _cache[Key(sourceLanguage, targetLanguage, text)] = translation;

    public int Count => _cache.Count;

    private static string Key(string sourceLanguage, string targetLanguage, string text)
        => $"{sourceLanguage}\u001f{targetLanguage}\u001f{text.Trim()}";
}
