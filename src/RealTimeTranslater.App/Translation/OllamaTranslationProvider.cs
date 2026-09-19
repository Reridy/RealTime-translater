using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App.Translation;

public sealed class OllamaTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;

    public OllamaTranslationProvider(
        HttpClient httpClient,
        string endpoint,
        string model)
    {
        _httpClient = httpClient;
        _endpoint = endpoint.TrimEnd('/');
        _model = model.Trim();

        if (_model.Length == 0)
            throw new ArgumentException("Ollama model name is required.", nameof(model));
    }

    public string Name => "Ollama";

    public async Task<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var context = request.Context.Count == 0
            ? "(no previous dialogue)"
            : string.Join("\n", request.Context);

        var payload = new
        {
            model = _model,
            stream = false,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "You are a game localization translator. " +
                        "Translate the input naturally into Korean. " +
                        "Preserve names, UI tokens, numbers, punctuation intent, and character tone. " +
                        "Return only the translated Korean text, with no explanation."
                },
                new
                {
                    role = "user",
                    content =
                        $"Source language: {request.SourceLanguage}\n" +
                        $"Target language: {request.TargetLanguage}\n" +
                        $"Recent dialogue:\n{context}\n\n" +
                        $"Text to translate:\n{request.Text}"
                }
            }
        };

        using var response = await _httpClient.PostAsJsonAsync(
            $"{_endpoint}/api/chat",
            payload,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content))
        {
            return content.GetString() ?? request.Text;
        }

        throw new InvalidOperationException(
            "Ollama response did not contain message.content.");
    }
}
