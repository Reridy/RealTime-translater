namespace RealTimeTranslater.Core.Translation;

public static class TranslationContextGuard
{
    public static string BuildRecentTargetContext(
        IReadOnlyList<string> context,
        string currentSource,
        int maxLines = 3)
    {
        if (maxLines <= 0 ||
            context.Count == 0)
        {
            return string.Empty;
        }

        var current =
            Normalize(
                currentSource);

        var lines =
            context
                .Select(TryParseMemoryLine)
                .Where(entry =>
                    entry is not null)
                .Select(entry =>
                    entry!.Value)
                .Where(entry =>
                    entry.Target.Length > 0 &&
                    !string.Equals(
                        Normalize(entry.Source),
                        current,
                        StringComparison.Ordinal))
                .Reverse()
                .Take(maxLines)
                .Reverse()
                .Select(entry =>
                    entry.Target.Length <= 160
                        ? entry.Target
                        : entry.Target[..160] + "…")
                .Distinct(
                    StringComparer.Ordinal)
                .ToArray();

        return string.Join(
            "\n",
            lines.Select(line =>
                "- " + line));
    }

    public static bool ContainsContextLeak(
        string candidate,
        IReadOnlyList<string> context,
        string currentSource)
    {
        if (string.IsNullOrWhiteSpace(
                candidate))
        {
            return false;
        }

        var normalizedCandidate =
            Normalize(candidate);

        if (normalizedCandidate.Contains(
                "=>",
                StringComparison.Ordinal) ||
            normalizedCandidate.Contains(
                "SOURCE BEGIN",
                StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.Contains(
                "SOURCE END",
                StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.Contains(
                "CONTEXT ONLY",
                StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.Contains(
                "Recent dialogue context",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var current =
            Normalize(
                currentSource);

        foreach (var raw in context)
        {
            var parsed =
                TryParseMemoryLine(
                    raw);

            if (parsed is null)
                continue;

            var entry =
                parsed.Value;

            if (string.Equals(
                    Normalize(entry.Source),
                    current,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var previousTarget =
                Normalize(
                    entry.Target);

            if (previousTarget.Length < 18)
                continue;

            if (normalizedCandidate.Contains(
                    previousTarget,
                    StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var fragment in
                     SignificantFragments(
                         previousTarget))
            {
                if (normalizedCandidate.Contains(
                        fragment,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string>
        SignificantFragments(
            string text)
    {
        const int FragmentLength = 28;

        if (text.Length < FragmentLength)
            yield break;

        var offsets =
            new[]
            {
                0,
                Math.Max(
                    0,
                    (text.Length -
                     FragmentLength) / 2),
                Math.Max(
                    0,
                    text.Length -
                    FragmentLength)
            };

        foreach (var offset in
                 offsets.Distinct())
        {
            var fragment =
                text.Substring(
                    offset,
                    Math.Min(
                        FragmentLength,
                        text.Length - offset))
                    .Trim();

            if (fragment.Length >= 18)
                yield return fragment;
        }
    }

    private static (
        string Source,
        string Target)?
        TryParseMemoryLine(
            string raw)
    {
        var line =
            raw.Trim();

        if (line.Length == 0 ||
            line.StartsWith(
                "Glossary:",
                StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith(
                "Current speaker:",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var separator =
            line.IndexOf(
                "=>",
                StringComparison.Ordinal);

        if (separator <= 0 ||
            separator >=
                line.Length - 2)
        {
            return null;
        }

        var source =
            line[..separator]
                .Trim();

        var target =
            line[
                (separator + 2)..]
                .Trim();

        if (source.Length == 0 ||
            target.Length == 0)
        {
            return null;
        }

        return (
            source,
            target);
    }

    private static string Normalize(
        string text)
        => string.Join(
            " ",
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
}
