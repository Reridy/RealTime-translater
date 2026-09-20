using RealTimeTranslater.Core.Translation;
using Xunit;

namespace RealTimeTranslater.Core.Tests;

public sealed class TranslationContextGuardTests
{
    private static readonly string[] Context =
    {
        "Why did you go that far? => 대체 왜 그렇게까지 갔을까?",
        "There are two reasons. => 그 이유는 두 가지로 나눌 수 있어.",
        "Glossary: Lucrezia => 루크레치아",
        "Current speaker: Claire"
    };

    [Fact]
    public void BuildsContextFromTargetSideOnly()
    {
        var context =
            TranslationContextGuard
                .BuildRecentTargetContext(
                    Context,
                    "One is, I wanted to show you.");

        Assert.Contains(
            "대체 왜 그렇게까지 갔을까?",
            context);

        Assert.DoesNotContain(
            "Why did you go that far?",
            context);

        Assert.DoesNotContain(
            "=>",
            context);

        Assert.DoesNotContain(
            "Glossary:",
            context);
    }

    [Fact]
    public void RejectsArrowStyleMemoryLeak()
    {
        var candidate =
            "왜 그렇게까지 했어? => 대체 왜 그렇게까지 갔을까? " +
            "하나는, 너에게 보여주고 싶었어. 우리가 강하다는 것을.";

        Assert.True(
            TranslationContextGuard
                .ContainsContextLeak(
                    candidate,
                    Context,
                    "One is, I wanted to show you."));
    }

    [Fact]
    public void RejectsCopiedPreviousTargetSentence()
    {
        var candidate =
            "그 이유는 두 가지로 나눌 수 있어. " +
            "하나는, 너에게 보여주고 싶었어. 우리가 강하다는 것을.";

        Assert.True(
            TranslationContextGuard
                .ContainsContextLeak(
                    candidate,
                    Context,
                    "One is, I wanted to show you."));
    }

    [Fact]
    public void AcceptsCleanCurrentTranslation()
    {
        var candidate =
            "하나는, 너에게 보여주고 싶었어. 우리가 강하다는 걸. " +
            "네 일방적인 보호는 필요 없다는 것도.";

        Assert.False(
            TranslationContextGuard
                .ContainsContextLeak(
                    candidate,
                    Context,
                    "One is, I wanted to show you."));
    }

    [Fact]
    public void ExcludesCurrentSentenceFromContext()
    {
        var context =
            TranslationContextGuard
                .BuildRecentTargetContext(
                    new[]
                    {
                        "One is, I wanted to show you. => 하나는, 너에게 보여주고 싶었어.",
                        "Earlier line. => 이전 대사."
                    },
                    "One is, I wanted to show you.");

        Assert.DoesNotContain(
            "하나는, 너에게 보여주고 싶었어.",
            context);

        Assert.Contains(
            "이전 대사.",
            context);
    }
}
