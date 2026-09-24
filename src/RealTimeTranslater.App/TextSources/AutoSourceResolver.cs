namespace RealTimeTranslater.App.TextSources;

internal enum AutoSourceKind
{
    Ocr,
    Unity,
    Browser
}

internal sealed record AutoSourceDecision(
    AutoSourceKind Kind,
    string Label,
    int Score);

internal sealed class AutoSourceResolver
{
    private static readonly HashSet<string> BrowserProcesses =
        new(
            new[]
            {
                "chrome",
                "msedge",
                "brave",
                "vivaldi",
                "opera",
                "firefox"
            },
            StringComparer.OrdinalIgnoreCase);

    internal AutoSourceDecision Resolve(
        string mode,
        string targetProcessName,
        string targetTitle,
        bool unityHealthy,
        BrowserCompanionSnapshot? browserSnapshot)
    {
        if (string.Equals(
                mode,
                "OCR",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                AutoSourceKind.Ocr,
                "OCR",
                100);
        }

        if (string.Equals(
                mode,
                "Unity Adapter + OCR fallback",
                StringComparison.OrdinalIgnoreCase))
        {
            return unityHealthy
                ? new(
                    AutoSourceKind.Unity,
                    "Unity Adapter",
                    100)
                : new(
                    AutoSourceKind.Ocr,
                    "OCR fallback",
                    60);
        }

        if (string.Equals(
                mode,
                "Browser Companion + OCR fallback",
                StringComparison.OrdinalIgnoreCase))
        {
            return BrowserMatches(
                    targetProcessName,
                    targetTitle,
                    browserSnapshot)
                ? new(
                    AutoSourceKind.Browser,
                    BrowserLabel(
                        browserSnapshot!),
                    100)
                : new(
                    AutoSourceKind.Ocr,
                    "OCR fallback",
                    60);
        }

        var candidates =
            new List<AutoSourceDecision>();

        if (unityHealthy)
        {
            candidates.Add(
                new(
                    AutoSourceKind.Unity,
                    "Unity Adapter",
                    100));
        }

        if (BrowserMatches(
                targetProcessName,
                targetTitle,
                browserSnapshot))
        {
            candidates.Add(
                new(
                    AutoSourceKind.Browser,
                    BrowserLabel(
                        browserSnapshot!),
                    string.Equals(
                        browserSnapshot!.Kind,
                        "youtube-captions",
                        StringComparison.OrdinalIgnoreCase)
                        ? 110
                        : 96));
        }

        candidates.Add(
            new(
                AutoSourceKind.Ocr,
                "OCR",
                40));

        return candidates
            .OrderByDescending(
                candidate =>
                    candidate.Score)
            .First();
    }

    internal static bool IsBrowserProcess(
        string processName)
        => BrowserProcesses.Contains(
            processName);

    private static bool BrowserMatches(
        string targetProcessName,
        string targetTitle,
        BrowserCompanionSnapshot? snapshot)
    {
        if (snapshot is null ||
            !snapshot.Visible ||
            !IsBrowserProcess(
                targetProcessName))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(
                targetTitle) ||
            string.IsNullOrWhiteSpace(
                snapshot.Title))
        {
            return true;
        }

        var target =
            NormalizeTitle(
                targetTitle);
        var browser =
            NormalizeTitle(
                snapshot.Title);

        return target.Contains(
                browser,
                StringComparison.OrdinalIgnoreCase) ||
            browser.Contains(
                target,
                StringComparison.OrdinalIgnoreCase) ||
            CommonPrefixLength(
                target,
                browser) >= 12;
    }

    private static string BrowserLabel(
        BrowserCompanionSnapshot snapshot)
        => string.Equals(
                snapshot.Kind,
                "youtube-captions",
                StringComparison.OrdinalIgnoreCase)
            ? "YouTube Captions"
            : "Browser DOM";

    private static string NormalizeTitle(
        string value)
    {
        var title =
            value.Trim();

        foreach (var suffix in
                 new[]
                 {
                     " - Google Chrome",
                     " - Microsoft Edge",
                     " - Brave",
                     " - Mozilla Firefox",
                     " - Opera"
                 })
        {
            if (title.EndsWith(
                    suffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                title =
                    title[..^suffix.Length];
                break;
            }
        }

        return title.Trim();
    }

    private static int CommonPrefixLength(
        string left,
        string right)
    {
        var length =
            Math.Min(
                left.Length,
                right.Length);

        var i = 0;

        while (i < length &&
               char.ToUpperInvariant(
                   left[i]) ==
               char.ToUpperInvariant(
                   right[i]))
        {
            i++;
        }

        return i;
    }
}
