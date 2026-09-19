using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.Core.Translation;

public sealed class TranslationCoordinator
{
    private readonly ITranslationProvider _provider;
    private readonly TranslationCache _cache;
    private readonly Queue<string> _context = new();
    private readonly int _contextLimit;

    public TranslationCoordinator(
        ITranslationProvider provider,
        TranslationCache? cache = null,
        int contextLimit = 4)
    {
        _provider = provider;
        _cache = cache ?? new TranslationCache();
        _contextLimit = Math.Max(0, contextLimit);
    }

    public async Task<IReadOnlyList<TranslatedRegion>> TranslateAsync(
        IReadOnlyList<TextRegion> regions,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var result = new List<TranslatedRegion>(regions.Count);

        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = region.Text.Trim();
            if (text.Length == 0)
                continue;

            if (!_cache.TryGet(sourceLanguage, targetLanguage, text, out var translated))
            {
                translated = await _provider.TranslateAsync(
                    new TranslationRequest(
                        text,
                        sourceLanguage,
                        targetLanguage,
                        _context.ToArray()),
                    cancellationToken);

                translated = translated.Trim();
                if (translated.Length == 0)
                    translated = text;

                _cache.Set(sourceLanguage, targetLanguage, text, translated);
            }

            result.Add(new TranslatedRegion(
                text,
                translated,
                region.Bounds,
                region.Confidence));

            Remember($"{text} => {translated}");
        }

        return result;
    }

    private void Remember(string line)
    {
        if (_contextLimit == 0)
            return;

        _context.Enqueue(line);
        while (_context.Count > _contextLimit)
            _context.Dequeue();
    }
}
