using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    private readonly string _targetProcessName;
    private readonly string _targetTitle;
    private readonly IReadOnlyList<string> _baseTranslationContext;
    private readonly UnityAdapterReceiver _unityAdapterReceiver = new();
    private readonly BrowserCompanionReceiver _browserCompanionReceiver = new();
    private readonly AutoSourceResolver _autoSourceResolver = new();
    private readonly HashSet<string> _learnedUnityTextObjects =
        new(StringComparer.Ordinal);

    private string _lastUnityTextKey = string.Empty;
    private IReadOnlyList<string> _lastVisibleSourceTexts =
        Array.Empty<string>();
    private IReadOnlyList<TranslatedRegion> _lastVisibleTranslations =
        Array.Empty<TranslatedRegion>();
    private int _retranslateRequested;
    private int _refreshTranslationRequested;
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
    private string _lastSpeculativeStartedText = string.Empty;
    private AutoSourceKind? _lastResolvedSource;

    public TranslationPipeline(
        IntPtr targetWindow,
        TesseractOcrService ocr,
        ITranslationProvider translationProvider,
        OverlayWindow overlay,
        AppSettings settings,
        string sourceLanguage,
        string targetLanguage,
        string textSourceMode,
        string targetProcessName,
        string targetTitle)
    {
        _targetWindow = targetWindow;
        _ocr = ocr;
        _overlay = overlay;
        _settings = settings;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
        _textSourceMode = textSourceMode;
        _targetProcessName = targetProcessName;
        _targetTitle = targetTitle;
        _baseTranslationContext =
            BuildGlossaryContext(
                settings.Translation.GlossaryText);

        _capture = new WindowCaptureService();
        _changeDetector = new FrameChangeDetector(settings.ChangeThreshold);
        _stabilizer = new FrameTextStabilizer(settings.StabilityFrames);
        var cachePath =
            BuildGameCachePath(
                settings.LastTargetProfileKey);

        _translator = new TranslationCoordinator(
            translationProvider,
            new TranslationCache(cachePath),
            contextLimit: 4);
    }

    public event Action<string>? StatusChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var frameInterval = TimeSpan.FromMilliseconds(
            1000.0 / Math.Clamp(_settings.CaptureFps, 1, 30));

        var forcedOcrFrames = 0;
        var autoSource =
            string.Equals(
                _textSourceMode,
                "Auto (Recommended)",
                StringComparison.OrdinalIgnoreCase);

        var useUnityAdapter =
            autoSource ||
            string.Equals(
                _textSourceMode,
                "Unity Adapter + OCR fallback",
                StringComparison.OrdinalIgnoreCase);

        var useBrowserCompanion =
            autoSource ||
            string.Equals(
                _textSourceMode,
                "Browser Companion + OCR fallback",
                StringComparison.OrdinalIgnoreCase);

        using var adapterCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        var unityReceiverTask = useUnityAdapter
            ? _unityAdapterReceiver.RunAsync(adapterCancellation.Token)
            : Task.CompletedTask;

        var browserReceiverTask = useBrowserCompanion
            ? _browserCompanionReceiver.RunAsync(adapterCancellation.Token)
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

                if (Interlocked.Exchange(
                        ref _refreshTranslationRequested,
                        0) != 0)
                {
                    CancelUnityTranslation();
                    ResetUnityTextState();
                    _stabilizer.Reset();

                    forcedOcrFrames =
                        Math.Max(
                            forcedOcrFrames,
                            _settings.StabilityFrames);

                    StatusChanged?.Invoke(
                        "Saved correction · refreshing current text from translation memory");
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

                BrowserCompanionSnapshot? browserSnapshot =
                    null;

                if (useBrowserCompanion &&
                    _browserCompanionReceiver.TryGetLatest(
                        TimeSpan.FromSeconds(2),
                        out var freshBrowserSnapshot))
                {
                    browserSnapshot =
                        freshBrowserSnapshot;
                }

                UnityAdapterSnapshot? unitySnapshot =
                    null;

                if (useUnityAdapter &&
                    _unityAdapterReceiver.TryGetLatest(
                        TimeSpan.FromSeconds(2),
                        out var freshUnitySnapshot))
                {
                    unitySnapshot =
                        freshUnitySnapshot;

                    _lastUnityAdapterSeenAt =
                        DateTimeOffset.UtcNow;
                }

                var sourceDecision =
                    _autoSourceResolver.Resolve(
                        _textSourceMode,
                        _targetProcessName,
                        _targetTitle,
                        unitySnapshot is not null,
                        browserSnapshot);

                if (_lastResolvedSource !=
                    sourceDecision.Kind)
                {
                    CancelUnityTranslation();
                    ResetUnityTextState();
                    _lastSpeculativeStartedText =
                        string.Empty;
                    _lastResolvedSource =
                        sourceDecision.Kind;
                }

                if (sourceDecision.Kind ==
                        AutoSourceKind.Browser &&
                    browserSnapshot is not null &&
                    await HandleBrowserSnapshotAsync(
                        browserSnapshot,
                        frame,
                        sourceDecision.Label,
                        loopStart,
                        frameInterval,
                        cancellationToken))
                {
                    continue;
                }

                if (sourceDecision.Kind ==
                        AutoSourceKind.Unity &&
                    unitySnapshot is not null)
                {

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

                        var currentSourceText =
                            string.Join(
                                "\n",
                                unityRegions.Select(region =>
                                    region.Text.Trim()));

                        var speculative =
                            SpeculativeTranslationPolicy
                                .Evaluate(
                                    _lastSpeculativeStartedText,
                                    currentSourceText,
                                    now -
                                    _pendingUnityTextSince,
                                    markedPartial:
                                        !unityRegions.All(region =>
                                            LooksCompleteForImmediateTranslation(
                                                region.Text)));

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
                            !retryCoolingDown &&
                            speculative.ShouldTranslate)
                        {
                            _lastSpeculativeStartedText =
                                currentSourceText;

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

                        var exactTranslation =
                            string.Equals(
                                _lastUnityTextKey,
                                unityTextKey,
                                StringComparison.Ordinal);

                        var reuseSpeculative =
                            !exactTranslation &&
                            _lastUnityTranslations.Count ==
                                unityRegions.Count &&
                            _lastSpeculativeStartedText.Length > 0 &&
                            currentSourceText.StartsWith(
                                _lastSpeculativeStartedText,
                                StringComparison.Ordinal);

                        var visibleTranslations =
                            exactTranslation ||
                            reuseSpeculative
                                ? RemapTranslations(
                                    _lastUnityTranslations,
                                    unityRegions)
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

                        if (needsTranslation &&
                            _unityTranslationTask is null &&
                            !retryCoolingDown &&
                            !speculative.ShouldTranslate)
                        {
                            state +=
                                " · coalescing partial text";
                        }

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
                    _lastVisibleTranslations =
                        Array.Empty<TranslatedRegion>();
                    _lastVisibleSourceTexts =
                        Array.Empty<string>();

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

                if (sourceDecision.Kind ==
                        AutoSourceKind.Unity &&
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
                        RecognizeOcrRegions(
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

                            _lastVisibleTranslations =
                                translated.ToArray();

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
            adapterCancellation.Cancel();

            foreach (var task in
                     new[]
                     {
                         unityReceiverTask,
                         browserReceiverTask
                     })
            {
                try
                {
                    await task;
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

    public IReadOnlyList<TranslatedRegion> GetCurrentTranslations()
        => _lastVisibleTranslations
            .ToArray();

    public bool ApplyCorrection(
        string sourceText,
        string correctedTranslation)
    {
        if (string.IsNullOrWhiteSpace(sourceText) ||
            string.IsNullOrWhiteSpace(correctedTranslation))
        {
            return false;
        }

        _translator.StoreCorrection(
            new[]
            {
                "auto",
                _sourceLanguage
            },
            _targetLanguage,
            sourceText,
            correctedTranslation);

        Interlocked.Exchange(
            ref _refreshTranslationRequested,
            1);

        return true;
    }

    public void Dispose()
    {
        CancelUnityTranslation();
        _browserCompanionReceiver.Dispose();
        _capture.Dispose();
    }

    private async Task<bool> HandleBrowserSnapshotAsync(
        BrowserCompanionSnapshot snapshot,
        CaptureFrame frame,
        string sourceLabel,
        long loopStart,
        TimeSpan frameInterval,
        CancellationToken cancellationToken)
    {
        var regions =
            BrowserCompanionRegionMapper.Map(
                snapshot,
                frame);

        if (regions.Count == 0)
            return false;

        _lastVisibleSourceTexts =
            regions
                .Select(region =>
                    region.Text)
                .Where(text =>
                    !string.IsNullOrWhiteSpace(text))
                .Distinct(
                    StringComparer.Ordinal)
                .ToArray();

        var textKey =
            "browser:" +
            BuildTextKey(
                regions);

        var currentSourceText =
            string.Join(
                "\n",
                regions.Select(region =>
                    region.Text.Trim()));

        var now =
            DateTimeOffset.UtcNow;

        if (!string.Equals(
                textKey,
                _pendingUnityTextKey,
                StringComparison.Ordinal))
        {
            _pendingUnityTextKey =
                textKey;
            _pendingUnityTextSince =
                now;

            if (_unityTranslationTask is not null &&
                !string.Equals(
                    _unityTranslationTaskKey,
                    textKey,
                    StringComparison.Ordinal))
            {
                CancelUnityTranslation();
            }
        }

        await HarvestUnityTranslationAsync(
            textKey,
            regions);

        var markedPartial =
            snapshot.Regions.Any(region =>
                region.Partial);

        var speculative =
            SpeculativeTranslationPolicy
                .Evaluate(
                    _lastSpeculativeStartedText,
                    currentSourceText,
                    now -
                    _pendingUnityTextSince,
                    markedPartial);

        var retryCoolingDown =
            string.Equals(
                textKey,
                _failedUnityTextKey,
                StringComparison.Ordinal) &&
            now <
                _nextUnityTranslationRetryAt;

        var needsTranslation =
            !string.Equals(
                textKey,
                _lastUnityTextKey,
                StringComparison.Ordinal) ||
            _lastUnityTranslations.Count !=
                regions.Count;

        if (needsTranslation &&
            _unityTranslationTask is null &&
            !retryCoolingDown &&
            speculative.ShouldTranslate)
        {
            _lastSpeculativeStartedText =
                currentSourceText;

            var browserContext =
                _baseTranslationContext
                    .Concat(
                        new[]
                        {
                            $"Browser page: {snapshot.Title}",
                            $"Browser URL: {snapshot.Url}"
                        })
                    .ToArray();

            StartUnityTranslation(
                textKey,
                regions,
                browserContext,
                cancellationToken);
        }

        var exactTranslation =
            string.Equals(
                _lastUnityTextKey,
                textKey,
                StringComparison.Ordinal);

        var reuseSpeculative =
            !exactTranslation &&
            _lastUnityTranslations.Count ==
                regions.Count &&
            _lastSpeculativeStartedText.Length > 0 &&
            currentSourceText.StartsWith(
                _lastSpeculativeStartedText,
                StringComparison.Ordinal);

        var visibleTranslations =
            exactTranslation ||
            reuseSpeculative
                ? RemapTranslations(
                    _lastUnityTranslations,
                    regions)
                : Array.Empty<TranslatedRegion>();

        await RenderUnityAsync(
            visibleTranslations,
            frame);

        var state =
            BuildUnityStatusState(
                textKey,
                retryCoolingDown);

        if (needsTranslation &&
            _unityTranslationTask is null &&
            !retryCoolingDown &&
            !speculative.ShouldTranslate)
        {
            state +=
                " · coalescing partial text";
        }

        var metrics =
            _translator.LastMetrics;

        var metricText =
            metrics.RegionCount > 0
                ? $" · route {metrics.RouteLabel} · cache {metrics.CacheHits}/{metrics.RegionCount}"
                : string.Empty;

        StatusChanged?.Invoke(
            $"Running · {_capture.BackendName} · {sourceLabel} · {regions.Count} text region(s){state}{metricText}");

        await DelayRemaining(
            loopStart,
            frameInterval,
            cancellationToken);

        return true;
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

        _lastVisibleTranslations =
            _lastUnityTranslations.ToArray();

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

    private static IReadOnlyList<TranslatedRegion>
        RemapTranslations(
            IReadOnlyList<TranslatedRegion> translations,
            IReadOnlyList<TextRegion> regions)
    {
        if (translations.Count !=
            regions.Count)
        {
            return Array.Empty<TranslatedRegion>();
        }

        return translations
            .Select((translated, index) =>
                translated with
                {
                    Bounds =
                        regions[index].Bounds,
                    LayoutBounds =
                        regions[index].LayoutBounds,
                    ForegroundArgb =
                        regions[index].ForegroundArgb,
                    SourceLineCount =
                        regions[index].SourceLineCount,
                    SourceAlignment =
                        regions[index].SourceAlignment
                })
            .ToArray();
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

    private static string BuildGameCachePath(
        string? profileKey)
    {
        var root =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "RealTimeTranslater",
                "translation-cache");

        Directory.CreateDirectory(root);

        if (string.IsNullOrWhiteSpace(
                profileKey))
        {
            return Path.Combine(
                root,
                "v2-global.json");
        }

        var hash =
            Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        profileKey)))
                .ToLowerInvariant();

        return Path.Combine(
            root,
            "v2-" + hash[..16] + ".json");
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

    private IReadOnlyList<TextRegion> RecognizeOcrRegions(
        Bitmap bitmap)
    {
        var rectangle =
            GetOcrRegion(
                bitmap.Width,
                bitmap.Height,
                _settings.OcrRegion);

        if (rectangle.X == 0 &&
            rectangle.Y == 0 &&
            rectangle.Width == bitmap.Width &&
            rectangle.Height == bitmap.Height)
        {
            return _ocr.Recognize(
                bitmap);
        }

        using var cropped =
            bitmap.Clone(
                rectangle,
                bitmap.PixelFormat);

        return _ocr
            .Recognize(cropped)
            .Select(region =>
                region with
                {
                    Bounds =
                        new PixelRect(
                            region.Bounds.X +
                                rectangle.X,
                            region.Bounds.Y +
                                rectangle.Y,
                            region.Bounds.Width,
                            region.Bounds.Height)
                })
            .ToArray();
    }

    private static Rectangle GetOcrRegion(
        int width,
        int height,
        string? mode)
    {
        if (width < 1 ||
            height < 1)
        {
            return new Rectangle(
                0,
                0,
                Math.Max(1, width),
                Math.Max(1, height));
        }

        return mode switch
        {
            "Bottom 45%" =>
                new Rectangle(
                    0,
                    (int)Math.Round(
                        height * 0.55),
                    width,
                    Math.Max(
                        1,
                        height -
                        (int)Math.Round(
                            height * 0.55))),

            "Bottom 30%" =>
                new Rectangle(
                    0,
                    (int)Math.Round(
                        height * 0.70),
                    width,
                    Math.Max(
                        1,
                        height -
                        (int)Math.Round(
                            height * 0.70))),

            "Center 70%" =>
                new Rectangle(
                    (int)Math.Round(
                        width * 0.15),
                    (int)Math.Round(
                        height * 0.15),
                    Math.Max(
                        1,
                        (int)Math.Round(
                            width * 0.70)),
                    Math.Max(
                        1,
                        (int)Math.Round(
                            height * 0.70))),

            _ =>
                new Rectangle(
                    0,
                    0,
                    width,
                    height)
        };
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
