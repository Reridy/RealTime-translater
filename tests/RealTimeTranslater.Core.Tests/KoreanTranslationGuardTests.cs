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
    [InlineData(
        "W-Well... I don't really feel like I'm a noble.",
        "와-와... 사실은 나는 왕족이라는 느낌이 Really... 별로 없어.")]
    [InlineData(
        "How is it? Have you arrived?",
        "상태는怎么样? 도착하셨나요?")]
    [InlineData(
        "This is troubling.",
        "이건 곤란하네요 ???")]
    [InlineData(
        "I see, amnesia can also have this kind of problem.",
        "I see, amnesia can also have this kind of problem.")]
    [InlineData(
        "This is a long paragraph explaining several details about the character, her habits, her history, and what happened during the event in a way that should not be cut off halfway.",
        "이 문단은 중간에서 잘렸어요.")]
    [InlineData(
        "Press the button.",
        "ABC를 누르세요.")]
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
    public void NormalizeExtractsJsonTranslationWrapper()
    {
        var normalized =
            KoreanTranslationGuard.Normalize(
                "{\"translation\":\"걱정하지 마세요.\"}");

        Assert.Equal(
            "걱정하지 마세요.",
            normalized);
    }

    [Fact]
    public void RecoveryKeepsAllowedProperNames()
    {
        const string source =
            "No matter what anyone says, you are Lucrezia.";

        var recovered =
            KoreanTranslationGuard.RecoverBestKoreanLine(
                "설명:\n누가 뭐라고 해도 당신은 LUCREZIA예요.");

        Assert.True(
            KoreanTranslationGuard.IsAcceptable(
                source,
                recovered));
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
