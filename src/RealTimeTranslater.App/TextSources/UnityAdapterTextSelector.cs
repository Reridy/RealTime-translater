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
        "dialog", "dialogue", "talk", "message",
        "story", "scenario", "novel", "textbox",
        "conversation", "caption", "subtitle"
    };

    private static readonly string[] SpeakerHints =
    {
        "speaker", "speakername", "charactername", "nameplate",
        "character_name", "chara_name", "talker", "talkername",
        "name_text", "nametext"
    };

    private static readonly string[] ChoiceHints =
    {
        "choice", "answer", "option", "decision", "response"
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

    private static readonly HashSet<string> NavigationControlLabels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "skip",
            "auto",
            "menu",
            "back",
            "close",
            "log",
            "history",
            "backlog",
            "hide",
            "save",
            "load",
            "config",
            "settings",
            "next"
        };

    internal static IReadOnlyList<UnityAdapterRegionDto> Select(
        UnityAdapterSnapshot snapshot,
        string overlayMode,
        bool dialogueOnly)
    {
        var sourceWidth = Math.Max(1, snapshot.Data.ScreenWidth);
        var sourceHeight = Math.Max(1, snapshot.Data.ScreenHeight);

        var visible = snapshot.Data.Regions
            .Where(IsMeaningful)
            .GroupBy(BuildDeduplicationKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        if (!dialogueOnly)
        {
            return visible
                .Take(MaximumReplaceRegions)
                .ToArray();
        }

        var scored = visible
            .Select(region => AnalyzeDialogueCandidate(
                region,
                sourceWidth,
                sourceHeight))
            .Where(candidate => candidate.IsCandidate)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Region.Y)
            .ThenBy(candidate => candidate.Region.X)
            .Take(string.Equals(
                overlayMode,
                "Subtitle",
                StringComparison.OrdinalIgnoreCase)
                    ? MaximumSubtitleRegions
                    : MaximumReplaceRegions)
            .ToArray();

        return scored
            .Select(candidate => candidate.Region)
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToArray();
    }

    private static DialogueCandidate AnalyzeDialogueCandidate(
        UnityAdapterRegionDto region,
        int screenWidth,
        int screenHeight)
    {
        var text = region.Text.Trim();
        var lowered = text.ToLowerInvariant();
        var metadata =
            $"{region.ObjectName} {region.Hierarchy} {region.SelectableName}"
                .ToLowerInvariant();

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
            region.IsSpeakerLike ||
            SpeakerHints.Any(metadata.Contains);

        var isChoice =
            region.IsChoiceLike ||
            ChoiceHints.Any(metadata.Contains);

        var hasSentenceEnding =
            SentenceEndingRegex().IsMatch(text);

        var hasJapaneseSentencePunctuation =
            JapaneseSentencePunctuationRegex().IsMatch(text);

        var looksLikeSentence =
            hasSentenceEnding ||
            hasJapaneseSentencePunctuation ||
            (words >= 5 && text.Length >= 24) ||
            (japaneseCount >= 8 && text.Length >= 16);

        var looksStructuredUi =
            StructuredUiMetadataHints.Any(metadata.Contains) ||
            lineCount >= 5 ||
            LabelLineRegex().Matches(text).Count >= 2 ||
            BulletLineRegex().Matches(text).Count >= 2 ||
            UiNoiseHints.Count(lowered.Contains) >= 2;

        if (hasSpeakerHint)
            return Reject(region);

        if (region.IsSelectable &&
            NavigationControlLabels.Contains(
                NormalizeControlLabel(text)))
        {
            return Reject(region);
        }

        // A normal Unity Button/Selectable inside the dialogue canvas is
        // usually SKIP/AUTO/etc. Keep it only when it is explicitly choice-like
        // or the button text itself clearly looks like a dialogue response.
        if (region.IsSelectable &&
            !isChoice &&
            !looksLikeSentence)
        {
            return Reject(region);
        }

        // Speaker labels are often simple Text objects rather than Selectables.
        // Suppress short name/title-like strings even when their parent lives
        // under a dialogue container.
        var looksLikeNameOnly =
            !isChoice &&
            !looksLikeSentence &&
            lineCount == 1 &&
            words is >= 1 and <= 3 &&
            text.Length <= 32;

        if (looksLikeNameOnly)
            return Reject(region);

        if (looksStructuredUi && !isChoice)
            return Reject(region);

        var probableChoiceButton =
            region.IsSelectable &&
            hasStrongDialogueHint &&
            looksLikeSentence;

        var isCandidate =
            isChoice ||
            probableChoiceButton ||
            (!region.IsSelectable &&
             (hasStrongDialogueHint || looksLikeSentence));

        if (!isCandidate)
            return Reject(region);

        var score = 0;

        if (isChoice)
            score += 140;

        if (probableChoiceButton)
            score += 100;

        if (hasStrongDialogueHint)
            score += 80;

        if (hasSentenceEnding || hasJapaneseSentencePunctuation)
            score += 50;

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

        if (UiNoiseHints.Any(lowered.Contains))
            score -= 35;

        var digitCount = text.Count(char.IsDigit);
        var letterCount = text.Count(char.IsLetter);

        if (digitCount > 0 && letterCount <= 4)
            score -= 50;

        if (lineCount >= 4)
            score -= 35;

        return new DialogueCandidate(
            region,
            IsCandidate: score >= 35,
            Score: score);
    }

    private static DialogueCandidate Reject(
        UnityAdapterRegionDto region)
        => new(
            region,
            IsCandidate: false,
            Score: int.MinValue);

    private static string NormalizeControlLabel(string text)
        => WhitespaceRegex()
            .Replace(text.Trim(), " ")
            .Trim()
            .ToLowerInvariant();

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

    private sealed record DialogueCandidate(
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
