using System.Diagnostics;
using System.IO;
using RealTimeTranslater.App.Capture;
using RealTimeTranslater.App.Configuration;
using RealTimeTranslater.App.Ocr;
using RealTimeTranslater.App.Overlay;
using RealTimeTranslater.App.TextSources;
using RealTimeTranslater.Core.Models;
using RealTimeTranslater.Core.Ocr;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App.Pipeline;

public sealed class TranslationPipeline : IDisposable
{
    private readonly IntPtr _targetWindow;
    private readonly WindowCaptureService _capture;
    private readonly FrameChangeDetector _changeDetector;
    private readonly TesseractOcrService _ocr;
    private readonly FrameTextStabilizer _stabilizer;
    private readonly TranslationCoordinator _translator;
    private readonly OverlayWindow _overlay;
    private readonly AppSettings _settings;
    private readonly string _sourceLanguage;
    private readonly string _targetLanguage;
    private readonly string _textSourceMode;
    private readonly IReadOnlyList<string> _baseTranslationContext;
    private readonly UnityAdapterReceiver _unityAdapterReceiver = new();
    private readonly HashSet<string> _learnedUnityTextObjects =
        new(StringComparer.Ordinal);

    private string _lastUnityTextKey = string.Empty;
    private IReadOnlyList<string> _lastVisibleSourceTexts =
        Array.Empty<string>();
    private int _retranslateRequested;
    private IReadOnlyList<TranslatedRegion> _lastUnityTranslations =
        Array.Empty<TranslatedRegion>();
    private double? _lastUnityTranslationMilliseconds;
    private string _failedUnityTextKey = string.Empty;
    private DateTimeOffset _nextUnityTranslationRetryAt =
        DateTimeOffset.MinValue;
    private string? _lastUnityTranslationError;
    private int _unityFailureCount;

    private string _pendingUnityTextKey = string.Empty;
    private DateTimeOffset _pendingUnityTextSince =
        DateTimeOffset.MinValue;
    private DateTimeOffset _lastUnityAdapterSeenAt =
        DateTimeOffset.MinValue;

    private Task<UnityTranslationAttempt>? _unityTranslationTask;
    private CancellationTokenSource? _unityTranslationCancellation;
    private string _unityTranslationTaskKey = string.Empty;

    public TranslationPipeline(
        IntPtr targetWindow,
        TesseractOcrService ocr,
        ITranslationProvider translationProvider,
        OverlayWindow overlay,
        AppSettings settings,
        string sourceLanguage,
        string targetLanguage,
        string textSourceMode)
    {
        _targetWindow = targetWindow;
        _ocr = ocr;
        _overlay = overlay;
        _settings = settings;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
        _textSourceMode = textSourceMode;
        _baseTranslationContext =
            BuildGlossaryContext(
                settings.Translation.GlossaryText);

        _capture = new WindowCaptureService();
        _changeDetector = new FrameChangeDetector(settings.ChangeThreshold);
        _stabilizer = new FrameTextStabilizer(settings.StabilityFrames);
        var cachePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "RealTimeTranslater",
                "translation-cache.json");

