using System.Text.RegularExpressions;

namespace RealTimeTranslater.Core.Translation;

public static partial class TranslationQualityGuard
{
    public static bool IsAcceptable(
        string source,
        string candidate,
        string sourceLanguage,
        string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var target =
            NormalizeLanguageCode(
                targetLanguage);

        var sourceCode =
            ResolveSourceLanguage(
                source,
                sourceLanguage);

        if (target == "ko")
        {
            return KoreanTranslationGuard.IsAcceptable(
                source,
                candidate,
                sourceCode);
        }

        if (RolePrefixRegex().IsMatch(candidate))
            return false;

        if (HasRunawayRepetition(candidate))
            return false;

        if (source.Length >= 90 &&
            candidate.Length <
                Math.Max(
                    6,
                    source.Length / 14))
        {
            return false;
        }

        if (!ContainsExpectedTargetScript(
                candidate,
                target))
        {
            return false;
        }

        if (!TargetAllowsKana(target) &&
            candidate.Any(IsKana))
        {
            return false;
        }

        if (!TargetAllowsHan(target) &&
            candidate.Any(IsHan))
        {
            return false;
        }

        if (target != "ko" &&
            candidate.Any(IsHangul))
        {
            return false;
        }

        if (target != "ru" &&
            sourceCode == "ru" &&
            candidate.Any(IsCyrillic))
        {
            return false;
        }

        if (target != "ar" &&
            sourceCode == "ar" &&
            candidate.Any(IsArabic))
        {
            return false;
        }

        if (target != "th" &&
            sourceCode == "th" &&
            candidate.Any(IsThai))
        {
            return false;
        }

        // A whole ordinary sentence should not simply pass through unchanged
        // when source and target languages differ. Short coined terms and
        // names such as NukuNuku are intentionally exempt.
        if (sourceCode != target &&
            source.Length >= 18 &&
            source.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)
                .Length >= 4 &&
            string.Equals(
                NormalizeWhitespace(source),
                NormalizeWhitespace(candidate),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static bool IsSameLanguage(
        string sourceLanguage,
        string targetLanguage)
    {
        var source =
            NormalizeLanguageCode(
                sourceLanguage);

        var target =
            NormalizeLanguageCode(
                targetLanguage);

        if (source != target)
            return false;

        // A generic Chinese source is not necessarily already in the selected
        // Simplified/Traditional variant. Force a translation/conversion when
        // the target explicitly requests one of those variants.
        if (source == "zh")
        {
            var normalizedSource =
                NormalizeFullLanguageCode(
                    sourceLanguage);
            var normalizedTarget =
                NormalizeFullLanguageCode(
                    targetLanguage);

            if (normalizedTarget is "zh-cn" or "zh-tw")
            {
                return string.Equals(
                    normalizedSource,
                    normalizedTarget,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }

    public static string NormalizeFullLanguageCode(
        string code)
        => string.IsNullOrWhiteSpace(code)
            ? "auto"
            : code.Trim()
                .Replace('_', '-')
                .ToLowerInvariant();

    public static string NormalizeLanguageCode(
        string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "auto";

        var normalized =
            code.Trim()
                .ToLowerInvariant();

        var separator =
            normalized.IndexOfAny(
                new[] { '-', '_' });

        return separator > 0
            ? normalized[..separator]
            : normalized;
    }

    private static bool ContainsExpectedTargetScript(
        string text,
        string target)
        => target switch
        {
            "ko" => text.Any(IsHangul),
            "ja" => text.Any(ch =>
                IsKana(ch) ||
                IsHan(ch)),
            "zh" => text.Any(IsHan),
            "ru" => text.Any(IsCyrillic),
            "ar" => text.Any(IsArabic),
            "th" => text.Any(IsThai),
            _ => text.Any(IsLatin)
        };

    private static bool TargetAllowsKana(string target)
        => target == "ja";

    private static bool TargetAllowsHan(string target)
        => target is "ja" or "zh";

    private static string ResolveSourceLanguage(
        string source,
        string configured)
    {
        var normalized =
            NormalizeLanguageCode(
                configured);

        if (normalized != "auto")
            return normalized;

        if (source.Any(IsKana))
            return "ja";

        if (source.Any(IsHangul))
            return "ko";

        if (source.Any(IsHan))
            return "zh";

        if (source.Any(IsCyrillic))
            return "ru";

        if (source.Any(IsArabic))
            return "ar";

        if (source.Any(IsThai))
            return "th";

        if (source.Any(IsLatin))
            return "en";

        return "auto";
    }

    private static string NormalizeWhitespace(
        string text)
        => string.Join(
            " ",
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

    private static bool HasRunawayRepetition(
        string text)
    {
        if (text.Length < 24)
            return false;

        foreach (var size in new[] { 2, 3, 4, 5, 6, 8, 10, 12 })
        {
            if (text.Length < size * 5)
                continue;

            for (var start = 0;
                 start + size * 5 <= text.Length;
                 start++)
            {
                var unit =
                    text.Substring(
                        start,
                        size);

                var repeated = true;

                for (var i = 1; i < 5; i++)
                {
                    if (!text.AsSpan(
                            start + i * size,
                            size)
                        .SequenceEqual(
                            unit.AsSpan()))
                    {
                        repeated = false;
                        break;
                    }
                }

                if (repeated)
                    return true;
            }
        }

        return false;
    }

    private static bool IsLatin(char ch)
        => ch is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '\u00C0' and <= '\u024F';

    private static bool IsHangul(char ch)
        => ch is >= '\uAC00' and <= '\uD7A3'
            or >= '\u3131' and <= '\u318E';

    private static bool IsKana(char ch)
        => ch is >= '\u3040' and <= '\u30FF'
            or >= '\u31F0' and <= '\u31FF';

    private static bool IsHan(char ch)
        => ch is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF';

    private static bool IsCyrillic(char ch)
        => ch is >= '\u0400' and <= '\u052F';

    private static bool IsArabic(char ch)
        => ch is >= '\u0600' and <= '\u06FF'
            or >= '\u0750' and <= '\u077F';

    private static bool IsThai(char ch)
        => ch is >= '\u0E00' and <= '\u0E7F';

    [GeneratedRegex(
        @"(?im)^\s*(assistant|user|system|translation|translated text)\s*:")]
    private static partial Regex RolePrefixRegex();
}
