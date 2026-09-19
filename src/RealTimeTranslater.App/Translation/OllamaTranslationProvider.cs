using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App.Translation;

public sealed partial class OllamaTranslationProvider : ITranslationProvider
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

        var translated = await RequestTranslationAsync(
            request,
            context,
            strictRetry: false,
            cancellationToken);

        if (!IsSuspiciousTranslation(request.Text, translated))
            return translated;

        // Retry only when the first result contains role/meta leakage,
        // non-Korean instruction text, or otherwise looks malformed.
        translated = await RequestTranslationAsync(
            request,
            "(none)",
            strictRetry: true,
            cancellationToken);

        return translated;
    }

    private async Task<string> RequestTranslationAsync(
        TranslationRequest request,
        string context,
        bool strictRetry,
        CancellationToken cancellationToken)
    {
        var outputBudget = Math.Clamp(
            request.Text.Length * 3 + 48,
            96,
            strictRetry ? 192 : 256);

        var systemPrompt =
            "You are a professional Korean game localizer. " +
            "Translate ONLY the supplied source text into natural Korean. " +
            "Preserve negation, who did what to whom, chronology, emotion, hesitation, emphasis, jokes, and character tone. " +
            "Do not invent facts or reinterpret the scene. " +
            "Use fluent spoken Korean for dialogue and concise standard Korean for UI. " +
            "Preserve proper names consistently, numbers, placeholders, control tokens, and meaningful line breaks. " +
            "Never output role labels such as user/assistant/system. " +
            "Never output Chinese instructions, explanations, notes, alternatives, or commentary. " +
            "Return exactly one JSON object with a single string field named translation.";

        if (strictRetry)
        {
            systemPrompt +=
                " This is a correction retry. Ignore all previous context and produce only the corrected Korean translation JSON.";
        }

        var payload = new
        {
            model = _model,
            stream = false,
            keep_alive = "30m",
            format = "json",
            options = new
            {
                temperature = strictRetry ? 0.05 : 0.12,
                top_p = 0.85,
                num_ctx = 1536,
                num_predict = outputBudget,
                repeat_penalty = 1.05
            },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = systemPrompt
                },
                new
                {
                    role = "user",
                    content =
                        $"Source language: {request.SourceLanguage}\n" +
                        $"Target language: {request.TargetLanguage}\n" +
                        $"Recent localization context (reference only; never repeat it):\n{context}\n\n" +
                        "SOURCE TEXT BEGIN\n" +
                        request.Text +
                        "\nSOURCE TEXT END"
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

        if (!document.RootElement.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException(
                "Ollama response did not contain message.content.");
        }

        var rawContent = content.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            throw new InvalidOperationException(
                "Ollama returned an empty translation.");
        }

        var translated = ParseStructuredTranslation(rawContent);
        if (string.IsNullOrWhiteSpace(translated))
        {
            throw new InvalidOperationException(
                "Ollama JSON response did not contain a translation.");
        }

        return translated.Trim();
    }

    private static string ParseStructuredTranslation(string rawContent)
    {
        try
        {
            using var json = JsonDocument.Parse(rawContent);

            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty(
                    "translation",
                    out var translation) &&
                translation.ValueKind == JsonValueKind.String)
            {
                return translation.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        // Some local models still wrap JSON in a code fence even when JSON
        // mode is requested. Recover only the JSON object; never surface
        // surrounding model chatter to the overlay.
        var match = JsonObjectRegex().Match(rawContent);
        if (match.Success)
        {
            try
            {
                using var json = JsonDocument.Parse(match.Value);

                if (json.RootElement.TryGetProperty(
                        "translation",
                        out var translation) &&
                    translation.ValueKind == JsonValueKind.String)
                {
                    return translation.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
            }
        }

        return string.Empty;
    }

    private static bool IsSuspiciousTranslation(
        string source,
        string translated)
    {
        if (string.IsNullOrWhiteSpace(translated))
            return true;

        if (RoleLeakRegex().IsMatch(translated) ||
            ChineseMetaRegex().IsMatch(translated))
        {
            return true;
        }

        var hangul = translated.Count(ch =>
            ch is >= '\uAC00' and <= '\uD7A3' ||
            ch is >= '\u3131' and <= '\u318E');

        // A sentence-length source should normally yield at least a little
        // Hangul. Short names/tokens are allowed to remain non-Korean.
        if (source.Length >= 18 && hangul == 0)
            return true;

        return false;
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

    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObjectRegex();

    [GeneratedRegex(
        @"(?im)^\s*(user|assistant|system)\s*:?(?:\s|$)")]
    private static partial Regex RoleLeakRegex();

    [GeneratedRegex(
        @"[请纠正翻译最后一句韩语重新输出答案解释]")]
    private static partial Regex ChineseMetaRegex();
}
