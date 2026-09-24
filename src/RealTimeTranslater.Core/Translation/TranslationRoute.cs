namespace RealTimeTranslater.Core.Translation;

public enum TranslationRoute
{
    Fast,
    Standard,
    Quality
}

public static class TranslationDifficultyRouter
{
    public static TranslationRoute Classify(
        string text,
        IReadOnlyList<string>? context = null)
    {
        var normalized =
            string.Join(
                " ",
                text.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));

        if (normalized.Length == 0)
            return TranslationRoute.Fast;

        var words =
            normalized.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries)
            .Length;

        var sentenceMarks =
            normalized.Count(ch =>
                ch is '.' or '!' or '?' or
                    '。' or '！' or '？');

        var structuralComplexity =
            normalized.Count(ch =>
                ch is ';' or ':' or '(' or ')' or
                    '[' or ']' or '—');

        var quoteCount =
            normalized.Count(ch =>
                ch is '"' or '“' or '”' or
                    '\'' or '‘' or '’');

        var hasUsefulContext =
            context is not null &&
            context.Any(line =>
                line.Contains(
                    "=>",
                    StringComparison.Ordinal));

        if (normalized.Length <= 72 &&
            words <= 13 &&
            sentenceMarks <= 1 &&
            structuralComplexity == 0 &&
            quoteCount <= 2)
        {
            return TranslationRoute.Fast;
        }

        if (normalized.Length >= 190 ||
            words >= 34 ||
            sentenceMarks >= 3 ||
            structuralComplexity >= 3 ||
            (hasUsefulContext &&
             normalized.Length >= 125))
        {
            return TranslationRoute.Quality;
        }

        return TranslationRoute.Standard;
    }

    public static string Label(
        TranslationRoute route)
        => route switch
        {
            TranslationRoute.Fast => "fast",
            TranslationRoute.Quality => "quality",
            _ => "standard"
        };
}

public sealed record TranslationRunMetrics(
    int RegionCount,
    int CacheHits,
    int ProviderCalls,
    TranslationRoute Route)
{
    public string RouteLabel
        => CacheHits == RegionCount &&
           RegionCount > 0
            ? "cache"
            : TranslationDifficultyRouter
                .Label(Route);
}
