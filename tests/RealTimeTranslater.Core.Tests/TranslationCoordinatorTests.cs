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
    public async Task ReusesCacheAcrossWhitespaceOnlyChanges()
    {
        var provider = new CountingProvider();
        var coordinator = new TranslationCoordinator(provider);

        await coordinator.TranslateAsync(
            new[]
            {
                new TextRegion(
                    "Hello   world",
                    new PixelRect(0, 0, 100, 20))
            },
            "en",
            "ko",
            CancellationToken.None);

        await coordinator.TranslateAsync(
            new[]
            {
                new TextRegion(
                    "Hello\nworld",
                    new PixelRect(0, 0, 100, 20))
            },
            "en",
            "ko",
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
    }



    [Fact]
    public async Task PreservesOverlayLayoutMetadata()
    {
        var provider = new CountingProvider();
        var coordinator = new TranslationCoordinator(provider);

        var region = new TextRegion(
            "hello",
            new PixelRect(10, 20, 100, 30),
            100f,
            LayoutBounds: new PixelRect(5, 15, 300, 70),
            ForegroundArgb: unchecked((int)0xFFFFFFFF),
            SourceLineCount: 2,
            SourceAlignment: "TopLeft");

        var result = await coordinator.TranslateAsync(
            new[] { region },
            "en",
            "ko",
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(region.LayoutBounds, result[0].LayoutBounds);
        Assert.Equal(region.ForegroundArgb, result[0].ForegroundArgb);
        Assert.Equal(region.SourceLineCount, result[0].SourceLineCount);
        Assert.Equal(region.SourceAlignment, result[0].SourceAlignment);
    }

    [Fact]
    public void PersistentCacheSurvivesNewInstance()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "RealTimeTranslater.Tests",
            Guid.NewGuid().ToString("N"),
            "translation-cache.json");

        try
        {
            var first = new TranslationCache(path);
            first.Set(
                "en",
                "ko",
                "Hello   world",
                "안녕하세요");

            var timeout = DateTime.UtcNow.AddSeconds(3);

            while (!File.Exists(path) &&
                   DateTime.UtcNow < timeout)
            {
                Thread.Sleep(50);
            }

            Assert.True(File.Exists(path));

            var second = new TranslationCache(path);

            Assert.True(
                second.TryGet(
                    "en",
                    "ko",
                    "Hello\nworld",
                    out var translated));

            Assert.Equal("안녕하세요", translated);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(directory) &&
                Directory.Exists(directory))
            {
                Directory.Delete(
                    directory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public async Task UserCorrectionOverridesCachedTranslation()
    {
        var provider = new CountingProvider();
        var coordinator = new TranslationCoordinator(provider);

        var regions = new[]
        {
            new TextRegion(
                "Hello.",
                new PixelRect(0, 0, 100, 20))
        };

        var initial = await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None);

        coordinator.StoreCorrection(
            new[] { "en", "auto" },
            "ko",
            "Hello.",
            "안녕.");

        var corrected = await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None);

        Assert.Equal(
            "translated:Hello.",
            initial[0].TranslatedText);
        Assert.Equal(
            "안녕.",
            corrected[0].TranslatedText);
        Assert.Equal(
            1,
            provider.CallCount);
    }

    [Fact]
    public async Task InvalidateForcesProviderToTranslateAgain()
    {
        var provider = new CountingProvider();
        var coordinator = new TranslationCoordinator(provider);

        var regions = new[]
        {
            new TextRegion(
                "Again.",
                new PixelRect(0, 0, 100, 20))
        };

        await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None);

        var removed =
            coordinator.Invalidate(
                new[] { "en" },
                "ko",
                new[] { "Again." });

        await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task SpeculativeTranslationDoesNotPersistOrEnterContext()
    {
        var provider = new CountingProvider();
        var coordinator = new TranslationCoordinator(
            provider,
            contextLimit: 4);

        var regions = new[]
        {
            new TextRegion(
                "I wanted to show you",
                new PixelRect(0, 0, 200, 30))
        };

        await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None,
            transient: true);

        await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None,
            transient: true);

        Assert.Equal(
            2,
            provider.CallCount);

        await coordinator.TranslateAsync(
            new[]
            {
                new TextRegion(
                    "final sentence",
                    new PixelRect(0, 0, 200, 30))
            },
            "en",
            "ko",
            CancellationToken.None);

        Assert.DoesNotContain(
            provider.LastContext,
            item => item.Contains(
                "I wanted to show you",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalTranslationAfterSpeculationIsCached()
    {
        var provider = new CountingProvider();
        var coordinator = new TranslationCoordinator(provider);

        var regions = new[]
        {
            new TextRegion(
                "We are strong.",
                new PixelRect(0, 0, 200, 30))
        };

        await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None,
            transient: true);

        await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None,
            transient: false);

        await coordinator.TranslateAsync(
            regions,
            "en",
            "ko",
            CancellationToken.None);

        Assert.Equal(
            2,
            provider.CallCount);
    }

    [Fact]
    public async Task UsesBatchProviderForMultipleCacheMisses()
    {
        var provider = new BatchCountingProvider();
        var coordinator = new TranslationCoordinator(provider);

        var result = await coordinator.TranslateAsync(
            new[]
            {
                new TextRegion(
                    "Krea: Tea?",
                    new PixelRect(0, 0, 100, 20)),
                new TextRegion(
                    "Ramune: Perfect!",
                    new PixelRect(0, 30, 100, 20))
            },
            "en",
            "ko",
            CancellationToken.None);

        Assert.Equal(1, provider.BatchCallCount);
        Assert.Equal(0, provider.SingleCallCount);
        Assert.Equal(2, result.Count);
        Assert.Equal(
            "batch:Krea: Tea?",
            result[0].TranslatedText);
        Assert.Equal(
            "batch:Ramune: Perfect!",
            result[1].TranslatedText);
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


    private sealed class BatchCountingProvider :
        IBatchTranslationProvider
    {
        public int SingleCallCount { get; private set; }
        public int BatchCallCount { get; private set; }

        public string Name => "BatchTest";

        public Task<string> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            SingleCallCount++;
            return Task.FromResult(
                $"single:{request.Text}");
        }

        public Task<IReadOnlyList<string>> TranslateBatchAsync(
            IReadOnlyList<TranslationRequest> requests,
            CancellationToken cancellationToken)
        {
            BatchCallCount++;

            return Task.FromResult<IReadOnlyList<string>>(
                requests
                    .Select(request =>
                        $"batch:{request.Text}")
                    .ToArray());
        }
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
