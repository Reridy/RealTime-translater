using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App.Translation;

public sealed partial class OllamaTranslationProvider : ITranslationProvider
{
    private const int MaximumContextLines = 1;
    private const int MaximumContextCharacters = 520;

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

        var first = await RequestTranslationAsync(
            request,
            context,
            strictRetry: false,
            cancellationToken);

        var normalized = NormalizeTranslation(first);

        if (!IsSuspiciousTranslation(request.Text, normalized))
            return normalized;

        // Bad local-model generations can occasionally leak role labels,
        // Chinese meta text, or repeat a phrase until the token budget ends.
        // Retry once with no history, lower temperature, and a tighter budget.
        var retry = await RequestTranslationAsync(
            request,
            "(none)",
            strictRetry: true,
            cancellationToken);

        normalized = NormalizeTranslation(retry);

        if (!IsSuspiciousTranslation(request.Text, normalized))
            return normalized;

        // Never paint runaway model garbage over the game. If even the strict
        // retry is malformed, recover the cleanest Korean sentence we can.
        var recovered = RecoverKoreanSentence(normalized);
        if (!string.IsNullOrWhiteSpace(recovered))
            return recovered;

        // Last-resort fail-safe: keep the original readable rather than
        // flooding the overlay with malformed generated text.
        return request.Text;
    }

    private async Task<string> RequestTranslationAsync(
        TranslationRequest request,
        string context,
        bool strictRetry,
        CancellationToken cancellationToken)
    {
        var outputBudget = strictRetry
            ? Math.Clamp(request.Text.Length + 40, 64, 128)
            : Math.Clamp(request.Text.Length * 2 + 36, 72, 160);

        var systemPrompt =
            "You are a Korean game localization translator. " +
            "Translate ONLY the source text into natural Korean. " +
            "Preserve the exact meaning: negation, subject/object relations, chronology, emotion, hesitation, emphasis, jokes, and character tone. " +
            "Treat stutters/hesitation literally; for example, a source like 'N-no' should remain a hesitant refusal such as '아-아니요', not become an apology. " +
            "Do not invent or omit information. " +
            "Use fluent spoken Korean for dialogue and concise standard Korean for UI. " +
            "Keep proper names consistent and preserve numbers/placeholders/control tokens. " +
            "Do not output Chinese. Do not output role labels, explanations, notes, alternatives, or commentary. " +
            "Return exactly one JSON object: {\"translation\":\"...\"}.";

        if (strictRetry)
        {
            systemPrompt +=
                " Correction retry: ignore all previous context, translate the source once, and stop immediately after the JSON object.";
        }

        var payload = new
        {
            model = _model,
            stream = false,
            keep_alive = "30m",
            format = "json",
            options = new
            {
                temperature = strictRetry ? 0.0 : 0.08,
                top_p = 0.8,
                num_ctx = 1024,
                num_predict = outputBudget,
                repeat_penalty = 1.12
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
                        $"Context (reference only, never repeat): {context}\n" +
                        "SOURCE:\n" +
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

        return translated;
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

        var match = JsonObjectRegex().Match(rawContent);
        if (!match.Success)
            return string.Empty;

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

        return string.Empty;
    }

    private static string NormalizeTranslation(string value)
    {
        var text = value
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();

        text = RoleLeakRegex().Replace(text, string.Empty);
        text = WhitespaceBeforeNewlineRegex().Replace(text, "\n");
        text = ExcessBlankLinesRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    private static bool IsSuspiciousTranslation(
        string source,
        string translated)
    {
        if (string.IsNullOrWhiteSpace(translated))
            return true;

        if (RoleLeakRegex().IsMatch(translated) ||
            ChineseMetaRegex().IsMatch(translated) ||
            HasRunawayRepetition(translated))
        {
            return true;
        }

        if (translated.Length >
            Math.Max(source.Length * 2.6, source.Length + 140))
        {
            return true;
        }

        var hangul = translated.Count(IsHangul);
        var han = translated.Count(IsHan);
        var latinWords = LatinWordRegex().Matches(translated).Count;
        var latinLetters = translated.Count(ch =>
            ch is >= 'A' and <= 'Z' ||
            ch is >= 'a' and <= 'z');

        // Sentence-length output should be primarily Korean, not a long run of
        // CJK ideographs from a degenerate multilingual generation.
        if (source.Length >= 18)
        {
            if (hangul < 4)
                return true;

            if (han >= 8 && han > hangul / 2)
                return true;

            // Korean dialogue should not suddenly trail off into English.
            // Allow one proper-name/token, but reject phrase-level leakage.
            if (latinWords >= 2 || latinLetters >= 14)
                return true;
        }

        return false;
    }

    private static bool HasRunawayRepetition(string text)
    {
        if (RepeatedPhraseRegex().IsMatch(text))
            return true;

        var compact = WhitespaceRegex().Replace(text, " ");
        if (compact.Length < 40)
            return false;

        var tokens = compact
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length < 8)
            return false;

        var mostCommon = tokens
            .GroupBy(token => token, StringComparer.Ordinal)
            .Max(group => group.Count());

        return mostCommon >= 6 &&
               mostCommon >= Math.Ceiling(tokens.Length * 0.35);
    }

    private static string RecoverKoreanSentence(string text)
    {
        var candidates = text
            .Split(
                new[] { '\n', '。', '！', '？' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length >= 2)
            .Select(line => new
            {
                Text = line,
                Hangul = line.Count(IsHangul),
                Han = line.Count(IsHan)
            })
            .Where(item =>
                item.Hangul >= 3 &&
                item.Han <= Math.Max(2, item.Hangul / 3))
            .OrderByDescending(item => item.Hangul)
            .FirstOrDefault();

        if (candidates is null)
            return string.Empty;

        var clean = LongHanRunRegex().Replace(
            candidates.Text,
            string.Empty);

        return clean.Trim();
    }

    private static bool IsHangul(char ch)
        => ch is >= '\uAC00' and <= '\uD7A3'
            or >= '\u3131' and <= '\u318E';

    private static bool IsHan(char ch)
        => ch is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF';

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
                builder.Append(' ');

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

    [GeneratedRegex(@"(.{2,12})\1{3,}")]
    private static partial Regex RepeatedPhraseRegex();

    [GeneratedRegex(@"[\u3400-\u4DBF\u4E00-\u9FFF]{5,}")]
    private static partial Regex LongHanRunRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z'-]*")]
    private static partial Regex LatinWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex WhitespaceBeforeNewlineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLinesRegex();
}
