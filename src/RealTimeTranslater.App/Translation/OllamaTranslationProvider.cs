using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App.Translation;

public sealed class OllamaTranslationProvider : ITranslationProvider
{
    private const int MaximumContextLines = 2;
    private const int MaximumContextCharacters = 900;

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
            throw new ArgumentException(
                "Ollama model name is required.",
                nameof(model));
    }

    public string Name => "Ollama";

    public async Task<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var context = BuildContext(request.Context);
        var outputBudget = Math.Clamp(
            request.Text.Length * 3 + 48,
            96,
            256);

        var payload = new
        {
            model = _model,
            stream = false,
            keep_alive = "30m",
            options = new
            {
                temperature = 0.15,
                top_p = 0.9,
                num_ctx = 1536,
                num_predict = outputBudget,
                repeat_penalty = 1.05
            },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "You are a professional Korean game localizer. " +
                        "Translate only the supplied source text into natural Korean. " +
                        "For dialogue, prefer fluent spoken Korean over literal word order and preserve the character's emotion, hesitation, emphasis, jokes, and tone. " +
                        "For short UI text, use concise standard Korean. " +
                        "Preserve proper names consistently, numbers, placeholders, control tokens, and meaningful line breaks. " +
                        "Do not add speaker names, notes, explanations, quotation marks, or multiple alternatives. " +
                        "Return only the final Korean translation."
                },
                new
                {
                    role = "user",
                    content =
                        $"Source language: {request.SourceLanguage}\n" +
                        $"Target language: {request.TargetLanguage}\n" +
                        $"Recent localization context (reference only; do not repeat it):\n{context}\n\n" +
                        "Translate this text:\n" +
                        request.Text
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
            var translated = content.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(translated))
                return translated;
        }

        throw new InvalidOperationException(
            "Ollama response did not contain message.content.");
    }

    private static string BuildContext(IReadOnlyList<string> context)
    {
        if (context.Count == 0)
            return "(none)";

        var lines = context
            .TakeLast(MaximumContextLines)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        if (lines.Length == 0)
            return "(none)";

        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            if (builder.Length > 0)
                builder.Append('\n');

            var remaining =
                MaximumContextCharacters - builder.Length;
            if (remaining <= 0)
                break;

            builder.Append(
                line.Length <= remaining
                    ? line
                    : line[..remaining]);
        }

        return builder.Length == 0
            ? "(none)"
            : builder.ToString();
    }
}
