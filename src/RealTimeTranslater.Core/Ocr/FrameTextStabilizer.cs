using System.Text;
using RealTimeTranslater.Core.Models;

namespace RealTimeTranslater.Core.Ocr;

public sealed class FrameTextStabilizer
{
    private readonly int _requiredFrames;
    private string? _lastKey;
    private int _sameFrameCount;

    public FrameTextStabilizer(int requiredFrames = 2)
    {
        if (requiredFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredFrames));

        _requiredFrames = requiredFrames;
    }

    public IReadOnlyList<TextRegion>? Push(IReadOnlyList<TextRegion> regions)
    {
        var key = BuildKey(regions);

        if (string.Equals(key, _lastKey, StringComparison.Ordinal))
        {
            _sameFrameCount++;
        }
        else
        {
            _lastKey = key;
            _sameFrameCount = 1;
        }

        return _sameFrameCount >= _requiredFrames ? regions : null;
    }

    public void Reset()
    {
        _lastKey = null;
        _sameFrameCount = 0;
    }

    private static string BuildKey(IReadOnlyList<TextRegion> regions)
    {
        var sb = new StringBuilder();
        foreach (var region in regions
                     .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                     .OrderBy(x => x.Bounds.Y)
                     .ThenBy(x => x.Bounds.X))
        {
            var normalized = string.Join(
                ' ',
                region.Text.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            sb.Append(normalized).Append('\n');
        }

        return sb.ToString();
    }
}
