using Xunit;
using RealTimeTranslater.Core.Models;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.Core.Tests;

public sealed class TranslationCoordinatorTests
{
    [Fact]
    public async Task UsesCacheForRepeatedText()
    {
        var provider = new CountingProvider();
        var coordinator = new TranslationCoordinator(provider);

        var regions = new[]
        {
            new TextRegion("hello", new PixelRect(0, 0, 100, 20))
        };

        var first = await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None);

        var second = await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal("translated:hello", first[0].TranslatedText);
        Assert.Equal("translated:hello", second[0].TranslatedText);
    }

    [Fact]
    public async Task PassesRecentDialogueAsContext()
    {
        var provider = new CountingProvider();
        var coordinator = new TranslationCoordinator(
            provider,
            contextLimit: 2);

        await coordinator.TranslateAsync(
            new[]
            {
                new TextRegion("first", new PixelRect(0, 0, 100, 20))
            },
            "en",
            "ko",
            CancellationToken.None);

        await coordinator.TranslateAsync(
            new[]
            {
                new TextRegion("second", new PixelRect(0, 30, 100, 20))
            },
            "en",
            "ko",
            CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
        Assert.NotEmpty(provider.LastContext);
        Assert.Contains(
            provider.LastContext,
            x => x.Contains("first", StringComparison.Ordinal));
    }

    private sealed class CountingProvider : ITranslationProvider
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<string> LastContext { get; private set; } =
            Array.Empty<string>();

        public string Name => "Test";

        public Task<string> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastContext = request.Context.ToArray();
            return Task.FromResult($"translated:{request.Text}");
        }
    }
}
