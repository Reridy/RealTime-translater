using System.Diagnostics;
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
    private readonly UnityAdapterReceiver _unityAdapterReceiver = new();

    private string _lastUnityTextKey = string.Empty;
    private IReadOnlyList<TranslatedRegion> _lastUnityTranslations =
        Array.Empty<TranslatedRegion>();

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

        _capture = new WindowCaptureService();
        _changeDetector = new FrameChangeDetector(settings.ChangeThreshold);
        _stabilizer = new FrameTextStabilizer(settings.StabilityFrames);
        _translator = new TranslationCoordinator(
            translationProvider,
            contextLimit: 4);
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

            using var frame = await _capture.CaptureAsync(
                _targetWindow,
                cancellationToken);
            if (frame is null)
            {
                StatusChanged?.Invoke(
                    $"Running · {_capture.BackendName} · waiting for target frame...");
                await DelayRemaining(loopStart, frameInterval, cancellationToken);
                continue;
            }

            if (useUnityAdapter &&
                _unityAdapterReceiver.TryGetLatest(
                    TimeSpan.FromSeconds(2),
                    out var unitySnapshot))
            {
                var selectedUnityRegions =
                    UnityAdapterTextSelector.Select(
                        unitySnapshot,
                        _settings.Overlay.Mode);

                var unityRegions = UnityAdapterRegionMapper.Map(
                    unitySnapshot,
                    selectedUnityRegions,
                    frame);

                if (unityRegions.Count > 0)
                {
                    var unityTextKey = string.Join(
                        "\u001e",
                        unityRegions.Select(region => region.Text));

                    if (!string.Equals(
                            unityTextKey,
                            _lastUnityTextKey,
                            StringComparison.Ordinal) ||
                        _lastUnityTranslations.Count != unityRegions.Count)
                    {
                        _lastUnityTranslations =
                            await _translator.TranslateAsync(
                                unityRegions,
                                "auto",
                                _targetLanguage,
                                cancellationToken);

                        _lastUnityTextKey = unityTextKey;
                    }
                    else
                    {
                        _lastUnityTranslations =
                            _lastUnityTranslations
                                .Select((translated, index) =>
                                    translated with
                                    {
                                        Bounds = unityRegions[index].Bounds
                                    })
                                .ToArray();
                    }
                }
                else
                {
                    _lastUnityTextKey = string.Empty;
                    _lastUnityTranslations =
                        Array.Empty<TranslatedRegion>();
                }

                await _overlay.Dispatcher.InvokeAsync(() =>
                    _overlay.Render(
                        _lastUnityTranslations,
                        frame.ScreenBounds,
                        frame.DpiScale));

                StatusChanged?.Invoke(
                    $"Running · {_capture.BackendName} · Unity Adapter {unityRegions.Count}/{unitySnapshot.Data.Regions.Count} selected text region(s)");

                await DelayRemaining(
                    loopStart,
                    frameInterval,
                    cancellationToken);
                continue;
            }

            var changed = _changeDetector.HasSignificantChange(frame.Bitmap);
            if (changed)
                forcedOcrFrames = Math.Max(1, _settings.StabilityFrames);

            if (changed || forcedOcrFrames > 0)
            {
                if (forcedOcrFrames > 0)
                    forcedOcrFrames--;

                var regions = _ocr.Recognize(frame.Bitmap);
                var stableRegions = _stabilizer.Push(regions);

                if (stableRegions is not null)
                {
                    var translated = await _translator.TranslateAsync(
                        stableRegions,
                        _sourceLanguage,
                        _targetLanguage,
                        cancellationToken);

                    await _overlay.Dispatcher.InvokeAsync(() =>
                        _overlay.Render(
                            translated,
                            frame.ScreenBounds,
                            frame.DpiScale));

                    var fallbackNote =
                        _capture.FallbackReason is null
                            ? string.Empty
                            : " · WGC unavailable, using GDI";

                    var adapterNote = useUnityAdapter
                        ? " · Unity Adapter waiting, OCR fallback"
                        : string.Empty;

                    StatusChanged?.Invoke(
                        $"Running · {_capture.BackendName}{fallbackNote}{adapterNote} · OCR {stableRegions.Count} line(s) · overlay {translated.Count} line(s)");
                }
                else
                {
                    var adapterNote = useUnityAdapter
                        ? " · Unity Adapter waiting, OCR fallback"
                        : string.Empty;

                    StatusChanged?.Invoke(
                        $"Running · {_capture.BackendName}{adapterNote} · stabilizing OCR ({regions.Count} line(s))");
                }
            }

            await DelayRemaining(loopStart, frameInterval, cancellationToken);
            }
        }
        finally
        {
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

    public void Dispose() => _capture.Dispose();

    private static async Task DelayRemaining(
        long loopStart,
        TimeSpan frameInterval,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.GetElapsedTime(loopStart);
        var remaining = frameInterval - elapsed;

        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken);
    }
}
