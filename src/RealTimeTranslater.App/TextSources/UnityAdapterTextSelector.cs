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

    private static readonly string[] HardStructuredUiMetadataHints =
    {
        "status", "build", "slot", "inventory", "item",
        "skill", "parameter", "stat", "setting", "menu"
    };

    private static readonly string[] ProseMetadataHints =
    {
        "description", "detail", "help", "lore", "profile",
        "flavor", "explanation", "information", "info", "bio"
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

    private static readonly HashSet<string> ShortHudLabels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "cond",
            "condition",
            "ap",
            "hp",
            "mp",
            "sp",
            "exp",
            "lv",
            "level",
            "atk",
            "def",
            "gold",
            "g",
            "turn",
            "day",
            "week"
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

    internal static IReadOnlyList<string> BuildTranslationContext(
        UnityAdapterSnapshot snapshot)
    {
        var speaker = snapshot.Data.Regions
            .Where(region =>
                region.IsSpeakerLike &&
                !string.IsNullOrWhiteSpace(region.Text))
            .Select(region => region.Text.Trim())
            .FirstOrDefault();

        return speaker is null
            ? Array.Empty<string>()
            : new[] { $"Current speaker: {speaker}" };
    }

    internal static IReadOnlyList<UnityAdapterRegionDto> Select(
        UnityAdapterSnapshot snapshot,
        string overlayMode,
        bool dialogueOnly,
        IReadOnlySet<string>? learnedTextObjects = null)
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

        var hasVisibleSpeaker = visible.Any(region =>
            region.IsSpeakerLike ||
            SpeakerHints.Any(hint =>
                $"{region.ObjectName} {region.Hierarchy}"
                    .Contains(
                        hint,
                        StringComparison.OrdinalIgnoreCase)));

        var scored = visible
            .Select(region => AnalyzeDialogueCandidate(
                region,
                sourceWidth,
                sourceHeight,
                hasVisibleSpeaker,
                learnedTextObjects?.Contains(
                    GetObjectKey(region)) == true))
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

        if (scored.Length == 0)
        {
            var rescued = visible
                .Where(region => LooksLikeSafeNarrativeFallback(
                    region,
                    sourceWidth,
                    sourceHeight))
                .OrderByDescending(region => region.Width * region.Height)
                .ThenByDescending(region => region.Text.Length)
                .Take(1)
                .Select(region => new DialogueCandidate(
                    region,
                    IsCandidate: true,
                    Score: 35))
                .ToArray();

            if (rescued.Length > 0)
                scored = rescued;
        }

        return scored
            .Select(candidate => candidate.Region)
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToArray();
    }

    private static DialogueCandidate AnalyzeDialogueCandidate(
        UnityAdapterRegionDto region,
        int screenWidth,
        int screenHeight,
        bool hasVisibleSpeaker,
        bool isLearnedTextObject)
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

        var hasSpeakerPrefix =
            SpeakerPrefixedDialogueRegex().IsMatch(text);

        var looksQuotedDialogue =
            QuotedDialogueRegex().IsMatch(text);

        var hasJapaneseSentencePunctuation =
            JapaneseSentencePunctuationRegex().IsMatch(text);

        var normalizedLabel = NormalizeControlLabel(
            ShortLabelPunctuationRegex().Replace(text, string.Empty));

        var looksLikeShortHudLabel =
            !isChoice &&
            !hasStrongDialogueHint &&
            lineCount == 1 &&
            (
                ShortHudLabels.Contains(normalizedLabel) ||
                (
                    text.Length <= 8 &&
                    words <= 2 &&
                    japaneseCount == 0 &&
                    ShortAsciiLabelRegex().IsMatch(text)
                )
            );

        var looksLikeSentence =
            !looksLikeShortHudLabel &&
            (
                hasSentenceEnding ||
                hasJapaneseSentencePunctuation ||
                hasSpeakerPrefix ||
                looksQuotedDialogue ||
                (words >= 5 && text.Length >= 24) ||
                (japaneseCount >= 8 && text.Length >= 16)
            );

        var looksLikeShortDialogue =
            hasVisibleSpeaker &&
            !region.IsSelectable &&
            !hasSpeakerHint &&
            !looksLikeShortHudLabel &&
            region.Y >= screenHeight * 0.45 &&
            region.Width >= screenWidth * 0.16 &&
            text.Length is >= 2 and <= 80 &&
            (
                words is >= 1 and <= 10 ||
                japaneseCount >= 2
            );

        var looksLikeBottomDialogue =
            !region.IsSelectable &&
            !hasSpeakerHint &&
            !looksLikeShortHudLabel &&
            region.Y >= screenHeight * 0.58 &&
            region.Width >= screenWidth * 0.30 &&
            text.Length is >= 2 and <= 180 &&
            (
                words is >= 1 and <= 18 ||
                japaneseCount >= 2
            );

        var normalizedControl =
            NormalizeControlLabel(text);

        var isNavigationControl =
            region.IsSelectable &&
            NavigationControlLabels.Contains(
                normalizedControl);

        var looksLikeWideNarrativeSelectable =
            region.IsSelectable &&
            !isNavigationControl &&
            !hasSpeakerHint &&
            !looksLikeShortHudLabel &&
            region.Width >= screenWidth * 0.28 &&
            region.Y >= screenHeight * 0.42 &&
            text.Length >= 20 &&
            (
                looksLikeSentence ||
                hasSpeakerPrefix ||
                looksQuotedDialogue ||
                words >= 5 ||
                japaneseCount >= 6
            );

        var looksLikeCinematicCaption =
            !isNavigationControl &&
            !hasSpeakerHint &&
            !looksLikeShortHudLabel &&
            region.Y >= screenHeight * 0.42 &&
            region.Width >= screenWidth * 0.20 &&
            text.Length >= 8 &&
            (
                hasSpeakerPrefix ||
                looksQuotedDialogue ||
                (
                    words >= 4 &&
                    SentencePunctuationRegex().IsMatch(text)
                )
            );

        var labelLineCount =
            LabelLineRegex().Matches(text).Count;
        var bulletLineCount =
            BulletLineRegex().Matches(text).Count;
        var uiNoiseCount =
            UiNoiseHints.Count(lowered.Contains);
        var digitCount = text.Count(char.IsDigit);
        var letterCount = text.Count(char.IsLetter);
        var digitRatio =
            text.Length == 0
                ? 0
                : digitCount / (double)text.Length;

        var hasProseMetadataHint =
            ProseMetadataHints.Any(metadata.Contains);

        var looksLikeProseBlock =
            !region.IsSelectable &&
            !looksLikeShortHudLabel &&
            words >= 7 &&
            text.Length >= 36 &&
            digitRatio < 0.18 &&
            bulletLineCount == 0 &&
            labelLineCount <= 1 &&
            uiNoiseCount <= 1 &&
            (
                hasSentenceEnding ||
                hasJapaneseSentencePunctuation ||
                SentencePunctuationRegex().Matches(text).Count >= 2 ||
                hasProseMetadataHint
            );

        var looksStructuredUi =
            HardStructuredUiMetadataHints.Any(metadata.Contains) ||
            lineCount >= 6 ||
            labelLineCount >= 2 ||
            bulletLineCount >= 2 ||
            uiNoiseCount >= 2;

        if (hasSpeakerHint || looksLikeShortHudLabel)
            return Reject(region);

        if (isNavigationControl)
        {
            return Reject(region);
        }

        // A normal Unity Button/Selectable inside the dialogue canvas is
        // usually SKIP/AUTO/etc. Keep it only when it is explicitly choice-like
        // or the button text itself clearly looks like a dialogue response.
        if (region.IsSelectable &&
            !isChoice &&
            !looksLikeSentence &&
            !looksLikeWideNarrativeSelectable)
        {
            return Reject(region);
        }

        // Speaker labels are often simple Text objects rather than Selectables.
        // Suppress short name/title-like strings even when their parent lives
        // under a dialogue container.
        var looksLikeNameOnly =
            !isChoice &&
            !looksLikeSentence &&
            !looksLikeShortDialogue &&
            !looksLikeBottomDialogue &&
            lineCount == 1 &&
            words is >= 1 and <= 3 &&
            text.Length <= 32;

        if (looksLikeNameOnly)
            return Reject(region);

        if (looksStructuredUi &&
            !isChoice &&
            !looksLikeProseBlock &&
            !looksLikeCinematicCaption)
        {
            return Reject(region);
        }

        var probableChoiceButton =
            region.IsSelectable &&
            hasStrongDialogueHint &&
            looksLikeSentence;

        var isCandidate =
            isChoice ||
            probableChoiceButton ||
            looksLikeCinematicCaption ||
            looksLikeWideNarrativeSelectable ||
            looksLikeProseBlock ||
            (
                isLearnedTextObject &&
                !region.IsSelectable &&
                !looksLikeShortHudLabel
            ) ||
            (!region.IsSelectable &&
             (hasStrongDialogueHint ||
              looksLikeSentence ||
              looksLikeShortDialogue ||
              looksLikeBottomDialogue));

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

        if (looksLikeProseBlock)
            score += 70;

        if (looksLikeShortDialogue)
            score += 55;

        if (looksLikeBottomDialogue)
            score += 45;

        if (looksLikeWideNarrativeSelectable)
            score += 85;

        if (looksLikeCinematicCaption)
            score += 110;

        if (hasSpeakerPrefix)
            score += 120;

        if (looksQuotedDialogue)
            score += 65;

        if (isLearnedTextObject)
            score += 90;

        if (hasProseMetadataHint)
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

        if (digitCount > 0 && letterCount <= 4)
            score -= 50;

        if (lineCount >= 4)
            score -= 35;

        return new DialogueCandidate(
            region,
            IsCandidate: score >= 35,
            Score: score);
    }

    private static bool LooksLikeSafeNarrativeFallback(
        UnityAdapterRegionDto region,
        int screenWidth,
        int screenHeight)
    {
        var text = region.Text?.Trim() ?? string.Empty;
        if (text.Length < 20)
            return false;

        var normalized =
            NormalizeControlLabel(text);

        if (NavigationControlLabels.Contains(normalized) ||
            ShortHudLabels.Contains(normalized) ||
            region.IsSpeakerLike)
        {
            return false;
        }

        var metadata =
            $"{region.ObjectName} {region.Hierarchy} {region.SelectableName}"
                .ToLowerInvariant();

        if (SpeakerHints.Any(metadata.Contains))
            return false;

        var words =
            WordRegex().Matches(text).Count;
        var japaneseCount =
            text.EnumerateRunes()
                .Count(rune =>
                    IsJapaneseRune(rune.Value));

        var hasSentenceShape =
            SentenceEndingRegex().IsMatch(text) ||
            JapaneseSentencePunctuationRegex().IsMatch(text) ||
            words >= 5 ||
            japaneseCount >= 6;

        if (!hasSentenceShape)
            return false;

        var lowered =
            text.ToLowerInvariant();

        var uiNoiseCount =
            UiNoiseHints.Count(
                lowered.Contains);

        var digitCount =
            text.Count(char.IsDigit);

        var digitRatio =
            digitCount /
            (double)Math.Max(
                1,
                text.Length);

        if (uiNoiseCount >= 2 ||
            digitRatio >= 0.22)
        {
            return false;
        }

        var largeEnough =
            region.Width >=
                screenWidth * 0.24 &&
            region.Height >= 10;

        var narrativePosition =
            region.Y >=
                screenHeight * 0.38 ||
            ProseMetadataHints.Any(
                metadata.Contains) ||
            StrongDialogueHints.Any(
                metadata.Contains);

        return largeEnough &&
            narrativePosition;
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

    internal static string GetObjectKey(
        UnityAdapterRegionDto region)
        => string.Join(
            "\u001f",
            region.Hierarchy?.Trim() ?? string.Empty,
            region.ObjectName?.Trim() ?? string.Empty,
            region.Kind?.Trim() ?? string.Empty);

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

    [GeneratedRegex("[.!?！？。…]")]
    private static partial Regex SentencePunctuationRegex();

    [GeneratedRegex(@"(?m)^\s*[\p{L}\p{N} _-]{2,24}:")]
    private static partial Regex LabelLineRegex();

    [GeneratedRegex("^\\s*[\\p{L}\\p{N}][\\p{L}\\p{N} ._'’-]{0,28}:\\s*[\\\"“‘']?.{3,}")]
    private static partial Regex SpeakerPrefixedDialogueRegex();

    [GeneratedRegex("^\\s*[\\\"“‘'][^\\\"”’']{4,}[\\\"”’']\\s*$")]
    private static partial Regex QuotedDialogueRegex();

    [GeneratedRegex(@"(?m)^\s*[-•*]\s+")]
    private static partial Regex BulletLineRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[.:：·・]+$")]
    private static partial Regex ShortLabelPunctuationRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9 .:/_-]{0,7}$")]
    private static partial Regex ShortAsciiLabelRegex();
}
