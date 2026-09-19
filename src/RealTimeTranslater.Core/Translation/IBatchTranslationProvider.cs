namespace RealTimeTranslater.Core.Translation;

public interface IBatchTranslationProvider : ITranslationProvider
{
    Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken cancellationToken);
}
