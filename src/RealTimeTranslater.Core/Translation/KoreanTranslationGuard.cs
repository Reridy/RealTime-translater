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

        text = CodeFenceRegex().Replace(text, string.Empty);
        text = TranslationLabelRegex().Replace(text, string.Empty);
        text = RoleLeakRegex().Replace(text, string.Empty);
        text = WhitespaceBeforeNewlineRegex().Replace(text, "\n");
        text = ExcessBlankLinesRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    public static bool IsAcceptable(
        string source,
        string candidate)
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

        if (source.Length >= 12 && hangul < 3)
            return false;

        if (han > 0)
            return false;

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
            if (IsAllowedLatinToken(source, token))
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
                item.Han == 0 &&
                item.Latin == 0)
            .OrderByDescending(item => item.Hangul)
            .FirstOrDefault();

        return candidates == null
            ? string.Empty
            : candidates.Text;
    }

    private static bool IsAllowedLatinToken(
        string source,
        string token)
    {
        if (token.Length <= 1)
            return true;

        if (token.Length <= 6 &&
            token.All(ch => char.IsUpper(ch) || char.IsDigit(ch)) &&
            source.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (char.IsUpper(token[0]) &&
            source.Contains(token, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
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

    [GeneratedRegex(@"[A-Za-z][A-Za-z'-]*")]
    private static partial Regex LatinWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex WhitespaceBeforeNewlineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLinesRegex();
}
