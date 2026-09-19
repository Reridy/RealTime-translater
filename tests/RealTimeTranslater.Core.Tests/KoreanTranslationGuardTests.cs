using RealTimeTranslater.Core.Translation;
using Xunit;

namespace RealTimeTranslater.Core.Tests;

public sealed class KoreanTranslationGuardTests
{
    [Theory]
    [InlineData(
        "That's not the issue. Your memory is very good.",
        "그게 문제는 아니에요. 기억력이 아주 좋아요.")]
    [InlineData(
        "No matter what anyone says, you are Luna.",
        "누가 뭐라고 해도 당신은 Luna예요.")]
    [InlineData(
        "Press AP.",
        "AP를 누르세요.")]
    public void AcceptsCleanKorean(
        string source,
        string candidate)
    {
        Assert.True(
            KoreanTranslationGuard.IsAcceptable(
                source,
                candidate));
    }

    [Theory]
    [InlineData(
        "Your memory is actually very good.",
        "기억력이 아주 좋아요 actually very good")]
    [InlineData(
        "How is it?",
        "상태는怎么样?")]
    [InlineData(
        "Of course, it feels strange.",
        "물론 이상한 기분이에요 ???")]
    [InlineData(
        "Thank you.",
        "user\n감사합니다.")]
    [InlineData(
        "This is normal.",
        "正常使用状态正常使用状态正常使用状态正常使用状态")]
    public void RejectsLeakedOrMalformedOutput(
        string source,
        string candidate)
    {
        Assert.False(
            KoreanTranslationGuard.IsAcceptable(
                source,
                candidate));
    }

    [Fact]
    public void NormalizeRemovesCommonWrapperNoise()
    {
        var normalized =
            KoreanTranslationGuard.Normalize(
                "Translation: 감사합니다.");

        Assert.Equal("감사합니다.", normalized);
    }
}
