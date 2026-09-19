using System.Diagnostics;
using RealTimeTranslater.App.Capture;
using RealTimeTranslater.App.Configuration;
using RealTimeTranslater.App.Ocr;
using RealTimeTranslater.App.Overlay;
using RealTimeTranslater.Core.Ocr;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App.Pipeline;

public sealed class TranslationPipeline
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

    public TranslationPipeline(
        IntPtr targetWindow,
        TesseractOcrService ocr,
        ITranslationProvider translationProvider,
        OverlayWindow overlay,
        AppSettings settings,
        string sourceLanguage,
        string targetLanguage)
    {
        _targetWindow = targetWindow;
        _ocr = ocr;
        _overlay = overlay;
        _settings = settings;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;

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

        StatusChanged?.Invoke("Running");

        while (!cancellationToken.IsCancellationRequested)
        {
            var loopStart = Stopwatch.GetTimestamp();

            using var frame = _capture.Capture(_targetWindow);
            if (frame is null)
            {
                StatusChanged?.Invoke("Target window is minimized, hidden, or unavailable.");
                await DelayRemaining(loopStart, frameInterval, cancellationToken);
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

                    StatusChanged?.Invoke(
                        $"Running · OCR {stableRegions.Count} line(s) · overlay {translated.Count} line(s)");
                }
                else
                {
                    StatusChanged?.Invoke(
                        $"Running · stabilizing OCR ({regions.Count} line(s))");
                }
            }

            await DelayRemaining(loopStart, frameInterval, cancellationToken);
        }
    }

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
