using RealTimeTranslater.Core.Translation;
using Xunit;

namespace RealTimeTranslater.Core.Tests;

public sealed class SpeculativeTranslationPolicyTests
{
    [Fact]
    public void CompleteSentenceTranslatesImmediately()
    {
        var decision =
            SpeculativeTranslationPolicy.Evaluate(
                string.Empty,
                "I wanted to show you.",
                TimeSpan.Zero,
                markedPartial: false);

        Assert.True(decision.ShouldTranslate);
        Assert.True(decision.IsFinal);
    }

    [Fact]
    public void PartialTextWaitsForDebounce()
    {
        var decision =
            SpeculativeTranslationPolicy.Evaluate(
                string.Empty,
                "I wanted to show you",
                TimeSpan.FromMilliseconds(40),
                markedPartial: true);

        Assert.False(decision.ShouldTranslate);
    }

    [Fact]
    public void MeaningfulPrefixGrowthRetranslates()
    {
        var decision =
            SpeculativeTranslationPolicy.Evaluate(
                "I wanted to show you",
                "I wanted to show you that we are strong",
                TimeSpan.FromMilliseconds(100),
                markedPartial: true);

        Assert.True(decision.ShouldTranslate);
        Assert.False(decision.IsFinal);
    }

    [Fact]
    public void TinyPrefixGrowthDoesNotSpamModel()
    {
        var decision =
            SpeculativeTranslationPolicy.Evaluate(
                "I wanted to show you",
                "I wanted to show you t",
                TimeSpan.FromMilliseconds(120),
                markedPartial: true);

        Assert.False(decision.ShouldTranslate);
    }

    [Fact]
    public void ReplacedTextEventuallyTranslates()
    {
        var decision =
            SpeculativeTranslationPolicy.Evaluate(
                "Old subtitle fragment",
                "A totally different subtitle",
                TimeSpan.FromMilliseconds(170),
                markedPartial: true);

        Assert.True(decision.ShouldTranslate);
    }
}
