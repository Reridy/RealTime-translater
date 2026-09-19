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
        CancellationToken cancellationToken,
        IReadOnlyList<string>? additionalContext = null)
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
            }
            else
            {
                misses.Add(entry);
            }
        }

        if (misses.Count > 1 &&
            _provider is IBatchTranslationProvider batchProvider)
        {
            var requestContext = BuildRequestContext(
                additionalContext);

            var requests = misses
                .Select(entry => new TranslationRequest(
                    entry.Text,
                    sourceLanguage,
                    targetLanguage,
                    requestContext))
                .ToArray();

            try
            {
                var batch =
                    await batchProvider.TranslateBatchAsync(
                        requests,
                        cancellationToken);

                if (batch.Count != misses.Count)
                {
                    throw new InvalidOperationException(
                        "Batch translation result count did not match request count.");
                }

                for (var i = 0; i < misses.Count; i++)
                {
                    var translated =
                        batch[i].Trim();

                    if (translated.Length == 0)
                        translated = misses[i].Text;

                    translatedByIndex[
                        misses[i].Index] =
                        translated;

                    _cache.Set(
                        sourceLanguage,
                        targetLanguage,
                        misses[i].Text,
                        translated);
                }

                misses.Clear();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Batch mode is an optimization. Fall back to the existing
                // single-line path if the local model cannot follow the batch
                // format reliably.
            }
        }

        foreach (var entry in misses)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var requestContext =
                BuildRequestContext(
                    additionalContext);

            var translated =
                await _provider.TranslateAsync(
                    new TranslationRequest(
                        entry.Text,
                        sourceLanguage,
                        targetLanguage,
                        requestContext),
                    cancellationToken);

            translated = translated.Trim();

            if (translated.Length == 0)
                translated = entry.Text;

            translatedByIndex[entry.Index] =
                translated;

            _cache.Set(
                sourceLanguage,
                targetLanguage,
                entry.Text,
                translated);
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
                    entry.Region.Confidence));

            Remember(
                $"{entry.Text} => {translated}");
        }

        return result;
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
