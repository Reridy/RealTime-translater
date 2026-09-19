using System.Net;
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
            throw new ArgumentException(
                "Ollama model name is required.",
                nameof(model));
    }

    public string Name => "Ollama";

    public async Task<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var sourceLanguage = DetectSourceLanguage(
            request.Text,
            request.SourceLanguage);

        if (IsTranslateGemmaModel(_model))
        {
            var dedicated = await RequestTranslateGemmaAsync(
                request.Text,
                sourceLanguage,
                request.TargetLanguage,
                cancellationToken);

            dedicated = KoreanTranslationGuard.Normalize(dedicated);

            if (KoreanTranslationGuard.IsAcceptable(
                    request.Text,
                    dedicated))
            {
                return dedicated;
            }

            throw new InvalidOperationException(
                "TranslateGemma returned a malformed Korean translation.");
        }

        var speakerContext = BuildSpeakerContext(request.Context);

        string? primary = null;
        Exception? primaryError = null;

        try
        {
            primary = await RequestGeneralModelAsync(
                request.Text,
                sourceLanguage,
                request.TargetLanguage,
                speakerContext,
                structuredOutput: true,
                strict: false,
                cancellationToken);
        }
        catch (Exception ex)
            when (ex is HttpRequestException or InvalidOperationException)
        {
            primaryError = ex;
        }

        if (!string.IsNullOrWhiteSpace(primary))
        {
            primary = KoreanTranslationGuard.Normalize(primary);

            if (KoreanTranslationGuard.IsAcceptable(
                    request.Text,
                    primary))
            {
                return primary;
            }
        }

        // Structured decoding can fail on some local Ollama/model
        // combinations. Retry once with a minimal plain-text request and no
        // dialogue history so a bad generation cannot poison the next line.
        if (primaryError is HttpRequestException httpError &&
            IsTransient(httpError.StatusCode))
        {
            await Task.Delay(250, cancellationToken);
        }

        string? strict = null;
        Exception? strictError = null;

        try
        {
            strict = await RequestGeneralModelAsync(
                request.Text,
                sourceLanguage,
                request.TargetLanguage,
                speakerContext: string.Empty,
                structuredOutput: false,
                strict: true,
                cancellationToken);
        }
        catch (Exception ex)
            when (ex is HttpRequestException or InvalidOperationException)
        {
            strictError = ex;
        }

        if (!string.IsNullOrWhiteSpace(strict))
        {
            strict = KoreanTranslationGuard.Normalize(strict);

            if (KoreanTranslationGuard.IsAcceptable(
                    request.Text,
                    strict))
            {
                return strict;
            }

            var recovered =
                KoreanTranslationGuard.RecoverBestKoreanLine(strict);

            if (!string.IsNullOrWhiteSpace(recovered) &&
                KoreanTranslationGuard.IsAcceptable(
                    request.Text,
                    recovered))
            {
                return recovered;
            }
        }

        var error =
            strictError?.Message ??
            primaryError?.Message ??
            "The model returned malformed translation output.";

        throw new InvalidOperationException(
            $"Ollama translation failed after validation/retry: {error}",
            strictError ?? primaryError);
    }

    private async Task<string> RequestTranslateGemmaAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var sourceName = LanguageName(sourceLanguage);
        var targetName = LanguageName(targetLanguage);

        var prompt =
            $"You are a professional {sourceName} ({sourceLanguage}) to " +
            $"{targetName} ({targetLanguage}) translator. " +
            $"Your goal is to accurately convey the meaning and nuances of " +
            $"the original {sourceName} text while adhering to " +
            $"{targetName} grammar, vocabulary, and cultural sensitivities.\n" +
            $"Produce only the {targetName} translation, without any " +
            $"additional explanations or commentary. Please translate the " +
            $"following {sourceName} text into {targetName}:\n\n\n" +
            sourceText;

        var payload = new
        {
            model = _model,
            stream = false,
            keep_alive = "30m",
            options = new
            {
                temperature = 0.0,
                num_ctx = 1024,
                num_predict = OutputBudget(sourceText, 144),
                repeat_penalty = 1.08
            },
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = prompt
                }
            }
        };

        return await SendChatAsync(
            payload,
            cancellationToken);
    }

    private async Task<string> RequestGeneralModelAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        string speakerContext,
        bool structuredOutput,
        bool strict,
        CancellationToken cancellationToken)
    {
        var systemPrompt =
            "You are a Korean game localization translator. " +
            "Translate ONLY the SOURCE text into natural Korean. " +
            "Preserve meaning exactly: negation, subject/object relations, " +
            "chronology, emotion, hesitation, emphasis, jokes, and tone. " +
            "Never invent, omit, continue, explain, or answer the dialogue. " +
            "For stutters, preserve hesitation: for example 'N-no' means " +
            "'아-아니요', not an apology. " +
            "Output Korean only except unavoidable proper names or short " +
            "game abbreviations already present in SOURCE.";

        if (structuredOutput)
        {
            systemPrompt +=
                " Return exactly one JSON object with one string field named " +
                "translation and nothing else.";
        }
        else
        {
            systemPrompt +=
                " Return only the Korean translation and stop immediately.";
        }

        if (strict)
        {
            systemPrompt +=
                " This is a correction retry. Ignore all prior conversation " +
                "and do not include any English or Chinese phrase.";
        }

        var userText =
            $"Source language: {sourceLanguage}\n" +
            $"Target language: {targetLanguage}\n";

        if (!string.IsNullOrWhiteSpace(speakerContext))
        {
            userText +=
                $"Speaker context: {speakerContext}\n";
        }

        userText +=
            "SOURCE BEGIN\n" +
            sourceText +
            "\nSOURCE END";

        object payload;

        if (structuredOutput)
        {
            payload = new
            {
                model = _model,
                stream = false,
                keep_alive = "30m",
                format = new
                {
                    type = "object",
                    properties = new
                    {
                        translation = new
                        {
                            type = "string"
                        }
                    },
                    required = new[] { "translation" },
                    additionalProperties = false
                },
                options = new
                {
                    temperature = 0.0,
                    top_p = 0.85,
                    num_ctx = 1024,
                    num_predict = OutputBudget(sourceText, 144),
                    repeat_penalty = 1.10
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
                        content = userText
                    }
                }
            };
        }
        else
        {
            payload = new
            {
                model = _model,
                stream = false,
                keep_alive = "30m",
                options = new
                {
                    temperature = 0.0,
                    top_p = 0.8,
                    num_ctx = 768,
                    num_predict = OutputBudget(sourceText, 112),
                    repeat_penalty = 1.14
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
                        content = userText
                    }
                }
            };
        }

        var raw = await SendChatAsync(
            payload,
            cancellationToken);

        if (!structuredOutput)
            return raw;

        try
        {
            using var json = JsonDocument.Parse(raw);

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

        throw new InvalidOperationException(
            "Ollama structured response did not contain translation.");
    }

    private async Task<string> SendChatAsync(
        object payload,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"{_endpoint}/api/chat",
            payload,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = TryReadOllamaError(body);

            throw new HttpRequestException(
                $"Ollama {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}: {detail}",
                inner: null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty(
                "message",
                out var message) &&
            message.TryGetProperty(
                "content",
                out var content))
        {
            var value = content.GetString()?.Trim();

            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        if (document.RootElement.TryGetProperty(
                "error",
                out var error))
        {
            throw new InvalidOperationException(
                $"Ollama generation error: {error.GetString()}");
        }

        throw new InvalidOperationException(
            "Ollama response did not contain message.content.");
    }

    private static string TryReadOllamaError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);

            if (json.RootElement.TryGetProperty(
                    "error",
                    out var error))
            {
                return error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? "unknown error"
                    : error.ToString();
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body)
            ? "unknown server error"
            : body.Length <= 240
                ? body
                : body[..240];
    }

    private static string BuildSpeakerContext(
        IReadOnlyList<string> context)
    {
        return context
            .Reverse()
            .Select(line => line.Trim())
            .FirstOrDefault(line =>
                line.StartsWith(
                    "Current speaker:",
                    StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
    }

    private static string DetectSourceLanguage(
        string text,
        string configured)
    {
        if (!string.Equals(
                configured,
                "auto",
                StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        if (text.Any(ch =>
                ch is >= '\u3040' and <= '\u30FF'))
        {
            return "ja";
        }

        if (text.Any(ch =>
                ch is >= 'A' and <= 'Z' ||
                ch is >= 'a' and <= 'z'))
        {
            return "en";
        }

        return "auto";
    }

    private static string LanguageName(string code)
        => code.ToLowerInvariant() switch
        {
            "en" => "English",
            "ja" => "Japanese",
            "ko" => "Korean",
            _ => "source language"
        };

    private static int OutputBudget(
        string sourceText,
        int maximum)
        => Math.Clamp(
            sourceText.Length + 28,
            48,
            maximum);

    private static bool IsTranslateGemmaModel(
        string model)
        => model.StartsWith(
            "translategemma",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(
        HttpStatusCode? status)
        => status is
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout or
            (HttpStatusCode)429;
}
