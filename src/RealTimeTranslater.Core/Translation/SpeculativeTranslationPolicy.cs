namespace RealTimeTranslater.Core.Translation;

public sealed record SpeculativeTranslationDecision(
    bool ShouldTranslate,
    bool IsFinal,
    string Reason);

public static class SpeculativeTranslationPolicy
{
    public static SpeculativeTranslationDecision Evaluate(
        string previousStartedText,
        string currentText,
        TimeSpan stableFor,
        bool markedPartial)
    {
        var previous =
            Normalize(
                previousStartedText);
        var current =
            Normalize(
                currentText);

        if (current.Length < 8)
        {
            return new(
                false,
                false,
                "too-short");
        }

        var complete =
            !markedPartial;

        if (complete &&
            !string.Equals(
                previous,
                current,
                StringComparison.Ordinal))
        {
            return new(
                true,
                true,
                "complete");
        }

        var minimumDebounce =
            previous.Length == 0
                ? TimeSpan.FromMilliseconds(115)
                : TimeSpan.FromMilliseconds(90);

        if (stableFor <
            minimumDebounce)
        {
            return new(
                false,
                false,
                "debounce");
        }

        if (previous.Length == 0)
        {
            return current.Length >= 18
                ? new(
                    true,
                    false,
                    "first-partial")
                : new(
                    false,
                    false,
                    "first-partial-short");
        }

        if (string.Equals(
                previous,
                current,
                StringComparison.Ordinal))
        {
            return new(
                false,
                complete,
                "unchanged");
        }

        var common =
            CommonPrefixLength(
                previous,
                current);

        var prefixRatio =
            common /
            (double)Math.Max(
                1,
                Math.Min(
                    previous.Length,
                    current.Length));

        var growth =
            current.Length -
            previous.Length;

        if (prefixRatio >= 0.78)
        {
            var meaningfulGrowth =
                growth >= 7 ||
                growth >=
                    Math.Max(
                        4,
                        (int)Math.Ceiling(
                            previous.Length *
                            0.16));

            return meaningfulGrowth
                ? new(
                    true,
                    false,
                    "prefix-growth")
                : new(
                    false,
                    false,
                    "small-prefix-growth");
        }

        if (stableFor >=
            TimeSpan.FromMilliseconds(150))
        {
            return new(
                true,
                complete,
                "replacement");
        }

        return new(
            false,
            complete,
            "unstable-replacement");
    }

    public static bool LooksComplete(
        string text)
    {
        var value =
            Normalize(text)
                .TrimEnd(
                    '"',
                    '\'',
                    '”',
                    '’',
                    ')',
                    ']',
                    '}');

        if (value.Length == 0)
            return false;

        return value[^1] is
            '.' or
            '!' or
            '?' or
            '。' or
            '！' or
            '？' or
            '…';
    }

    private static string Normalize(
        string text)
        => string.Join(
            " ",
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

    private static int CommonPrefixLength(
        string left,
        string right)
    {
        var max =
            Math.Min(
                left.Length,
                right.Length);

        var index = 0;

        while (index < max &&
               left[index] ==
               right[index])
        {
            index++;
        }

        return index;
    }
}
