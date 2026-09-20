using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App.Translation;

public sealed class MockTranslationProvider : ITranslationProvider
{
    public string Name => "Mock";

    public Task<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target =
            TranslationQualityGuard
                .NormalizeLanguageCode(
                    request.TargetLanguage)
                .ToUpperInvariant();

        return Task.FromResult(
            $"[{target}] {request.Text}");
    }
}
