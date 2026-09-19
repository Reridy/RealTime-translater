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
        return Task.FromResult($"[KO] {request.Text}");
    }
}
