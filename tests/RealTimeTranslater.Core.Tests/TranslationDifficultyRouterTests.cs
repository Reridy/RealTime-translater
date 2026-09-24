using RealTimeTranslater.Core.Translation;
using Xunit;

namespace RealTimeTranslater.Core.Tests;

public sealed class TranslationDifficultyRouterTests
{
    [Theory]
    [InlineData("Hello.")]
    [InlineData("Are you okay?")]
    [InlineData("N-no, wait!")]
    [InlineData("Save complete")]
    public void ShortSimpleTextUsesFastRoute(
        string text)
    {
        Assert.Equal(
            TranslationRoute.Fast,
            TranslationDifficultyRouter.Classify(
                text));
    }

    [Fact]
    public void LongMultiSentenceTextUsesQualityRoute()
    {
        var text =
            "I wanted to explain what happened before we arrived. " +
            "There were several reasons, and none of them were simple. " +
            "If we ignore the earlier promise, we will misunderstand why she left.";

        Assert.Equal(
            TranslationRoute.Quality,
            TranslationDifficultyRouter.Classify(
                text,
                new[]
                {
                    "Earlier line => 이전 대사"
                }));
    }

    [Fact]
    public void MediumDialogueUsesStandardRoute()
    {
        var text =
            "One is, I wanted to show you that we are strong and do not need your protection";

        Assert.Equal(
            TranslationRoute.Standard,
            TranslationDifficultyRouter.Classify(
                text));
    }
}
