using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.Core.Translation;

public sealed class TranslationCoordinator
{
    private readonly ITranslationProvider _provider;
    private readonly TranslationCache _cache;
    private readonly Queue<string> _context = new();
    private readonly int _contextLimit;

    public TranslationRunMetrics LastMetrics { get; private set; } =
        new(
            0,
            0,
            0,
            TranslationRoute.Fast);

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
        CancellationToken cancellationToken,
        IReadOnlyList<string>? additionalContext = null,
        bool transient = false)
    {
        var entries = regions
            .Select((region, index) => new PendingRegion(
                index,
                region,
                region.Text.Trim()))
            .Where(entry => entry.Text.Length > 0)
            .ToArray();

        if (entries.Length == 0)
            return Array.Empty<TranslatedRegion>();

        var translatedByIndex =
            new Dictionary<int, string>();

        var misses =
            new List<PendingRegion>();

        var cacheHits = 0;
        var providerCalls = 0;
        var strongestRoute =
            TranslationRoute.Fast;

        foreach (var entry in entries)
        {
            if (_cache.TryGet(
                    sourceLanguage,
                    targetLanguage,
                    entry.Text,
                    out var cached))
            {
                translatedByIndex[entry.Index] =
                    cached;
                cacheHits++;
            }
            else
            {
                misses.Add(entry);
            }
        }

        if (misses.Count > 1 &&
            _provider is IBatchTranslationProvider batchProvider)
        {
            var completedIndexes =
                new HashSet<int>();

            foreach (var chunk in misses.Chunk(6))
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                if (chunk.Length <= 1)
                    continue;

                var requestContext =
                    BuildRequestContext(
                        additionalContext);

                var requests = chunk
                    .Select(entry =>
                    {
                        var route =
                            TranslationDifficultyRouter
                                .Classify(
                                    entry.Text,
                                    requestContext);

                        strongestRoute =
                            Strongest(
                                strongestRoute,
                                route);

                        return new TranslationRequest(
                            entry.Text,
                            sourceLanguage,
                            targetLanguage,
                            requestContext,
                            route);
                    })
                    .ToArray();

                try
                {
                    providerCalls++;

                    var batch =
                        await batchProvider.TranslateBatchAsync(
                            requests,
                            cancellationToken);

                    if (batch.Count != chunk.Length)
                    {
                        throw new InvalidOperationException(
                            "Batch translation result count did not match request count.");
                    }

                    for (var i = 0; i < chunk.Length; i++)
                    {
                        var translated =
                            batch[i].Trim();

                        if (translated.Length == 0)
                            translated = chunk[i].Text;

                        translatedByIndex[
                            chunk[i].Index] =
                            translated;

                        completedIndexes.Add(
                            chunk[i].Index);

                        if (!transient)
                        {
                            _cache.Set(
                                sourceLanguage,
                                targetLanguage,
                                chunk[i].Text,
                                translated);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Batch mode is an optimization. Keep only this failed
                    // chunk for the proven single-line fallback path.
                }
            }

            if (completedIndexes.Count > 0)
            {
                misses = misses
                    .Where(entry =>
                        !completedIndexes.Contains(
                            entry.Index))
                    .ToList();
            }
        }

        foreach (var entry in misses)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var requestContext =
                BuildRequestContext(
                    additionalContext);

            var route =
                TranslationDifficultyRouter
                    .Classify(
                        entry.Text,
                        requestContext);

            strongestRoute =
                Strongest(
                    strongestRoute,
                    route);

            providerCalls++;

            var translated =
                await _provider.TranslateAsync(
                    new TranslationRequest(
                        entry.Text,
                        sourceLanguage,
                        targetLanguage,
                        requestContext,
                        route),
                    cancellationToken);

            translated = translated.Trim();

            if (translated.Length == 0)
                translated = entry.Text;

            translatedByIndex[entry.Index] =
                translated;

            if (!transient)
            {
                _cache.Set(
                    sourceLanguage,
                    targetLanguage,
                    entry.Text,
                    translated);
            }
        }

        var result =
            new List<TranslatedRegion>(
                entries.Length);

        foreach (var entry in entries)
        {
            var translated =
                translatedByIndex[entry.Index];

            result.Add(
                new TranslatedRegion(
                    entry.Text,
                    translated,
                    entry.Region.Bounds,
                    entry.Region.Confidence,
                    BackgroundArgb: null,
                    LayoutBounds: entry.Region.LayoutBounds,
                    ForegroundArgb: entry.Region.ForegroundArgb,
                    SourceLineCount: entry.Region.SourceLineCount,
                    SourceAlignment: entry.Region.SourceAlignment));

            if (!transient)
            {
                Remember(
                    $"{entry.Text} => {translated}");
            }
        }

        LastMetrics =
            new TranslationRunMetrics(
                entries.Length,
                cacheHits,
                providerCalls,
                strongestRoute);

        return result;
    }

    private static TranslationRoute Strongest(
        TranslationRoute left,
        TranslationRoute right)
        => (TranslationRoute)Math.Max(
            (int)left,
            (int)right);

    public void CommitTransient(
        IReadOnlyList<TranslatedRegion> translations,
        IEnumerable<string> sourceLanguages,
        string targetLanguage)
    {
        if (translations.Count == 0)
            return;

        var languages =
            sourceLanguages
                .Where(language =>
                    !string.IsNullOrWhiteSpace(language))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        foreach (var translation in translations)
        {
            if (string.IsNullOrWhiteSpace(
                    translation.OriginalText) ||
                string.IsNullOrWhiteSpace(
                    translation.TranslatedText))
            {
                continue;
            }

            foreach (var sourceLanguage in languages)
            {
                _cache.Set(
                    sourceLanguage,
                    targetLanguage,
                    translation.OriginalText,
                    translation.TranslatedText);
            }

            Remember(
                $"{translation.OriginalText} => {translation.TranslatedText}");
        }
    }

    public void StoreCorrection(
        IEnumerable<string> sourceLanguages,
        string targetLanguage,
        string sourceText,
        string correctedTranslation)
    {
        if (string.IsNullOrWhiteSpace(sourceText) ||
            string.IsNullOrWhiteSpace(correctedTranslation))
        {
            return;
        }

        foreach (var sourceLanguage in
                 sourceLanguages
                     .Where(language =>
                         !string.IsNullOrWhiteSpace(language))
                     .Distinct(
                         StringComparer.OrdinalIgnoreCase))
        {
            _cache.Set(
                sourceLanguage,
                targetLanguage,
                sourceText,
                correctedTranslation.Trim());
        }
    }

    public int Invalidate(
        IEnumerable<string> sourceLanguages,
        string targetLanguage,
        IEnumerable<string> texts)
    {
        var removed = 0;

        foreach (var sourceLanguage in
                 sourceLanguages
                     .Where(language =>
                         !string.IsNullOrWhiteSpace(language))
                     .Distinct(
                         StringComparer.OrdinalIgnoreCase))
        {
            foreach (var text in
                     texts
                         .Where(value =>
                             !string.IsNullOrWhiteSpace(value))
                         .Distinct(
                             StringComparer.Ordinal))
            {
                if (_cache.Remove(
                        sourceLanguage,
                        targetLanguage,
                        text))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    private IReadOnlyList<string> BuildRequestContext(
        IReadOnlyList<string>? additionalContext)
    {
        if (additionalContext is null ||
            additionalContext.Count == 0)
        {
            return _context.ToArray();
        }

        return _context
            .Concat(additionalContext)
            .ToArray();
    }

    private void Remember(string line)
    {
        if (_contextLimit == 0)
            return;

        _context.Enqueue(line);

        while (_context.Count > _contextLimit)
            _context.Dequeue();
    }

    private sealed record PendingRegion(
        int Index,
        TextRegion Region,
        string Text);
}
