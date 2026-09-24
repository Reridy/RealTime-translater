using Xunit;
using RealTimeTranslater.Core.Models;
using RealTimeTranslater.Core.Ocr;

namespace RealTimeTranslater.Core.Tests;

public sealed class FrameTextStabilizerTests
{
    [Fact]
    public void ReturnsNullUntilRequiredFrameCountIsReached()
    {
        var stabilizer = new FrameTextStabilizer(requiredFrames: 2);
        var frame = new[]
        {
            new TextRegion(
                "こんにちは",
                new PixelRect(10, 20, 120, 28))
        };

        Assert.Null(stabilizer.Push(frame));

        var stable = stabilizer.Push(frame);

        Assert.NotNull(stable);
        Assert.Single(stable!);
        Assert.Equal("こんにちは", stable![0].Text);
    }

    [Fact]
    public void ChangedTextRestartsStabilization()
    {
        var stabilizer = new FrameTextStabilizer(requiredFrames: 2);

        var first = new[]
        {
            new TextRegion("A", new PixelRect(0, 0, 10, 10))
        };
        var second = new[]
        {
            new TextRegion("B", new PixelRect(0, 0, 10, 10))
        };

        Assert.Null(stabilizer.Push(first));
        Assert.NotNull(stabilizer.Push(first));

        Assert.Null(stabilizer.Push(second));
        Assert.NotNull(stabilizer.Push(second));
    }

    [Fact]
    public void WhitespaceNoiseDoesNotBreakStability()
    {
        var stabilizer = new FrameTextStabilizer(requiredFrames: 2);

        var first = new[]
        {
            new TextRegion("hello   world", new PixelRect(0, 0, 10, 10))
        };
        var second = new[]
        {
            new TextRegion(" hello world ", new PixelRect(2, 1, 10, 10))
        };

        Assert.Null(stabilizer.Push(first));
        Assert.NotNull(stabilizer.Push(second));
    }
}
