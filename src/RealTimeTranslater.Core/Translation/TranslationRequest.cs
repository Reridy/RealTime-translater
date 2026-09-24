namespace RealTimeTranslater.Core.Translation;

public sealed record TranslationRequest(
    string Text,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<string> Context,
    TranslationRoute Route = TranslationRoute.Standard);
