using System.Drawing;
using System.Drawing.Imaging;
using RealTimeTranslater.Core.Models;
using Tesseract;

namespace RealTimeTranslater.App.Ocr;

public sealed class TesseractOcrService : IDisposable
{
    private readonly TesseractEngine _engine;
    private readonly float _minimumConfidence;

    public TesseractOcrService(
        string dataPath,
        string language,
        float minimumConfidence)
    {
        var resolvedDataPath = ResolveDataPath(dataPath);
        if (!Directory.Exists(resolvedDataPath))
        {
            throw new DirectoryNotFoundException(
                $"Tesseract data directory was not found: {resolvedDataPath}. " +
                "Run scripts/download-tessdata.ps1 first.");
        }

        _minimumConfidence = minimumConfidence;
        _engine = new TesseractEngine(
            resolvedDataPath,
            language,
            EngineMode.Default);
    }

    public IReadOnlyList<TextRegion> Recognize(Bitmap frame)
    {
        using var stream = new MemoryStream();
        frame.Save(stream, ImageFormat.Png);

        using var pix = Pix.LoadFromMemory(stream.ToArray());
        using var page = _engine.Process(pix, PageSegMode.Auto);
        using var iterator = page.GetIterator();

        var regions = new List<TextRegion>();
        iterator.Begin();

        do
        {
            var text = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var confidence = iterator.GetConfidence(PageIteratorLevel.TextLine);
            if (confidence < _minimumConfidence)
                continue;

            if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var box))
                continue;

            if (box.Width <= 1 || box.Height <= 1)
                continue;

            regions.Add(new TextRegion(
                text,
                new PixelRect(box.X1, box.Y1, box.Width, box.Height),
                confidence));
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return regions
            .OrderBy(x => x.Bounds.Y)
            .ThenBy(x => x.Bounds.X)
            .ToArray();
    }

    public void Dispose() => _engine.Dispose();

    private static string ResolveDataPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
            return configuredPath;

        var fromWorkingDirectory = Path.GetFullPath(
            configuredPath,
            Directory.GetCurrentDirectory());

        if (Directory.Exists(fromWorkingDirectory))
            return fromWorkingDirectory;

        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, configuredPath));
    }
}
