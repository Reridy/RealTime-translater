using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RealTimeTranslater.Core.Translation;

namespace RealTimeTranslater.App.Translation;

public sealed class OllamaTranslationProvider : IBatchTranslationProvider
{
    private const int MaximumHttpAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _mode;
    private readonly SemaphoreSlim _translationGate =
        new(1, 1);

    public OllamaTranslationProvider(
        HttpClient httpClient,
        string endpoint,
        string model,
        string mode = "Balanced")
    {
        _httpClient = httpClient;
        _endpoint = endpoint.TrimEnd('/');
        _model = model.Trim();
        _mode = NormalizeMode(mode);

        if (_model.Length == 0)
            throw new ArgumentException(
                "Ollama model name is required.",
                nameof(model));
    }

    public string Name => "Ollama";

    public async Task WarmupAsync(
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _model,
            prompt = string.Empty,
            stream = false,
            keep_alive = "2h"
        };

        _ = await PostJsonWithRetriesAsync(
            "/api/generate",
            payload,
            cancellationToken);
    }

    public async Task<string> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        await _translationGate.WaitAsync(
            cancellationToken);

        try
        {
        using var translationBudget =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        translationBudget.CancelAfter(
            TranslationBudget);

        try
        {
        var sourceLanguage = DetectSourceLanguage(
            request.Text,
            request.SourceLanguage);

        if (TranslationQualityGuard.IsSameLanguage(
                sourceLanguage,
                request.TargetLanguage))
        {
            return request.Text.Trim();
        }

        if (IsTranslateGemmaModel(_model))
        {
            return await TranslateWithTranslateGemmaAsync(
                request.Text,
                sourceLanguage,
                request.TargetLanguage,
                request.Context,
                request.Route,
                translationBudget.Token);
        }

        return await TranslateWithGeneralModelAsync(
            request,
            sourceLanguage,
            translationBudget.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Translation exceeded the {TranslationBudget.TotalSeconds:0} second realtime budget.");
        }
        }
        finally
        {
            _translationGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
            return Array.Empty<string>();

        if (!IsTranslateGemmaModel(_model) ||
            requests.Count == 1)
        {
            if (requests.Count == 1)
            {
                return new[]
                {
                    await TranslateAsync(
                        requests[0],
                        cancellationToken)
                };
            }

            throw new NotSupportedException(
                "Batch translation is currently optimized for TranslateGemma.");
        }

        await _translationGate.WaitAsync(
            cancellationToken);

        try
        {
            using var translationBudget =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            translationBudget.CancelAfter(
                TranslationBudget);

            var results =
                new string[requests.Count];

            var pending =
                new List<(int Index, TranslationRequest Request, string SourceLanguage)>();

            for (var i = 0; i < requests.Count; i++)
            {
                var request =
                    requests[i];

                var detectedSource =
                    DetectSourceLanguage(
                        request.Text,
                        request.SourceLanguage);

                if (TranslationQualityGuard.IsSameLanguage(
                        detectedSource,
                        request.TargetLanguage))
                {
                    results[i] =
                        request.Text.Trim();
                    continue;
                }

                pending.Add((
                    i,
                    request,
                    detectedSource));
            }

            if (pending.Count == 0)
                return results;

            if (pending.Count == 1)
            {
                var item =
                    pending[0];

                results[item.Index] =
                    await TranslateWithTranslateGemmaAsync(
                        item.Request.Text,
                        item.SourceLanguage,
                        item.Request.TargetLanguage,
                        item.Request.Context,
                        item.Request.Route,
                        translationBudget.Token);

                return results;
            }

            Exception? lastError = null;

            for (var attempt = 0;
                 attempt < 2;
                 attempt++)
            {
                try
                {
                    var translated =
                        await RequestTranslateGemmaBatchAsync(
                            pending
                                .Select(item => (
                                    item.Request.Text,
                                    item.SourceLanguage))
                                .ToArray(),
                            requests[0].TargetLanguage,
                            pending[0].Request.Context,
                            StrongestRoute(
                                pending.Select(item =>
                                    item.Request.Route)),
                            strict: attempt > 0,
                            translationBudget.Token);

                    if (translated.Count != pending.Count)
                    {
                        throw new InvalidOperationException(
                            "TranslateGemma batch result count did not match request count.");
                    }

                    var allValid = true;

                    for (var i = 0; i < pending.Count; i++)
                    {
                        var normalized =
                            KoreanTranslationGuard.Normalize(
                                translated[i]);

                        if (!TranslationQualityGuard.IsAcceptable(
                                pending[i].Request.Text,
                                normalized,
                                pending[i].SourceLanguage,
                                pending[i].Request.TargetLanguage) ||
                            TranslationContextGuard.ContainsContextLeak(
                                normalized,
                                pending[i].Request.Context,
                                pending[i].Request.Text))
                        {
                            allValid = false;
                            break;
                        }

                        results[pending[i].Index] =
                            normalized;
                    }

                    if (allValid)
                        return results;

                    lastError =
                        new InvalidOperationException(
                            "TranslateGemma batch output failed target-language quality validation.");
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                    when (ex is HttpRequestException or
                        InvalidOperationException or
                        TimeoutException)
                {
                    lastError = ex;
                }

                if (attempt == 0)
                {
                    await Task.Delay(
                        80,
                        translationBudget.Token);
                }
            }

            try
            {
                for (var i = 0; i < pending.Count; i++)
                {
                    var item =
                        pending[i];

                    results[item.Index] =
                        await TranslateWithTranslateGemmaAsync(
                            item.Request.Text,
                            item.SourceLanguage,
                            item.Request.TargetLanguage,
                            item.Request.Context,
                            item.Request.Route,
                            translationBudget.Token);
                }

                return results;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
                when (ex is HttpRequestException or
                    InvalidOperationException or
                    TimeoutException)
            {
                throw new InvalidOperationException(
                    "TranslateGemma batch recovery failed.",
                    ex);
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Batch translation exceeded the {TranslationBudget.TotalSeconds:0} second realtime budget.");
        }
        finally
        {
            _translationGate.Release();
        }
    }

    private async Task<string> TranslateWithTranslateGemmaAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<string> context,
        TranslationRoute route,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var translated =
                    await RequestTranslateGemmaAsync(
                        sourceText,
                        sourceLanguage,
                        targetLanguage,
                        context,
                        route,
                        strict: attempt > 0,
                        cancellationToken);

                translated =
                    KoreanTranslationGuard.Normalize(
                        translated);

                if (TranslationQualityGuard.IsAcceptable(
                        sourceText,
                        translated,
                        sourceLanguage,
                        targetLanguage) &&
                    !TranslationContextGuard.ContainsContextLeak(
                        translated,
                        context,
                        sourceText))
                {
                    return translated;
                }

                var recovered =
                    RecoverBestTargetLine(
                        translated,
                        targetLanguage);

                if (!string.IsNullOrWhiteSpace(
                        recovered) &&
                    TranslationQualityGuard.IsAcceptable(
                        sourceText,
                        recovered,
                        sourceLanguage,
                        targetLanguage) &&
                    !TranslationContextGuard.ContainsContextLeak(
                        recovered,
                        context,
                        sourceText))
                {
                    return recovered;
                }

                lastError =
                    new InvalidOperationException(
                        "TranslateGemma output did not pass target-language quality checks.");
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
                when (ex is HttpRequestException or
                    InvalidOperationException or
                    TimeoutException)
            {
                lastError = ex;
            }

            if (attempt == 0)
            {
                await Task.Delay(
                    90,
                    cancellationToken);
            }
        }

        try
        {
            var repaired =
                await RequestTranslateGemmaRepairAsync(
                    sourceText,
                    sourceLanguage,
                    targetLanguage,
                    context,
                    route,
                    cancellationToken);

            repaired =
                KoreanTranslationGuard.Normalize(
                    repaired);

            if (TranslationQualityGuard.IsAcceptable(
                    sourceText,
                    repaired,
                    sourceLanguage,
                    targetLanguage) &&
                !TranslationContextGuard.ContainsContextLeak(
                    repaired,
                    context,
                    sourceText))
            {
                return repaired;
            }

            var recovered =
                RecoverBestTargetLine(
                    repaired,
                    targetLanguage);

            if (!string.IsNullOrWhiteSpace(
                    recovered) &&
                TranslationQualityGuard.IsAcceptable(
                    sourceText,
                    recovered,
                    sourceLanguage,
                    targetLanguage) &&
                !TranslationContextGuard.ContainsContextLeak(
                    recovered,
                    context,
                    sourceText))
            {
                return recovered;
            }

            lastError =
                new InvalidOperationException(
                    "Structured recovery output did not pass target-language quality checks.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or
                InvalidOperationException or
                TimeoutException)
        {
            lastError = ex;
        }

        throw new InvalidOperationException(
            "TranslateGemma could not produce a safe target-language translation.",
            lastError);
    }

    private async Task<string> TranslateWithGeneralModelAsync(
        TranslationRequest request,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var speakerContext =
            BuildSpeakerContext(request.Context);

        var glossaryContext =
            BuildGlossaryContext(
                request.Context,
                request.Text);

        var recentDialogueContext =
            BuildRecentDialogueContext(
                request.Context,
                request.Text);

        Exception? primaryError = null;

        try
        {
            var primary =
                await RequestGeneralModelAsync(
                    request.Text,
                    sourceLanguage,
                    request.TargetLanguage,
                    speakerContext,
                    glossaryContext,
                    recentDialogueContext,
                    request.Route,
                    structuredOutput: true,
                    strict: false,
                    cancellationToken);

            primary =
                KoreanTranslationGuard.Normalize(
                    primary);

            if (TranslationQualityGuard.IsAcceptable(
                    request.Text,
                    primary,
                    sourceLanguage,
                    request.TargetLanguage) &&
                !TranslationContextGuard.ContainsContextLeak(
                    primary,
                    request.Context,
                    request.Text))
            {
                return primary;
            }

            primaryError =
                new InvalidOperationException(
                    "Structured translation failed target-language quality validation.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or
                InvalidOperationException or
                TimeoutException)
        {
            primaryError = ex;
        }

        Exception? strictError = null;

        try
        {
            var strict =
                await RequestGeneralModelAsync(
                    request.Text,
                    sourceLanguage,
                    request.TargetLanguage,
                    speakerContext: string.Empty,
                    glossaryContext: glossaryContext,
                    recentDialogueContext: string.Empty,
                    route: request.Route,
                    structuredOutput: false,
                    strict: true,
                    cancellationToken);

            strict =
                KoreanTranslationGuard.Normalize(
                    strict);

            if (TranslationQualityGuard.IsAcceptable(
                    request.Text,
                    strict,
                    sourceLanguage,
                    request.TargetLanguage) &&
                !TranslationContextGuard.ContainsContextLeak(
                    strict,
                    request.Context,
                    request.Text))
            {
                return strict;
            }

            var recovered =
                RecoverBestTargetLine(
                    strict,
                    request.TargetLanguage);

            if (!string.IsNullOrWhiteSpace(recovered) &&
                TranslationQualityGuard.IsAcceptable(
                    request.Text,
                    recovered,
                    sourceLanguage,
                    request.TargetLanguage) &&
                !TranslationContextGuard.ContainsContextLeak(
                    recovered,
                    request.Context,
                    request.Text))
            {
                return recovered;
            }

            strictError =
                new InvalidOperationException(
                    "Strict translation failed target-language quality validation.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or
                InvalidOperationException or
                TimeoutException)
        {
            strictError = ex;
        }

        throw new InvalidOperationException(
            "Ollama translation failed after validation/retry: " +
            (strictError?.Message ??
             primaryError?.Message ??
             "unknown error"),
            strictError ?? primaryError);
    }

    private async Task<IReadOnlyList<string>> RequestTranslateGemmaBatchAsync(
        IReadOnlyList<(string Text, string SourceLanguage)> items,
        string targetLanguage,
        IReadOnlyList<string> context,
        TranslationRoute route,
        bool strict,
        CancellationToken cancellationToken)
    {
        var targetName =
            LanguageName(targetLanguage);

        var prompt =
            new StringBuilder();

        var combinedSource =
            string.Join(
                "\n",
                items.Select(item =>
                    item.Text));

        var glossaryContext =
            BuildGlossaryContext(
                context,
                combinedSource);

        var speakerContext =
            BuildSpeakerContext(
                context);

        var recentDialogueContext =
            BuildRecentDialogueContext(
                context,
                combinedSource);

        if (!string.IsNullOrWhiteSpace(
                speakerContext))
        {
            prompt.AppendLine(
                $"Speaker context: {speakerContext}");
        }

        if (!string.IsNullOrWhiteSpace(
                glossaryContext))
        {
            prompt.AppendLine(
                "Mandatory game glossary. When a source term appears, use the specified target term exactly:");
            prompt.AppendLine(
                glossaryContext);
        }

        if (!strict &&
            !string.IsNullOrWhiteSpace(
                recentDialogueContext))
        {
            prompt.AppendLine(
                "CONTEXT ONLY — never quote, translate, summarize, or copy any of these lines into the result. Use them only to understand tone and continuity:");
            prompt.AppendLine(
                recentDialogueContext);
            prompt.AppendLine(
                "END CONTEXT ONLY");
        }

        prompt.Append(
            strict
                ? $"Translate every numbered item to {targetName}. Return only the JSON result. Do not omit, merge, explain, or continue any item. For English-source items only, difficult stylized fragments such as R-Really or coined terms such as NukuNuku may remain exactly as written when translating them would distort the character's wording. Ordinary English must still be translated. For Japanese, Chinese, or other non-user-language source items, do not leave source-script words untranslated; translate them or render names/coinages phonetically in the selected target language.\n"
                : $"Translate every numbered item to natural {targetName}. Preserve each item's meaning, tone, hesitation, and speaker wording. For English-source items only, difficult stylized fragments such as R-Really or coined terms such as NukuNuku may remain exactly as written when translating them would distort the character's wording. Ordinary English must still be translated. For Japanese, Chinese, or other non-user-language source items, do not leave source-script words untranslated; translate them or render names/coinages phonetically in the selected target language. Return only the JSON result in the same order.\n");

        for (var i = 0; i < items.Count; i++)
        {
            prompt.Append(i + 1);
            prompt.Append(". [");
            prompt.Append(items[i].SourceLanguage);
            prompt.Append("] ");
            prompt.AppendLine(items[i].Text);
        }

        var totalSourceLength =
            items.Sum(item => item.Text.Length);

        var payload = new
        {
            model = _model,
            stream = false,
            keep_alive = "2h",
            format = new
            {
                type = "object",
                properties = new
                {
                    translations = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "string"
                        },
                        minItems = items.Count,
                        maxItems = items.Count
                    }
                },
                required = new[] { "translations" },
                additionalProperties = false
            },
            options = new
            {
                temperature = 0.0,
                num_ctx = BatchContextBudgetFor(route),
                num_predict = Math.Clamp(
                    (int)Math.Ceiling(totalSourceLength * 1.25) + 40,
                    72,
                    BatchOutputBudgetFor(route)),
                repeat_penalty =
                    strict ? 1.12 : 1.08
            },
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = prompt.ToString()
                }
            }
        };

        var raw =
            await SendChatAsync(
                payload,
                cancellationToken);

        try
        {
            using var json =
                JsonDocument.Parse(raw);

            if (json.RootElement.ValueKind ==
                    JsonValueKind.Object &&
                json.RootElement.TryGetProperty(
                    "translations",
                    out var translations) &&
                translations.ValueKind ==
                    JsonValueKind.Array)
            {
                return translations
                    .EnumerateArray()
                    .Select(item =>
                        item.ValueKind == JsonValueKind.String
                            ? item.GetString() ?? string.Empty
                            : string.Empty)
                    .ToArray();
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException(
            "TranslateGemma batch response did not contain a translations array.");
    }

    private async Task<string> RequestTranslateGemmaRepairAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<string> context,
        TranslationRoute route,
        CancellationToken cancellationToken)
    {
        var sourceName =
            LanguageName(sourceLanguage);
        var targetName =
            LanguageName(targetLanguage);

        var glossaryContext =
            BuildGlossaryContext(
                context,
                sourceText);

        var speakerContext =
            BuildSpeakerContext(
                context);

        var payload = new
        {
            model = _model,
            stream = false,
            keep_alive = "2h",
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
                num_ctx = RepairContextBudgetFor(route),
                num_predict =
                    TranslateGemmaOutputBudget(
                        sourceText,
                        RepairOutputBudgetFor(route)),
                repeat_penalty = 1.14
            },
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content =
                        $"Translate this {sourceName} text to natural {targetName}. " +
                        $"Return exactly one JSON object with the field translation. " +
                        $"The value must contain only the complete {targetName} translation. " +
                        PreservationInstruction(
                            sourceLanguage,
                            targetLanguage) +
                        $" Do not explain, repeat the source, or continue the dialogue. " +
                        (string.IsNullOrWhiteSpace(glossaryContext)
                            ? string.Empty
                            : $"Mandatory glossary: {glossaryContext}. ") +
                        (string.IsNullOrWhiteSpace(speakerContext)
                            ? string.Empty
                            : $"{speakerContext}. ") +
                        "\n\n" +
                        sourceText
                }
            }
        };

        return await SendChatAsync(
            payload,
            cancellationToken);
    }

    private async Task<string> RequestTranslateGemmaAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<string> context,
        TranslationRoute route,
        bool strict,
        CancellationToken cancellationToken)
    {
        var sourceName =
            LanguageName(sourceLanguage);
        var targetName =
            LanguageName(targetLanguage);

        var preservation =
            PreservationInstruction(
                sourceLanguage,
                targetLanguage);

        var glossaryContext =
            BuildGlossaryContext(
                context,
                sourceText);

        var glossaryInstruction =
            string.IsNullOrWhiteSpace(
                glossaryContext)
                ? string.Empty
                : $" Mandatory glossary: {glossaryContext}. Use those target terms exactly.";

        var speakerContext =
            BuildSpeakerContext(
                context);

        var speakerInstruction =
            string.IsNullOrWhiteSpace(
                speakerContext)
                ? string.Empty
                : $" {speakerContext}.";

        var recentDialogueContext =
            route == TranslationRoute.Fast
                ? string.Empty
                : BuildRecentDialogueContext(
                    context,
                    sourceText);

        var recentInstruction =
            strict ||
            string.IsNullOrWhiteSpace(
                recentDialogueContext)
                ? string.Empty
                : $" CONTEXT ONLY (never copy or translate this into the answer): {recentDialogueContext} END CONTEXT ONLY.";

        var prompt = strict
            ? $"Translate this {sourceName} text to {targetName}. Output only the complete {targetName} translation. {preservation}{glossaryInstruction}{speakerInstruction} Do not explain, repeat, continue, or omit.\n\n" +
              sourceText
            : $"Translate {sourceName} to natural {targetName}. Preserve meaning, tone, hesitation, negation, and names. {preservation}{glossaryInstruction}{speakerInstruction}{recentInstruction} Output only the translation.\n\n" +
              sourceText;

        var payload = new
        {
            model = _model,
            stream = false,
            keep_alive = "2h",
            options = new
            {
                temperature = 0.0,
                num_ctx =
                    TranslateGemmaContextBudget(
                        strict,
                        route),
                num_predict =
                    TranslateGemmaOutputBudget(
                        sourceText,
                        TranslateGemmaOutputCap(
                            strict,
                            route)),
                repeat_penalty =
                    strict ? 1.12 : 1.08
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
        string glossaryContext,
        string recentDialogueContext,
        TranslationRoute route,
        bool structuredOutput,
        bool strict,
        CancellationToken cancellationToken)
    {
        var targetName =
            LanguageName(
                targetLanguage);

        var systemPrompt =
            $"You are a professional {targetName} game localization translator. " +
            $"Translate ONLY the SOURCE text into natural {targetName}. " +
            "Preserve meaning exactly: negation, subject/object relations, " +
            "chronology, emotion, hesitation, emphasis, jokes, and tone. " +
            "Never invent, omit, continue, explain, or answer the dialogue. " +
            PreservationInstruction(
                sourceLanguage,
                targetLanguage) +
            " Short game abbreviations already present in SOURCE may remain.";

        if (!string.IsNullOrWhiteSpace(
                glossaryContext))
        {
            systemPrompt +=
                " Mandatory game glossary: " +
                glossaryContext +
                ". When a source term appears, use its specified target term exactly.";
        }

        if (structuredOutput)
        {
            systemPrompt +=
                " Return exactly one JSON object with one string field named " +
                "translation and nothing else.";
        }
        else
        {
            systemPrompt +=
                $" Return only the {targetName} translation and stop immediately.";
        }

        if (strict)
        {
            systemPrompt +=
                " This is a correction retry. Ignore all prior conversation. " +
                $"Do not include source-language text unless it is an allowed English fragment or already belongs to {targetName}.";
        }

        var userText =
            $"Source language: {sourceLanguage}\n" +
            $"Target language: {targetLanguage}\n";

        if (!string.IsNullOrWhiteSpace(
                speakerContext))
        {
            userText +=
                $"Speaker context: {speakerContext}\n";
        }

        if (!strict &&
            !string.IsNullOrWhiteSpace(
                recentDialogueContext))
        {
            userText +=
                "CONTEXT ONLY — never quote, translate, summarize, or copy this into the answer:\n" +
                recentDialogueContext +
                "\nEND CONTEXT ONLY\n";
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
                keep_alive = "2h",
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
                    required =
                        new[] { "translation" },
                    additionalProperties = false
                },
                options = new
                {
                    temperature = 0.0,
                    top_p = 0.85,
                    num_ctx = GeneralStructuredContextBudgetFor(route),
                    num_predict =
                        OutputBudget(
                            sourceText,
                            GeneralStructuredOutputBudgetFor(route)),
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
                keep_alive = "2h",
                options = new
                {
                    temperature = 0.0,
                    top_p = 0.8,
                    num_ctx = GeneralStrictContextBudgetFor(route),
                    num_predict =
                        OutputBudget(
                            sourceText,
                            GeneralStrictOutputBudgetFor(route)),
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
            using var json =
                JsonDocument.Parse(raw);

            if (json.RootElement.ValueKind ==
                    JsonValueKind.Object &&
                json.RootElement.TryGetProperty(
                    "translation",
                    out var translation) &&
                translation.ValueKind ==
                    JsonValueKind.String)
            {
                return translation.GetString() ??
                    string.Empty;
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
        var body =
            await PostJsonWithRetriesAsync(
                "/api/chat",
                payload,
                cancellationToken);

        using var document =
            JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty(
                "message",
                out var message) &&
            message.TryGetProperty(
                "content",
                out var content))
        {
            var value =
                content.GetString()?.Trim();

            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        if (document.RootElement.TryGetProperty(
                "error",
                out var error))
        {
            throw new InvalidOperationException(
                "Ollama generation error: " +
                error.GetString());
        }

        throw new InvalidOperationException(
            "Ollama response did not contain message.content.");
    }

    private async Task<string> PostJsonWithRetriesAsync(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 0;
             attempt < MaximumHttpAttempts;
             attempt++)
        {
            try
            {
                using var timeout =
                    CancellationTokenSource
                        .CreateLinkedTokenSource(
                            cancellationToken);

                timeout.CancelAfter(
                    RequestTimeout);

                using var response =
                    await _httpClient
                        .PostAsJsonAsync(
                            _endpoint + path,
                            payload,
                            timeout.Token);

                var body =
                    await response.Content
                        .ReadAsStringAsync(
                            timeout.Token);

                if (response.IsSuccessStatusCode)
                    return body;

                var detail =
                    TryReadOllamaError(
                        body);

                var error =
                    new HttpRequestException(
                        $"Ollama {(int)response.StatusCode} " +
                        $"{response.ReasonPhrase}: {detail}",
                        inner: null,
                        response.StatusCode);

                lastError = error;

                if (!IsTransient(
                        response.StatusCode) ||
                    attempt ==
                        MaximumHttpAttempts - 1)
                {
                    throw error;
                }
            }
            catch (OperationCanceledException)
                when (!cancellationToken
                    .IsCancellationRequested)
            {
                lastError =
                    new TimeoutException(
                        $"Ollama request exceeded {RequestTimeout.TotalSeconds:0}s.");

                if (attempt ==
                    MaximumHttpAttempts - 1)
                {
                    throw lastError;
                }
            }
            catch (HttpRequestException ex)
                when (attempt <
                    MaximumHttpAttempts - 1 &&
                    IsTransient(
                        ex.StatusCode))
            {
                lastError = ex;
            }

            await Task.Delay(
                RetryDelay(attempt),
                cancellationToken);
        }

        throw lastError ??
            new InvalidOperationException(
                "Ollama request failed.");
    }

    private static TimeSpan RetryDelay(
        int attempt)
        => attempt switch
        {
            0 => TimeSpan.FromMilliseconds(180),
            1 => TimeSpan.FromMilliseconds(550),
            _ => TimeSpan.FromSeconds(1)
        };

    private static string TryReadOllamaError(
        string body)
    {
        try
        {
            using var json =
                JsonDocument.Parse(body);

            if (json.RootElement.TryGetProperty(
                    "error",
                    out var error))
            {
                return error.ValueKind ==
                        JsonValueKind.String
                    ? error.GetString() ??
                        "unknown error"
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

    private static string BuildGlossaryContext(
        IReadOnlyList<string> context,
        string sourceText)
    {
        var entries =
            context
                .Select(line =>
                    line.Trim())
                .Where(line =>
                    line.StartsWith(
                        "Glossary:",
                        StringComparison.OrdinalIgnoreCase))
                .Select(line =>
                    line[
                        "Glossary:".Length..]
                        .Trim())
                .Select(line =>
                {
                    var separator =
                        line.IndexOf(
                            "=>",
                            StringComparison.Ordinal);

                    if (separator <= 0 ||
                        separator >=
                            line.Length - 2)
                    {
                        return (
                            Source: string.Empty,
                            Target: string.Empty);
                    }

                    return (
                        Source:
                            line[..separator]
                                .Trim(),
                        Target:
                            line[
                                (separator + 2)..]
                                .Trim());
                })
                .Where(entry =>
                    entry.Source.Length > 0 &&
                    entry.Target.Length > 0 &&
                    sourceText.Contains(
                        entry.Source,
                        StringComparison.OrdinalIgnoreCase))
                .Take(12)
                .Select(entry =>
                    $"{entry.Source} => {entry.Target}")
                .ToArray();

        return string.Join(
            "; ",
            entries);
    }

    private string BuildRecentDialogueContext(
        IReadOnlyList<string> context,
        string currentSource)
    {
        if (string.Equals(
                _mode,
                "Fast",
                StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return TranslationContextGuard
            .BuildRecentTargetContext(
                context,
                currentSource,
                maxLines:
                    string.Equals(
                        _mode,
                        "Quality",
                        StringComparison.Ordinal)
                        ? 3
                        : 2);
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
                ch is >= '\u3040' and <= '\u30FF' ||
                ch is >= '\u31F0' and <= '\u31FF'))
        {
            return "ja";
        }

        if (text.Any(ch =>
                ch is >= '\uAC00' and <= '\uD7A3' ||
                ch is >= '\u3131' and <= '\u318E'))
        {
            return "ko";
        }

        if (text.Any(ch =>
                ch is >= '\u3400' and <= '\u4DBF' ||
                ch is >= '\u4E00' and <= '\u9FFF'))
        {
            return "zh";
        }

        if (text.Any(ch =>
                ch is >= '\u0400' and <= '\u052F'))
        {
            return "ru";
        }

        if (text.Any(ch =>
                ch is >= '\u0600' and <= '\u06FF' ||
                ch is >= '\u0750' and <= '\u077F'))
        {
            return "ar";
        }

        if (text.Any(ch =>
                ch is >= '\u0E00' and <= '\u0E7F'))
        {
            return "th";
        }

        if (text.Any(ch =>
                ch is >= 'A' and <= 'Z' ||
                ch is >= 'a' and <= 'z'))
        {
            return "en";
        }

        return "auto";
    }

    private static string LanguageName(
        string code)
    {
        var full =
            TranslationQualityGuard
                .NormalizeFullLanguageCode(
                    code);

        if (full == "zh-cn")
            return "Simplified Chinese";

        if (full == "zh-tw")
            return "Traditional Chinese";

        return TranslationQualityGuard
            .NormalizeLanguageCode(code) switch
        {
            "en" => "English",
            "ja" => "Japanese",
            "zh" => "Chinese",
            "ko" => "Korean",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "pt" => "Portuguese",
            "it" => "Italian",
            "ru" => "Russian",
            "vi" => "Vietnamese",
            "id" => "Indonesian",
            "th" => "Thai",
            "pl" => "Polish",
            "tr" => "Turkish",
            "nl" => "Dutch",
            "ar" => "Arabic",
            _ => "the selected target language"
        };
    }

    private static string PreservationInstruction(
        string sourceLanguage,
        string targetLanguage)
    {
        var source =
            TranslationQualityGuard
                .NormalizeLanguageCode(
                    sourceLanguage);

        var target =
            TranslationQualityGuard
                .NormalizeLanguageCode(
                    targetLanguage);

        var targetName =
            LanguageName(target);

        if (TranslationQualityGuard.IsSameLanguage(
                sourceLanguage,
                targetLanguage))
        {
            return
                $"Keep wording already written in {targetName} unchanged when appropriate.";
        }

        var englishRule =
            " Difficult English fragments that are intentionally awkward to localize, " +
            "such as stutters like R-Really, stylized coined terms like NukuNuku, " +
            "or proper names, may remain exactly in English when translating them " +
            "would distort the wording. Ordinary English must still be translated.";

        return
            englishRule +
            $" Do not leave other foreign-language source script untranslated. " +
            $"Translate it into {targetName}, or render names and coined terms " +
            $"phonetically in {targetName}.";
    }

    private static string RecoverBestTargetLine(
        string candidate,
        string targetLanguage)
    {
        if (!string.Equals(
                TranslationQualityGuard.NormalizeLanguageCode(
                    targetLanguage),
                "ko",
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return KoreanTranslationGuard
            .RecoverBestKoreanLine(
                candidate);
    }

    private TimeSpan TranslationBudget
        => _mode switch
        {
            "Fast" => TimeSpan.FromSeconds(16),
            "Quality" => TimeSpan.FromSeconds(35),
            _ => TimeSpan.FromSeconds(25)
        };

    private TimeSpan RequestTimeout
        => _mode switch
        {
            "Fast" => TimeSpan.FromSeconds(14),
            "Quality" => TimeSpan.FromSeconds(28),
            _ => TimeSpan.FromSeconds(20)
        };

    private int BatchContextBudgetFor(
        TranslationRoute route)
        => ApplyRouteBudget(
            _mode switch
            {
                "Fast" => 768,
                "Quality" => 1536,
                _ => 1024
            },
            route,
            minimum: 384,
            maximum: 1792);

    private int BatchOutputBudgetFor(
        TranslationRoute route)
        => ApplyRouteBudget(
            _mode switch
            {
                "Fast" => 256,
                "Quality" => 512,
                _ => 384
            },
            route,
            minimum: 128,
            maximum: 576);

    private int RepairContextBudgetFor(
        TranslationRoute route)
        => ApplyRouteBudget(
            _mode switch
            {
                "Fast" => 512,
                "Quality" => 896,
                _ => 640
            },
            route,
            minimum: 384,
            maximum: 1024);

    private int RepairOutputBudgetFor(
        TranslationRoute route)
        => ApplyRouteBudget(
            _mode switch
            {
                "Fast" => 128,
                "Quality" => 224,
                _ => 160
            },
            route,
            minimum: 96,
            maximum: 256);

    private int TranslateGemmaContextBudget(
        bool strict,
        TranslationRoute route)
    {
        var baseBudget =
            _mode switch
            {
                "Fast" => strict ? 512 : 384,
                "Quality" => strict ? 896 : 768,
                _ => strict ? 640 : 512
            };

        return ApplyRouteBudget(
            baseBudget,
            route,
            minimum:
                strict
                    ? 320
                    : 256,
            maximum: 1024);
    }

    private int TranslateGemmaOutputCap(
        bool strict,
        TranslationRoute route)
    {
        var baseBudget =
            _mode switch
            {
                "Fast" => strict ? 128 : 96,
                "Quality" => strict ? 224 : 192,
                _ => strict ? 160 : 128
            };

        return ApplyRouteBudget(
            baseBudget,
            route,
            minimum:
                strict
                    ? 72
                    : 56,
            maximum: 256);
    }

    private int GeneralStructuredContextBudgetFor(
        TranslationRoute route)
        => ApplyRouteBudget(
            _mode switch
            {
                "Fast" => 768,
                "Quality" => 1536,
                _ => 1024
            },
            route,
            minimum: 384,
            maximum: 1792);

    private int GeneralStructuredOutputBudgetFor(
        TranslationRoute route)
        => ApplyRouteBudget(
            _mode switch
            {
                "Fast" => 160,
                "Quality" => 320,
                _ => 224
            },
            route,
            minimum: 96,
            maximum: 384);

    private int GeneralStrictContextBudgetFor(
        TranslationRoute route)
        => ApplyRouteBudget(
            _mode switch
            {
                "Fast" => 512,
                "Quality" => 1024,
                _ => 768
            },
            route,
            minimum: 320,
            maximum: 1152);

    private int GeneralStrictOutputBudgetFor(
        TranslationRoute route)
        => ApplyRouteBudget(
            _mode switch
            {
                "Fast" => 128,
                "Quality" => 256,
                _ => 176
            },
            route,
            minimum: 80,
            maximum: 320);

    private static int ApplyRouteBudget(
        int value,
        TranslationRoute route,
        int minimum,
        int maximum)
    {
        var multiplier =
            route switch
            {
                TranslationRoute.Fast => 0.68,
                TranslationRoute.Quality => 1.22,
                _ => 1.0
            };

        return Math.Clamp(
            (int)Math.Round(
                value * multiplier),
            minimum,
            maximum);
    }

    private static TranslationRoute StrongestRoute(
        IEnumerable<TranslationRoute> routes)
        => routes.DefaultIfEmpty(
                TranslationRoute.Standard)
            .Max();

    private static string NormalizeMode(
        string? mode)
        => mode?.Trim() switch
        {
            "Fast" => "Fast",
            "Quality" => "Quality",
            _ => "Balanced"
        };

    private static int TranslateGemmaOutputBudget(
        string sourceText,
        int maximum)
        => Math.Clamp(
            (int)Math.Ceiling(
                sourceText.Length * 0.95) +
            20,
            36,
            maximum);

    private static int OutputBudget(
        string sourceText,
        int maximum)
        => Math.Clamp(
            (int)Math.Ceiling(
                sourceText.Length * 1.35) +
            28,
            48,
            maximum);

    private static bool IsTranslateGemmaModel(
        string model)
        => model.StartsWith(
            "translategemma",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(
        HttpStatusCode? status)
        => status is null ||
            status is
                HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout or
                (HttpStatusCode)429;
}
