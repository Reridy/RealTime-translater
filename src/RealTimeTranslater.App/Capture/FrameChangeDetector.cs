using System.Drawing;

namespace RealTimeTranslater.App.Capture;

public sealed class FrameChangeDetector
{
    private const int SampleWidth = 32;
    private const int SampleHeight = 18;
    private readonly double _threshold;
    private byte[]? _previous;

    public FrameChangeDetector(double threshold)
    {
        _threshold = Math.Clamp(threshold, 0.001, 1.0);
    }

    public bool HasSignificantChange(Bitmap frame)
    {
        var current = Sample(frame);

        if (_previous is null)
        {
            _previous = current;
            return true;
        }

        long absoluteDifference = 0;
        for (var i = 0; i < current.Length; i++)
            absoluteDifference += Math.Abs(current[i] - _previous[i]);

        _previous = current;
        var normalized = absoluteDifference / (255.0 * current.Length);
        return normalized >= _threshold;
    }

    private static byte[] Sample(Bitmap frame)
    {
        using var small = new Bitmap(SampleWidth, SampleHeight);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.DrawImage(
                frame,
                new Rectangle(0, 0, SampleWidth, SampleHeight),
                new Rectangle(0, 0, frame.Width, frame.Height),
                GraphicsUnit.Pixel);
        }

        var result = new byte[SampleWidth * SampleHeight];
        var index = 0;

        for (var y = 0; y < SampleHeight; y++)
        {
            for (var x = 0; x < SampleWidth; x++)
            {
                var color = small.GetPixel(x, y);
                result[index++] = (byte)((color.R * 30 + color.G * 59 + color.B * 11) / 100);
            }
        }

        return result;
    }
}
