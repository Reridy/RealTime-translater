using RealTimeTranslater.Core.Translation;
using Xunit;

namespace RealTimeTranslater.Core.Tests;

public sealed class TranslationQualityGuardTests
{
    [Theory]
    [InlineData("en", "ko", "안녕하세요.")]
    [InlineData("ja", "en", "Hello there.")]
    [InlineData("en", "ja", "こんにちは。")]
    [InlineData("en", "zh-CN", "你好。")]
    [InlineData("en", "zh-TW", "您好。")]
    [InlineData("en", "ru", "Здравствуйте.")]
    [InlineData("en", "th", "สวัสดี")]
    [InlineData("en", "fr", "Bonjour.")]
    [InlineData("en", "nl", "Hallo.")]
    public void AcceptsExpectedTargetScript(
        string sourceLanguage,
        string targetLanguage,
        string candidate)
    {
        Assert.True(
            TranslationQualityGuard.IsAcceptable(
                "A short greeting.",
                candidate,
                sourceLanguage,
                targetLanguage));
    }

    [Theory]
    [InlineData("ja", "en", "こんにちは。")]
    [InlineData("zh", "ko", "你好。")]
    [InlineData("en", "ja", "Hello there.")]
    [InlineData("ru", "en", "Здравствуйте.")]
    public void RejectsObviousWrongTargetScript(
        string sourceLanguage,
        string targetLanguage,
        string candidate)
    {
        Assert.False(
            TranslationQualityGuard.IsAcceptable(
                "A short source sentence.",
                candidate,
                sourceLanguage,
                targetLanguage));
    }

    [Fact]
    public void DistinguishesExplicitChineseVariants()
    {
        Assert.False(
            TranslationQualityGuard.IsSameLanguage(
                "zh",
                "zh-CN"));

        Assert.False(
            TranslationQualityGuard.IsSameLanguage(
                "zh",
                "zh-TW"));

        Assert.True(
            TranslationQualityGuard.IsSameLanguage(
                "zh-CN",
                "zh-CN"));

        Assert.False(
            TranslationQualityGuard.IsSameLanguage(
                "zh-CN",
                "zh-TW"));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("ko", "ko")]
    [InlineData("ja", "ja")]
    [InlineData("fr-FR", "fr")]
    public void RecognizesSameLanguage(
        string sourceLanguage,
        string targetLanguage)
    {
        Assert.True(
            TranslationQualityGuard.IsSameLanguage(
                sourceLanguage,
                targetLanguage));
    }
}
