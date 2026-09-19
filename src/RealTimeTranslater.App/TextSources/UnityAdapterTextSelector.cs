using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RealTimeTranslater.App.TextSources;

internal static partial class UnityAdapterTextSelector
{
    private const int MaximumReplaceRegions = 64;
    private const int MaximumSubtitleRegions = 6;

    private static readonly string[] StrongDialogueHints =
    {
        "dialog", "dialogue", "talk", "message", "choice",
        "story", "scenario", "novel", "textbox",
        "conversation", "caption", "subtitle"
    };

    private static readonly string[] SpeakerHints =
    {
        "speaker", "speakername", "charactername", "nameplate"
    };

    private static readonly string[] StructuredUiMetadataHints =
    {
        "tooltip", "description", "detail", "status", "build",
        "slot", "inventory", "item", "skill", "parameter",
        "stat", "help", "setting", "menu"
    };

    private static readonly string[] UiNoiseHints =
    {
        "total", "exp", "lv", "level", "atk", "def", "hp", "mp",
        "gold", "perica", "save", "settings", "inventory", "status",
        "close", "slot", "move", "click", "select", "enter", "esc",
        "menu", "skills", "luck", "intelligence", "condition", "ap",
        "cooldown", "attack speed", "move speed", "owned effect",
        "active while owned", "augments", "magic:", "sword:"
    };

    internal static IReadOnlyList<UnityAdapterRegionDto> Select(
        UnityAdapterSnapshot snapshot,
        string overlayMode)
    {
        var sourceWidth = Math.Max(1, snapshot.Data.ScreenWidth);
        var sourceHeight = Math.Max(1, snapshot.Data.ScreenHeight);

        var visible = snapshot.Data.Regions
            .Where(IsMeaningful)
            .GroupBy(BuildDeduplicationKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        if (!string.Equals(
                overlayMode,
                "Subtitle",
                StringComparison.OrdinalIgnoreCase))
        {
            return visible
                .Take(MaximumReplaceRegions)
                .ToArray();
        }

        var scored = visible
            .Select(region => AnalyzeSubtitleCandidate(
                region,
                sourceWidth,
                sourceHeight))
            .Where(candidate => candidate.IsCandidate)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Region.Y)
            .ThenBy(candidate => candidate.Region.X)
            .Take(MaximumSubtitleRegions)
            .ToArray();

        return scored
            .Select(candidate => candidate.Region)
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToArray();
    }

    private static SubtitleCandidate AnalyzeSubtitleCandidate(
        UnityAdapterRegionDto region,
        int screenWidth,
        int screenHeight)
    {
        var text = region.Text.Trim();
        var lowered = text.ToLowerInvariant();
        var metadata =
            $"{region.ObjectName} {region.Hierarchy}".ToLowerInvariant();

        var lineCount = Math.Max(
            1,
            text.Count(ch => ch == '\n') + 1);

        var words = WordRegex().Matches(text).Count;
        var japaneseCount = text
            .EnumerateRunes()
            .Count(rune => IsJapaneseRune(rune.Value));

        var hasStrongDialogueHint =
            StrongDialogueHints.Any(metadata.Contains);
        var hasSpeakerHint =
            SpeakerHints.Any(metadata.Contains);
        var hasSentenceEnding =
            SentenceEndingRegex().IsMatch(text);
        var hasJapaneseSentencePunctuation =
            JapaneseSentencePunctuationRegex().IsMatch(text);

        var looksStructuredUi =
            StructuredUiMetadataHints.Any(metadata.Contains) ||
            lineCount >= 5 ||
            LabelLineRegex().Matches(text).Count >= 2 ||
            BulletLineRegex().Matches(text).Count >= 2 ||
            UiNoiseHints.Count(lowered.Contains) >= 2;

        var looksLikeSentence =
            hasSentenceEnding ||
            hasJapaneseSentencePunctuation ||
            (words >= 5 && text.Length >= 24) ||
            (japaneseCount >= 8 && text.Length >= 16);

        var isChoice =
            metadata.Contains("choice", StringComparison.Ordinal);

        // Subtitle mode should be conservative. A short name by itself,
        // a stat block, or a tooltip is not enough evidence of dialogue.
        var isCandidate =
            !looksStructuredUi &&
            (hasStrongDialogueHint ||
             isChoice ||
             looksLikeSentence);

        if (!isCandidate)
        {
            return new SubtitleCandidate(
                region,
                IsCandidate: false,
                Score: int.MinValue);
        }

        var score = 0;

        if (hasStrongDialogueHint)
            score += 120;

        if (isChoice)
            score += 70;

        if (hasSentenceEnding || hasJapaneseSentencePunctuation)
            score += 45;

        if (words >= 5)
            score += 25;

        if (text.Length >= 30)
            score += 20;

        if (japaneseCount >= 8)
            score += 20;

        if (region.Width >= screenWidth * 0.35)
            score += 15;

        if (region.Y >= screenHeight * 0.50)
            score += 12;

        if (lineCount is 2 or 3)
            score += 8;

        if (hasSpeakerHint && !looksLikeSentence)
            score -= 40;

        if (UiNoiseHints.Any(lowered.Contains))
            score -= 35;

        var digitCount = text.Count(char.IsDigit);
        var letterCount = text.Count(char.IsLetter);

        if (digitCount > 0 && letterCount <= 4)
            score -= 50;

        if (lineCount >= 4)
            score -= 35;

        return new SubtitleCandidate(
            region,
            IsCandidate: score >= 35,
            Score: score);
    }

    private static bool IsMeaningful(UnityAdapterRegionDto region)
    {
        var text = region.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (region.Width < 2 || region.Height < 2)
            return false;

        var letters = 0;
        var japanese = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter)
            {
                letters++;
            }

            if (IsJapaneseRune(rune.Value))
                japanese++;
        }

        return letters > 0 || japanese > 0;
    }

    private static string BuildDeduplicationKey(
        UnityAdapterRegionDto region)
    {
        static int Bucket(int value) => value / 8;

        return string.Join(
            "\u001f",
            region.Text.Trim(),
            Bucket(region.X),
            Bucket(region.Y),
            Bucket(region.Width),
            Bucket(region.Height));
    }

    private static bool IsJapaneseRune(int value)
        => value is >= 0x3040 and <= 0x30FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xFF66 and <= 0xFF9D;

    private sealed record SubtitleCandidate(
        UnityAdapterRegionDto Region,
        bool IsCandidate,
        int Score);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex("[.!?！？。…](?:[\\\"\'”’）)]*)$")]
    private static partial Regex SentenceEndingRegex();

    [GeneratedRegex("[！？。…]")]
    private static partial Regex JapaneseSentencePunctuationRegex();

    [GeneratedRegex(@"(?m)^\s*[\p{L}\p{N} _-]{2,24}:")]
    private static partial Regex LabelLineRegex();

    [GeneratedRegex(@"(?m)^\s*[-•*]\s+")]
    private static partial Regex BulletLineRegex();
}
