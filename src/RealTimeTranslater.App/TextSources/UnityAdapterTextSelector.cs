using System.Globalization;
using System.Text.RegularExpressions;

namespace RealTimeTranslater.App.TextSources;

internal static partial class UnityAdapterTextSelector
{
    private const int MaximumReplaceRegions = 64;
    private const int MaximumSubtitleRegions = 6;

    private static readonly string[] DialogueHints =
    {
        "dialog", "dialogue", "talk", "message", "speaker",
        "choice", "story", "scenario", "novel", "textbox",
        "conversation", "caption", "subtitle"
    };

    private static readonly string[] UiNoiseHints =
    {
        "total", "exp", "lv", "level", "atk", "def", "hp", "mp",
        "gold", "perica", "save", "settings", "inventory", "status",
        "close", "slot", "move", "click", "select", "enter", "esc",
        "menu", "skills", "luck", "intelligence", "condition", "ap"
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

        return visible
            .Select(region => new
            {
                Region = region,
                Score = ScoreForSubtitle(
                    region,
                    sourceWidth,
                    sourceHeight)
            })
            .Where(candidate => candidate.Score >= 30)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Region.Y)
            .ThenBy(candidate => candidate.Region.X)
            .Take(MaximumSubtitleRegions)
            .Select(candidate => candidate.Region)
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToArray();
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

    private static int ScoreForSubtitle(
        UnityAdapterRegionDto region,
        int screenWidth,
        int screenHeight)
    {
        var text = region.Text.Trim();
        var metadata =
            $"{region.ObjectName} {region.Hierarchy}".ToLowerInvariant();

        var score = 0;

        if (DialogueHints.Any(metadata.Contains))
            score += 100;

        var japaneseCount = text
            .EnumerateRunes()
            .Count(rune => IsJapaneseRune(rune.Value));

        if (japaneseCount >= 2)
            score += 60;

        var words = WordRegex().Matches(text).Count;
        if (words >= 5)
            score += 25;

        if (text.Length >= 30)
            score += 20;

        if (SentenceEndingRegex().IsMatch(text))
            score += 30;

        if (region.Width >= screenWidth * 0.35)
            score += 15;

        if (region.Y >= screenHeight * 0.45)
            score += 8;

        var lowered = text.ToLowerInvariant();
        if (UiNoiseHints.Any(hint => lowered.Contains(hint)))
            score -= 45;

        var digitCount = text.Count(char.IsDigit);
        var letterCount = text.Count(char.IsLetter);

        if (digitCount > 0 && letterCount <= 4)
            score -= 40;

        if (words <= 2 && text.Length <= 14 && japaneseCount == 0)
            score -= 20;

        return score;
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

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"[.!?！？。…](?:[\"'”’）)]*)$")]
    private static partial Regex SentenceEndingRegex();
}
