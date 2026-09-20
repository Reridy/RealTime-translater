using System.Text.Json;
using System.Text.RegularExpressions;

namespace RealTimeTranslater.Core.Translation;

public static partial class KoreanTranslationGuard
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();

        if (TryExtractJsonTranslation(
                text,
                out var jsonTranslation))
        {
            text = jsonTranslation;
        }

        text = CodeFenceRegex().Replace(text, string.Empty);
        text = TranslationLabelRegex().Replace(text, string.Empty);
        text = RoleLeakRegex().Replace(text, string.Empty);
        text = WhitespaceBeforeNewlineRegex().Replace(text, "\n");
        text = ExcessBlankLinesRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    public static bool IsAcceptable(
        string source,
        string candidate,
        string sourceLanguage = "auto")
    {
        var rawCandidate = candidate;

        if (RoleLeakRegex().IsMatch(rawCandidate) ||
            MetaInstructionRegex().IsMatch(rawCandidate))
        {
            return false;
        }

        candidate = Normalize(candidate);

        if (candidate.Length == 0)
            return false;

        if (ReplacementCharacterRegex().IsMatch(candidate) ||
            SuspiciousQuestionMarksRegex().IsMatch(candidate) ||
            HasRunawayRepetition(candidate))
        {
            return false;
        }

        if (candidate.Length >
            Math.Max(source.Length * 2.35, source.Length + 110))
        {
            return false;
        }

        var hangul = candidate.Count(IsHangul);
        var han = candidate.Count(IsHan);
        var kana = candidate.Count(IsKana);

        if (source.Length >= 12 && hangul < 3)
            return false;

        if (han > 0 ||
            kana > 0)
        {
            return false;
        }

        sourceLanguage =
            ResolveSourceLanguage(
                source,
                sourceLanguage);

        var sourceLetters = source.Count(char.IsLetter);
        var candidateLetters = candidate.Count(char.IsLetter);

        // Catch truncated long-paragraph generations. Korean is often more
        // compact than English/Japanese, so keep this threshold conservative.
        if (sourceLetters >= 70 &&
            candidateLetters < Math.Max(18, sourceLetters * 0.24))
        {
            return false;
        }

        var latinTokens = LatinWordRegex()
            .Matches(candidate)
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();

        foreach (var token in latinTokens)
        {
            if (IsAllowedLatinToken(
                    source,
                    token,
                    sourceLanguage))
                continue;

            return false;
        }

        return true;
    }

    public static string RecoverBestKoreanLine(string value)
    {
        value = Normalize(value);

        var candidates = value
            .Split(
                new[] { '\n', '。', '！', '？' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length >= 2)
            .Select(line => new
            {
                Text = line,
                Hangul = line.Count(IsHangul),
                Han = line.Count(IsHan),
                Latin = LatinWordRegex().Matches(line).Count
            })
            .Where(item =>
                item.Hangul >= 3 &&
                item.Han == 0)
            .OrderBy(item => item.Latin)
            .ThenByDescending(item => item.Hangul)
            .FirstOrDefault();

        return candidates == null
            ? string.Empty
            : candidates.Text;
    }

    private static bool IsAllowedLatinToken(
        string source,
        string token,
        string sourceLanguage)
    {
        if (token.Length <= 1)
            return true;

        // Short game/UI abbreviations such as AP/HP may remain in any source
        // language when they were already present in the source.
        if (token.Length <= 6 &&
            token.All(ch =>
                char.IsUpper(ch) ||
                char.IsDigit(ch)) &&
            source.Contains(
                token,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Only an English source may deliberately preserve difficult English
        // fragments in the Korean localization. This covers proper names,
        // stylized coined terms such as NukuNuku, and stutters such as
        // R-Really. For Japanese/Chinese/etc. we require Korean translation
        // or Korean phonetic rendering instead of leaking the foreign script.
        if (!string.Equals(
                sourceLanguage,
                "en",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!source.Contains(
                token,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (char.IsUpper(token[0]) &&
            source.Contains(
                token,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (StutteredEnglishTokenRegex().IsMatch(
                token) ||
            CamelCaseTokenRegex().IsMatch(
                token))
        {
            return true;
        }

        if (token.Length >= 2 &&
            token.All(ch =>
                !char.IsLetter(ch) ||
                char.IsUpper(ch)))
        {
            var matchingSourceToken =
                LatinWordRegex()
                    .Matches(source)
                    .Cast<Match>()
                    .Select(match =>
                        match.Value)
                    .FirstOrDefault(sourceToken =>
                        string.Equals(
                            sourceToken,
                            token,
                            StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(
                    matchingSourceToken) &&
                char.IsUpper(
                    matchingSourceToken[0]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractJsonTranslation(
        string text,
        out string translation)
    {
        translation = string.Empty;

        if (!text.StartsWith(
                "{",
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var json =
                JsonDocument.Parse(text);

            if (json.RootElement.ValueKind !=
                    JsonValueKind.Object ||
                !json.RootElement.TryGetProperty(
                    "translation",
                    out var value) ||
                value.ValueKind !=
                    JsonValueKind.String)
            {
                return false;
            }

            translation =
                value.GetString()?.Trim() ??
                string.Empty;

            return translation.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasRunawayRepetition(string text)
    {
        if (RepeatedPhraseRegex().IsMatch(text))
            return true;

        var compact = WhitespaceRegex().Replace(text, " ");
        if (compact.Length < 36)
            return false;

        var tokens = compact
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length < 8)
            return false;

        var mostCommon = tokens
            .GroupBy(token => token, StringComparer.Ordinal)
            .Max(group => group.Count());

        return mostCommon >= 5 &&
               mostCommon >= Math.Ceiling(tokens.Length * 0.30);
    }

    private static bool IsHangul(char ch)
        => ch is >= '\uAC00' and <= '\uD7A3'
            or >= '\u3131' and <= '\u318E';

    private static bool IsKana(char ch)
        => ch is >= '\u3040' and <= '\u30FF'
            or >= '\u31F0' and <= '\u31FF';

    private static string ResolveSourceLanguage(
        string source,
        string configured)
    {
        if (!string.Equals(
                configured,
                "auto",
                StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        if (source.Any(IsKana))
            return "ja";

        if (source.Any(IsHan))
            return "zh";

        if (source.Any(ch =>
                ch is >= 'A' and <= 'Z' ||
                ch is >= 'a' and <= 'z'))
        {
            return "en";
        }

        if (source.Any(IsHangul))
            return "ko";

        return "auto";
    }

    private static bool IsHan(char ch)
        => ch is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF';

    [GeneratedRegex(@"\x60\x60\x60(?:json|text)?|\x60\x60\x60", RegexOptions.IgnoreCase)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(
        @"(?im)^\s*(translation|translated text|korean)\s*:\s*")]
    private static partial Regex TranslationLabelRegex();

    [GeneratedRegex(
        @"(?im)^\s*(user|assistant|system)\s*:?(?:\s|$)")]
    private static partial Regex RoleLeakRegex();

    [GeneratedRegex(
        @"(?i)(please translate|translation only|corrected translation|output only|translate the|source text|target language)")]
    private static partial Regex MetaInstructionRegex();

    [GeneratedRegex(@"\uFFFD")]
    private static partial Regex ReplacementCharacterRegex();

    [GeneratedRegex(@"\?{3,}")]
    private static partial Regex SuspiciousQuestionMarksRegex();

    [GeneratedRegex(@"(.{2,14})\1{3,}")]
    private static partial Regex RepeatedPhraseRegex();

    [GeneratedRegex(@"^(?<lead>[A-Za-z])-(?i:\k<lead>)[A-Za-z'-]+$")]
    private static partial Regex StutteredEnglishTokenRegex();

    [GeneratedRegex(@"^[A-Z][a-z]+(?:[A-Z][a-z]+)+$")]
    private static partial Regex CamelCaseTokenRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z'-]*")]
    private static partial Regex LatinWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex WhitespaceBeforeNewlineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLinesRegex();
}