        _translator = new TranslationCoordinator(
            translationProvider,
            new TranslationCache(cachePath),
            contextLimit: 0);
    }

    public event Action<string>? StatusChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var frameInterval = TimeSpan.FromMilliseconds(
            1000.0 / Math.Clamp(_settings.CaptureFps, 1, 30));

        var forcedOcrFrames = 0;
        var useUnityAdapter = string.Equals(
            _textSourceMode,
            "Unity Adapter + OCR fallback",
            StringComparison.OrdinalIgnoreCase);

        using var adapterCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        var unityReceiverTask = useUnityAdapter
            ? _unityAdapterReceiver.RunAsync(adapterCancellation.Token)
            : Task.CompletedTask;

        StatusChanged?.Invoke("Running");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var loopStart = Stopwatch.GetTimestamp();
                var adapterSemanticOcrFallback = false;

                if (Interlocked.Exchange(
                        ref _retranslateRequested,
                        0) != 0)
                {
                    var removed =
                        _translator.Invalidate(
                            new[]
                            {
                                "auto",
                                _sourceLanguage
                            },
                            _targetLanguage,
                            _lastVisibleSourceTexts);

                    CancelUnityTranslation();
                    ResetUnityTextState();
                    _stabilizer.Reset();

                    forcedOcrFrames =
                        Math.Max(
                            forcedOcrFrames,
                            _settings.StabilityFrames);

                    StatusChanged?.Invoke(
                        $"Retranslate requested · invalidated {removed} cached entr{(removed == 1 ? "y" : "ies")}");
                }

                using var frame = await _capture.CaptureAsync(
                    _targetWindow,
                    cancellationToken);

                if (frame is null)
                {
                    StatusChanged?.Invoke(
                        $"Running · {_capture.BackendName} · waiting for target frame...");

                    await DelayRemaining(
                        loopStart,
                        frameInterval,
                        cancellationToken);
                    continue;
                }

                if (useUnityAdapter &&
                    _unityAdapterReceiver.TryGetLatest(
                        TimeSpan.FromSeconds(2),
                        out var unitySnapshot))
                {
                    _lastUnityAdapterSeenAt =
                        DateTimeOffset.UtcNow;

                    var selectedUnityRegions =
                        UnityAdapterTextSelector.Select(
                            unitySnapshot,
                            _settings.Overlay.Mode,
                            _settings.UnityDialogueOnly,
                            _learnedUnityTextObjects);

                    foreach (var selected in selectedUnityRegions)
                    {
                        _learnedUnityTextObjects.Add(
                            UnityAdapterTextSelector.GetObjectKey(
                                selected));
                    }

                    var unityRegions = UnityAdapterRegionMapper.Map(
                        unitySnapshot,
                        selectedUnityRegions,
                        frame);

                    if (unityRegions.Count > 0)
                    {
                        _lastVisibleSourceTexts =
                            unityRegions
                                .Select(region =>
                                    region.Text)
                                .Where(text =>
                                    !string.IsNullOrWhiteSpace(text))
                                .Distinct(
                                    StringComparer.Ordinal)
                                .ToArray();

                        var unityTextKey = BuildTextKey(
                            unityRegions);

                        var now =
                            DateTimeOffset.UtcNow;

                        if (!string.Equals(
                                unityTextKey,
                                _pendingUnityTextKey,
                                StringComparison.Ordinal))
                        {
                            _pendingUnityTextKey =
                                unityTextKey;
                            _pendingUnityTextSince =
                                now;

                            if (!string.Equals(
                                    _lastUnityTextKey,
                                    unityTextKey,
                                    StringComparison.Ordinal))
                            {
                                _lastUnityTranslations =
                                    Array.Empty<TranslatedRegion>();
                                _lastUnityTextKey =
                                    string.Empty;
                            }

                            if (_unityTranslationTask is not null &&
                                !string.Equals(
                                    _unityTranslationTaskKey,
                                    unityTextKey,
                                    StringComparison.Ordinal))
                            {
                                CancelUnityTranslation();
                            }
                        }

                        await HarvestUnityTranslationAsync(
                            unityTextKey,
                            unityRegions);

                        var requiredStability =
                            unityRegions.All(region =>
                                LooksCompleteForImmediateTranslation(
                                    region.Text))
                                ? TimeSpan.Zero
                                : TimeSpan.FromMilliseconds(75);

                        var textStable =
                            now -
                            _pendingUnityTextSince >=
                            requiredStability;

                        if (!textStable)
                        {
                            await RenderUnityAsync(
                                Array.Empty<TranslatedRegion>(),
                                frame);

                            StatusChanged?.Invoke(
                                $"Running · {_capture.BackendName} · Unity Adapter stabilizing text · {unityRegions.Count}/{unitySnapshot.Data.Regions.Count} selected text region(s)");

                            await DelayRemaining(
                                loopStart,
                                frameInterval,
                                cancellationToken);
                            continue;
                        }

                        var retryCoolingDown =
                            string.Equals(
                                unityTextKey,
                                _failedUnityTextKey,
                                StringComparison.Ordinal) &&
                            now <
                                _nextUnityTranslationRetryAt;

                        var needsTranslation =
                            !string.Equals(
                                unityTextKey,
                                _lastUnityTextKey,
                                StringComparison.Ordinal) ||
                            _lastUnityTranslations.Count !=
                                unityRegions.Count;

                        if (needsTranslation &&
                            _unityTranslationTask is null &&
                            !retryCoolingDown)
                        {
                            StartUnityTranslation(
                                unityTextKey,
                                unityRegions,
                                _baseTranslationContext
                                    .Concat(
                                        UnityAdapterTextSelector
                                            .BuildTranslationContext(
                                                unitySnapshot))
                                    .ToArray(),
                                cancellationToken);
                        }

                        if (!needsTranslation &&
                            _lastUnityTranslations.Count ==
                                unityRegions.Count)
                        {
                            _lastUnityTranslations =
                                _lastUnityTranslations
                                    .Select((translated, index) =>
                                        translated with
                                        {
                                            Bounds =
                                                unityRegions[index].Bounds,
                                            LayoutBounds =
                                                unityRegions[index].LayoutBounds,
                                            ForegroundArgb =
                                                unityRegions[index].ForegroundArgb,
                                            SourceLineCount =
                                                unityRegions[index].SourceLineCount,
                                            SourceAlignment =
                                                unityRegions[index].SourceAlignment
                                        })
                                    .ToArray();
                        }

                        var visibleTranslations =
                            string.Equals(
                                _lastUnityTextKey,
                                unityTextKey,
                                StringComparison.Ordinal)
                                ? _lastUnityTranslations
                                : Array.Empty<TranslatedRegion>();

                        await RenderUnityAsync(
                            visibleTranslations,
                            frame);

                        var unityScope =
                            _settings.UnityDialogueOnly
                                ? "smart"
                                : "all text";

                        var state =
                            BuildUnityStatusState(
                                unityTextKey,
                                retryCoolingDown);

                        StatusChanged?.Invoke(
                            $"Running · {_capture.BackendName} · Unity Adapter {unityScope} · {unityRegions.Count}/{unitySnapshot.Data.Regions.Count} selected text region(s){state}");

                        await DelayRemaining(
                            loopStart,
                            frameInterval,
                            cancellationToken);
                        continue;
                    }

                    CancelUnityTranslation();
                    ResetUnityTextState();

                    await RenderUnityAsync(
                        Array.Empty<TranslatedRegion>(),
                        frame);

                    adapterSemanticOcrFallback = true;

                    var emptyScope =
                        _settings.UnityDialogueOnly
                            ? "smart"
                            : "all text";

                    StatusChanged?.Invoke(
                        $"Running · {_capture.BackendName} · Unity Adapter {emptyScope} · 0/{unitySnapshot.Data.Regions.Count} selected text region(s) · semantic OCR fallback");
                }

                if (useUnityAdapter &&
                    !adapterSemanticOcrFallback &&
                    _lastUnityAdapterSeenAt !=
                        DateTimeOffset.MinValue &&
                    DateTimeOffset.UtcNow -
                        _lastUnityAdapterSeenAt <
                        TimeSpan.FromSeconds(5))
                {
                    CancelUnityTranslation();

                    await RenderUnityAsync(
                        Array.Empty<TranslatedRegion>(),
                        frame);

                    var receiverError =
                        string.IsNullOrWhiteSpace(
                            _unityAdapterReceiver.LastError)
                            ? string.Empty
                            : " · " +
                              _unityAdapterReceiver.LastError;

                    StatusChanged?.Invoke(
                        $"Running · {_capture.BackendName} · Unity Adapter reconnecting{receiverError}");

                    await DelayRemaining(
                        loopStart,
                        frameInterval,
                        cancellationToken);
                    continue;
                }

                var changed =
                    _changeDetector.HasSignificantChange(
                        frame.Bitmap);

                if (changed)
                {
                    forcedOcrFrames =
                        Math.Max(
                            1,
                            _settings.StabilityFrames);
                }

                if (changed || forcedOcrFrames > 0)
                {
                    if (forcedOcrFrames > 0)
                        forcedOcrFrames--;

                    var regions =
                        _ocr.Recognize(
                            frame.Bitmap);

                    if (adapterSemanticOcrFallback &&
                        _settings.UnityDialogueOnly)
                    {
                        regions =
                            SelectSemanticOcrRegions(
                                regions,
                                frame.Bitmap.Width,
                                frame.Bitmap.Height);
                    }

                    var stableRegions =
                        _stabilizer.Push(
                            regions);

                    if (stableRegions is not null)
                    {
                        _lastVisibleSourceTexts =
                            stableRegions
                                .Select(region =>
                                    region.Text)
                                .Where(text =>
                                    !string.IsNullOrWhiteSpace(text))
                                .Distinct(
                                    StringComparer.Ordinal)
                                .ToArray();

                        try
                        {
                            var translated =
                                await _translator.TranslateAsync(
                                    stableRegions,
                                    _sourceLanguage,
                                    _targetLanguage,
                                    cancellationToken,
                                    _baseTranslationContext);

                            var styled =
                                PrepareOverlayRegions(
                                    translated,
                                    frame);

                            await _overlay.Dispatcher.InvokeAsync(() =>
                                _overlay.Render(
                                    styled,
                                    frame.ScreenBounds,
                                    frame.DpiScale));

                            var fallbackNote =
                                _capture.FallbackReason is null
                                    ? string.Empty
                                    : " · WGC unavailable, using GDI";

                            var adapterNote =
                                adapterSemanticOcrFallback
                                    ? " · Unity Adapter semantic OCR fallback"
                                    : useUnityAdapter
                                        ? " · Unity Adapter waiting, OCR fallback"
                                        : string.Empty;

                            StatusChanged?.Invoke(
                                $"Running · {_capture.BackendName}{fallbackNote}{adapterNote} · OCR {stableRegions.Count} line(s) · overlay {translated.Count} line(s)");
                        }
                        catch (OperationCanceledException)
                            when (cancellationToken
                                .IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            forcedOcrFrames =
                                Math.Max(
                                    forcedOcrFrames,
                                    _settings.StabilityFrames);

                            await _overlay.Dispatcher.InvokeAsync(() =>
                                _overlay.Render(
                                    Array.Empty<TranslatedRegion>(),
                                    frame.ScreenBounds,
                                    frame.DpiScale));

                            StatusChanged?.Invoke(
                                $"Running · {_capture.BackendName} · OCR translation error, retrying: {SummarizeError(ex)}");
                        }
                    }
                    else
                    {
                        var adapterNote =
                            adapterSemanticOcrFallback
                                ? " · Unity Adapter semantic OCR fallback"
                                : useUnityAdapter
                                    ? " · Unity Adapter waiting, OCR fallback"
                                    : string.Empty;

                        StatusChanged?.Invoke(
                            $"Running · {_capture.BackendName}{adapterNote} · stabilizing OCR ({regions.Count} line(s))");
                    }
                }

                await DelayRemaining(
                    loopStart,
                    frameInterval,
                    cancellationToken);
            }
        }
        finally
        {
            CancelUnityTranslation();

            if (useUnityAdapter)
            {
                adapterCancellation.Cancel();

                try
                {
                    await unityReceiverTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    public void RequestRetranslateCurrent()
    {
        Interlocked.Exchange(
            ref _retranslateRequested,
            1);
    }

    public void Dispose()
    {
        CancelUnityTranslation();
        _capture.Dispose();
    }

    private void StartUnityTranslation(
        string textKey,
        IReadOnlyList<TextRegion> regions,
        IReadOnlyList<string> context,
        CancellationToken cancellationToken)
    {
        CancelUnityTranslation();

        _unityTranslationCancellation =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        _unityTranslationTaskKey =
            textKey;

        _unityTranslationTask =
            TranslateUnityAsync(
                textKey,
                regions,
                context,
                _unityTranslationCancellation.Token);
    }

    private async Task<UnityTranslationAttempt>
        TranslateUnityAsync(
            string textKey,
            IReadOnlyList<TextRegion> regions,
            IReadOnlyList<string> context,
            CancellationToken cancellationToken)
    {
        var started =
            Stopwatch.GetTimestamp();

        try
        {
            var translated =
                await _translator.TranslateAsync(
                    regions,
                    "auto",
                    _targetLanguage,
                    cancellationToken,
                    context);

            return new UnityTranslationAttempt(
                textKey,
                translated,
                Stopwatch.GetElapsedTime(started)
                    .TotalMilliseconds,
                Error: null,
                Canceled: false);
        }
        catch (OperationCanceledException)
        {
            return new UnityTranslationAttempt(
                textKey,
                Array.Empty<TranslatedRegion>(),
                Stopwatch.GetElapsedTime(started)
                    .TotalMilliseconds,
                Error: null,
                Canceled: true);
        }
        catch (Exception ex)
        {
            return new UnityTranslationAttempt(
                textKey,
                Array.Empty<TranslatedRegion>(),
                Stopwatch.GetElapsedTime(started)
                    .TotalMilliseconds,
                Error: ex,
                Canceled: false);
        }
    }

    private async Task HarvestUnityTranslationAsync(
        string currentTextKey,
        IReadOnlyList<TextRegion> currentRegions)
    {
        if (_unityTranslationTask is null ||
            !_unityTranslationTask.IsCompleted)
        {
            return;
        }

        var task =
            _unityTranslationTask;

        _unityTranslationTask =
            null;
        _unityTranslationTaskKey =
            string.Empty;

        _unityTranslationCancellation?.Dispose();
        _unityTranslationCancellation =
            null;

        var attempt =
            await task;

        _lastUnityTranslationMilliseconds =
            attempt.ElapsedMilliseconds;

        if (attempt.Canceled)
            return;

        if (!string.Equals(
                attempt.TextKey,
                currentTextKey,
                StringComparison.Ordinal))
        {
            return;
        }

        if (attempt.Error is not null)
        {
            RegisterUnityTranslationFailure(
                currentTextKey,
                attempt.Error);
            return;
        }

        if (attempt.Translations.Count !=
            currentRegions.Count)
        {
            RegisterUnityTranslationFailure(
                currentTextKey,
                new InvalidOperationException(
                    "Translation result count did not match the current Unity text region count."));
            return;
        }

        _lastUnityTranslations =
            attempt.Translations
                .Select((translated, index) =>
                    translated with
                    {
                        Bounds =
                            currentRegions[index].Bounds,
                        LayoutBounds =
                            currentRegions[index].LayoutBounds,
                        ForegroundArgb =
                            currentRegions[index].ForegroundArgb,
                        SourceLineCount =
                            currentRegions[index].SourceLineCount,
                        SourceAlignment =
                            currentRegions[index].SourceAlignment
                    })
                .ToArray();

        _lastUnityTextKey =
            currentTextKey;
        _failedUnityTextKey =
            string.Empty;
        _lastUnityTranslationError =
            null;
        _unityFailureCount =
            0;
    }

    private void RegisterUnityTranslationFailure(
        string textKey,
        Exception error)
    {
        _lastUnityTranslations =
            Array.Empty<TranslatedRegion>();
        _lastUnityTextKey =
            string.Empty;

        if (string.Equals(
                _failedUnityTextKey,
                textKey,
                StringComparison.Ordinal))
        {
            _unityFailureCount++;
        }
        else
        {
            _failedUnityTextKey =
                textKey;
            _unityFailureCount =
                1;
        }

        _nextUnityTranslationRetryAt =
            DateTimeOffset.UtcNow +
            UnityRetryDelay(
                _unityFailureCount);

        _lastUnityTranslationError =
            SummarizeError(
                error);
    }

    private void CancelUnityTranslation()
    {
        if (_unityTranslationCancellation is not null)
        {
            try
            {
                _unityTranslationCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _unityTranslationCancellation.Dispose();
            _unityTranslationCancellation =
                null;
        }

        _unityTranslationTask =
            null;
        _unityTranslationTaskKey =
            string.Empty;
    }

    private void ResetUnityTextState()
    {
        _lastUnityTextKey =
            string.Empty;
        _failedUnityTextKey =
            string.Empty;
        _lastUnityTranslations =
            Array.Empty<TranslatedRegion>();
        _lastUnityTranslationMilliseconds =
            null;
        _lastUnityTranslationError =
            null;
        _unityFailureCount =
            0;
        _pendingUnityTextKey =
            string.Empty;
        _pendingUnityTextSince =
            DateTimeOffset.MinValue;
    }

    private async Task RenderUnityAsync(
        IReadOnlyList<TranslatedRegion> regions,
        CaptureFrame frame)
    {
        var styled =
            PrepareOverlayRegions(
                regions,
                frame);

        await _overlay.Dispatcher.InvokeAsync(() =>
            _overlay.Render(
                styled,
                frame.ScreenBounds,
                frame.DpiScale));
    }

    private IReadOnlyList<TranslatedRegion> PrepareOverlayRegions(
        IReadOnlyList<TranslatedRegion> regions,
        CaptureFrame frame)
    {
        if (string.Equals(
                _settings.Overlay.Mode,
                "Subtitle",
                StringComparison.OrdinalIgnoreCase))
        {
            return regions;
        }

        return OverlayRegionStyler.ApplyBackgroundSamples(
            regions,
            frame);
    }

    private string BuildUnityStatusState(
        string currentTextKey,
        bool retryCoolingDown)
    {
        if (_unityTranslationTask is not null &&
            string.Equals(
                _unityTranslationTaskKey,
                currentTextKey,
                StringComparison.Ordinal))
        {
            return " · translating...";
        }

        if (_lastUnityTranslationError is not null)
        {
            var retryMs =
                retryCoolingDown
                    ? Math.Max(
                        0,
                        (_nextUnityTranslationRetryAt -
                         DateTimeOffset.UtcNow)
                        .TotalMilliseconds)
                    : 0;

            return
                $" · translation error " +
                $"(attempt {_unityFailureCount}, retry in {retryMs:0} ms): " +
                _lastUnityTranslationError;
        }

        return _lastUnityTranslationMilliseconds is double latency
            ? $" · translate {latency:0} ms"
            : string.Empty;
    }

    private string BuildUnityTextKey(
        UnityAdapterSnapshot snapshot)
    {
        var selected =
            UnityAdapterTextSelector.Select(
                snapshot,
                _settings.Overlay.Mode,
                _settings.UnityDialogueOnly,
                _learnedUnityTextObjects);

        return string.Join(
            "\u001e",
            selected.Select(region =>
                region.Text.Trim()));
    }

    private static IReadOnlyList<string>
        BuildGlossaryContext(
            string glossaryText)
    {
        if (string.IsNullOrWhiteSpace(
                glossaryText))
        {
            return Array.Empty<string>();
        }

        var entries =
            new List<string>();

        foreach (var rawLine in glossaryText
                     .Replace("\r\n", "\n")
                     .Replace("\r", "\n")
                     .Split('\n'))
        {
            var line =
                rawLine.Trim();

            if (line.Length == 0 ||
                line.StartsWith(
                    "#",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var separator =
                line.IndexOf(
                    "=>",
                    StringComparison.Ordinal);

            var separatorLength = 2;

            if (separator < 0)
            {
                separator =
                    line.IndexOf('=');
                separatorLength = 1;
            }

            if (separator <= 0 ||
                separator >=
                    line.Length -
                    separatorLength)
            {
                continue;
            }

            var source =
                line[..separator]
                    .Trim();

            var target =
                line[
                    (separator +
                     separatorLength)..]
                    .Trim();

            if (source.Length == 0 ||
                target.Length == 0)
            {
                continue;
            }

            entries.Add(
                $"Glossary: {source} => {target}");

            if (entries.Count >= 32)
                break;
        }

        return entries;
    }

    private static IReadOnlyList<TextRegion>
        SelectSemanticOcrRegions(
            IReadOnlyList<TextRegion> regions,
            int frameWidth,
            int frameHeight)
    {
        static bool EndsLikeSentence(string value)
        {
            var text = value
                .Trim()
                .TrimEnd(
                    '"',
                    '\'',
                    '”',
                    '’',
                    ')',
                    ']',
                    '}');

            return text.Length > 0 &&
                text[^1] is
                    '.' or
                    '!' or
                    '?' or
                    '。' or
                    '！' or
                    '？' or
                    '…';
        }

        static bool IsNavigationNoise(string value)
        {
            var normalized =
                string.Join(
                    " ",
                    value
                        .Trim()
                        .ToLowerInvariant()
                        .Split(
                            (char[]?)null,
                            StringSplitOptions.RemoveEmptyEntries));

            return normalized is
                "skip" or
                "auto" or
                "menu" or
                "back" or
                "close" or
                "save" or
                "load" or
                "settings" or
                "cond." or
                "cond" or
                "ap" or
                "hp" or
                "mp";
        }

        return regions
            .Where(region =>
            {
                var text =
                    region.Text.Trim();

                if (text.Length < 12 ||
                    IsNavigationNoise(text))
                {
                    return false;
                }

                var words =
                    text.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries)
                    .Length;

                var letters =
                    text.Count(char.IsLetter);

                var digits =
                    text.Count(char.IsDigit);

                var digitRatio =
                    digits /
                    (double)Math.Max(
                        1,
                        text.Length);

                var japanese =
                    text.Count(ch =>
                        ch is >= '\u3040' and <= '\u30FF' ||
                        ch is >= '\u3400' and <= '\u4DBF' ||
                        ch is >= '\u4E00' and <= '\u9FFF');

                var sentenceLike =
                    EndsLikeSentence(text) ||
                    words >= 5 ||
                    japanese >= 6;

                var wideEnough =
                    region.Bounds.Width >=
                        frameWidth * 0.18 ||
                    text.Length >= 34;

                var readable =
                    letters >= 4 ||
                    japanese >= 4;

                return sentenceLike &&
                    wideEnough &&
                    readable &&
                    digitRatio < 0.30;
            })
            .OrderBy(region =>
                region.Bounds.Y)
            .ThenBy(region =>
                region.Bounds.X)
            .Take(8)
            .ToArray();
    }

    private static bool LooksCompleteForImmediateTranslation(
        string text)
    {
        var trimmed =
            text.Trim();

        if (trimmed.Length >= 90)
            return true;

        trimmed =
            trimmed.TrimEnd(
                '"',
                '\'',
                '”',
                '’',
                ')',
                ']',
                '}');

        if (trimmed.Length == 0)
            return false;

        return trimmed[^1] is
            '.' or
            '!' or
            '?' or
            '。' or
            '！' or
            '？' or
            '…';
    }

    private static string BuildTextKey(
        IReadOnlyList<TextRegion> regions)
        => string.Join(
            "\u001e",
            regions.Select(region =>
                region.Text.Trim()));

    private static TimeSpan UnityRetryDelay(
        int failureCount)
        => failureCount switch
        {
            <= 1 => TimeSpan.FromMilliseconds(350),
            2 => TimeSpan.FromMilliseconds(750),
            3 => TimeSpan.FromSeconds(1.5),
            4 => TimeSpan.FromSeconds(2.5),
            _ => TimeSpan.FromSeconds(4)
        };

    private static string SummarizeError(
        Exception ex)
    {
        var message =
            ex.Message
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

        if (message.Contains(
                "quality",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "safe Korean translation",
                StringComparison.OrdinalIgnoreCase))
        {
            return "model output failed target-language quality checks";
        }

        if (message.Contains(
                "timed out",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "exceeded",
                StringComparison.OrdinalIgnoreCase))
        {
            return "translation request timed out";
        }

        if (message.Contains(
                "500",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "server error",
                StringComparison.OrdinalIgnoreCase))
        {
            return "temporary Ollama server error";
        }

        return message.Length <= 120
            ? message
            : message[..120] + "…";
    }

    private static async Task DelayRemaining(
        long loopStart,
        TimeSpan frameInterval,
        CancellationToken cancellationToken)
    {
        var elapsed =
            Stopwatch.GetElapsedTime(
                loopStart);

        var remaining =
            frameInterval -
            elapsed;

        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(
                remaining,
                cancellationToken);
        }
    }

    private sealed record UnityTranslationAttempt(
        string TextKey,
        IReadOnlyList<TranslatedRegion> Translations,
        double ElapsedMilliseconds,
        Exception? Error,
        bool Canceled);
}
