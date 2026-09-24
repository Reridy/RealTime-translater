namespace RealTimeTranslater.Core.Translation;

public interface ITranslationProvider
{
    string Name { get; }

    Task<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}
