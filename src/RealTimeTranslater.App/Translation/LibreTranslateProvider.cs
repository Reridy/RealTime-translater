using System.Net.Http.Json;
using System.Text.Json;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App.Translation;

public sealed class LibreTranslateProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;

    public LibreTranslateProvider(HttpClient httpClient, string endpoint)
    {
        _httpClient = httpClient;
        _endpoint = endpoint.TrimEnd('/');
    }

    public string Name => "LibreTranslate";

    public async Task<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            q = request.Text,
            source = NormalizeSourceLanguage(request.SourceLanguage),
            target = request.TargetLanguage,
            format = "text"
        };

        using var response = await _httpClient.PostAsJsonAsync(
            $"{_endpoint}/translate",
            payload,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("translatedText", out var translated))
            return translated.GetString() ?? request.Text;

        throw new InvalidOperationException(
            "LibreTranslate response did not contain translatedText.");
    }

    private static string NormalizeSourceLanguage(string language)
        => string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : language;
}
