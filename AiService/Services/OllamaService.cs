using AiService.Models;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiService.Services;

public class OllamaService(IConfiguration config, ILogger<OllamaService> logger) : IAgentChatClient
{
    private readonly string _baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
    private readonly string _model = config["Ollama:Model"] ?? "qwen3:14b";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(300) };
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static int _queueCounter;
    private static int _currentQueueLength;

    public int QueueLength => _currentQueueLength;

    /// <summary>Встать в очередь к LLM. Возвращает позицию в очереди (1 = сейчас выполняется).</summary>
    public async Task<int> EnterQueueAsync(CancellationToken ct)
    {
        var pos = Interlocked.Increment(ref _queueCounter);
        Interlocked.Exchange(ref _currentQueueLength, pos);
        await _gate.WaitAsync(ct);
        return pos;
    }

    /// <summary>Покинуть очередь.</summary>
    public void LeaveQueue()
    {
        _gate.Release();
        var left = Interlocked.Decrement(ref _queueCounter);
        Interlocked.Exchange(ref _currentQueueLength, Math.Max(0, left));
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Встать в очередь, выполнить запрос к LLM, покинуть очередь.</summary>
    /// <param name="temperature">Температура сэмплирования. null — дефолт 0.1.
    /// verify-part использует 0.05: одинаковый вход обязан давать одинаковый
    /// вердикт (флипы на том же входе недопустимы для подсказки мастеру).</param>
    public async Task<(string Response, string? Thinking)> GenerateAsync(
    string prompt,
    bool think = false,
    IProgress<string>? thinkingProgress = null,
    CancellationToken ct = default,
    string? model = null,
    double? temperature = null)
    {
        await EnterQueueAsync(ct);
        try
        {
            return await GenerateCoreAsync(prompt, think, thinkingProgress, ct, model, temperature);
        }
        finally
        {
            LeaveQueue();
        }
    }

    /// <summary>Выполнить запрос к Ollama без управления очередью.</summary>
    public async Task<(string Response, string? Thinking)> GenerateCoreAsync(
    string prompt,
    bool think = false,
    IProgress<string>? thinkingProgress = null,
    CancellationToken ct = default,
    string? model = null,
    double? temperature = null)
    {
        try
        {
            return await GenerateCoreOnceAsync(prompt, think, thinkingProgress, ct, model, temperature);
        }
        catch (DegenerateGenerationException ex) when (think)
        {
            // Один ретрай с температурой повыше: петля часто рвётся на менее
            // жадном сэмплировании. Не помогло — исключение уходит выше, контроллер
            // эскалирует день (fail-safe, как HasError).
            logger.LogWarning(ex,
                "Петля в рассуждении, повторный прогон с температурой 0.3");
            thinkingProgress?.Report("\n… повторный прогон после зацикливания …\n");
            try
            {
                return await GenerateCoreOnceAsync(prompt, think, thinkingProgress, ct, model, 0.3);
            }
            catch (DegenerateGenerationException retryEx)
            {
                logger.LogWarning(retryEx, "Петля в рассуждении и после ретрая");
                throw new DegenerateGenerationException(
                    "Модель дважды зациклилась в рассуждении: " + retryEx.Message);
            }
        }
    }

    private async Task<(string Response, string? Thinking)> GenerateCoreOnceAsync(
    string prompt,
    bool think,
    IProgress<string>? thinkingProgress,
    CancellationToken ct,
    string? model,
    double? temperature)
    {
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? _model : model;

        var request = new OllamaGenerateRequest
        {
            Model = effectiveModel,
            Prompt = prompt,
            Stream = think,
            Think = think,
            Format = think ? null : "json",
            Options = new()
            {
                Temperature = temperature ?? 0.1,
                // Штраф за повторы везде (и think, и короткие JSON): петли в рассуждении
                // и залипания в формате режутся сэмплером, на детерминизм 1.15 почти не влияет.
                RepeatPenalty = 1.15,
                RepeatLastN = 64,
                // Промпт (~2.3k токенов) + данные насыщенного дня (~2-3k) + think-генерация
                // должны помещаться целиком: при переполнении Ollama молча вытесняет
                // НАЧАЛО промпта (ROLE/DEFINITIONS). 8192 не хватало в think-режиме.
                NumCtx = 16384,
                // 4096 не хватало: размышления съедали весь бюджет, JSON не оставался
                // (3 пустых ответа в прогоне 2026-07-18).
                NumPredict = think ? 8192 : -1,
            }
        };

        logger.LogInformation(
            "Отправка в Ollama. Модель: {Model}, think={Think}, символов: {Len}",
            effectiveModel, think, prompt.Length);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{_baseUrl}/api/generate")
        {
            Content = JsonContent.Create(request)
        };

        using var response = await _http.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();

        if (!think)
        {
            var result = await response.Content
                .ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: ct);
            var text = result?.Response ?? "";
            logger.LogInformation("Ответ без think. Символов: {Len}", text.Length);
            logger.LogDebug("Сырой ответ: {Raw}", text);
            return (text, null);
        }

        var fullResponse = new StringBuilder();
        var thinkBuffer = new StringBuilder();
        var lastReportedLen = 0;
        var lastLoopCheckLen = 0;

        // Временной троттлинг размышлений (≈50 репортов/сек): равномерное «печатание»
        // независимо от скорости модели + bounded-нагрузка на UI-поток. Дельты идут
        // как есть, lastReportedLen двигается только на реально отосланное — потерь нет.
        // Хвост после done дотягивается ниже без троттлинга.
        var thinkReportInterval = TimeSpan.FromMilliseconds(20);
        var lastThinkReportAt = Stopwatch.GetTimestamp();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            logger.LogTrace("Chunk: {Chunk}", line);

            OllamaStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(line);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Не удалось разобрать chunk: {Chunk}", line);
                continue;
            }

            if (chunk == null) continue;

            if (!string.IsNullOrEmpty(chunk.Thinking))
            {
                thinkBuffer.Append(chunk.Thinking);

                // Ранний обрыв петли: дальше модель будет повторяться до NumPredict,
                // жечь GPU и очередь. Проверка не чаще каждых ~200 символов прироста.
                if (thinkBuffer.Length - lastLoopCheckLen >= 200)
                {
                    lastLoopCheckLen = thinkBuffer.Length;
                    if (ThinkingLoopGuard.IsLooping(thinkBuffer.ToString()))
                    {
                        var tail = thinkBuffer.ToString(
                            Math.Max(0, thinkBuffer.Length - 200), Math.Min(200, thinkBuffer.Length));
                        throw new DegenerateGenerationException(
                            $"повтор рассуждения: «{tail.Trim()}»");
                    }
                }

                if (thinkingProgress != null)
                {
                    var currentLen = thinkBuffer.Length;
                    if (currentLen > lastReportedLen
                        && Stopwatch.GetElapsedTime(lastThinkReportAt) >= thinkReportInterval)
                    {
                        var delta = thinkBuffer.ToString(lastReportedLen,
                                           currentLen - lastReportedLen);
                        lastReportedLen = currentLen;
                        lastThinkReportAt = Stopwatch.GetTimestamp();
                        logger.LogDebug("Think delta [{L}]: [{D}]", delta.Length, delta);
                        thinkingProgress.Report(delta);
                    }
                }
            }

            if (!string.IsNullOrEmpty(chunk.Response))
                fullResponse.Append(chunk.Response);

            if (chunk.Done)
            {
                if (thinkingProgress != null && thinkBuffer.Length > lastReportedLen)
                {
                    var tail = thinkBuffer.ToString(lastReportedLen,
                                   thinkBuffer.Length - lastReportedLen);
                    if (!string.IsNullOrWhiteSpace(tail))
                        thinkingProgress.Report(tail);
                }
                break;
            }
        }

        var raw = fullResponse.ToString();
        var thinking = thinkBuffer.Length == 0 ? null : thinkBuffer.ToString();

        logger.LogInformation(
            "Ответ с think. Символов response: {RLen}, thinking: {TLen}",
            raw.Length, thinking?.Length ?? 0);
        logger.LogDebug("Сырой ответ: {Raw}", raw);

        return (raw, thinking);
    }

    /// <summary>
    /// Один раунд диалога через POST /api/chat (агентский контур): модель может вернуть
    /// текст и/или запросы tools. Без стриминга: раунд целиком. Очередь занимается
    /// на каждый раунд отдельно — между раундами её держат другие клиенты, голодания нет.
    /// format — только для финального ответа ("json"); в tool-раундах null.
    /// think:true — Ollama стримит рассуждения (stream:true): дельты идут
    /// в thinkingProgress (панель хода в UI), итог собирается здесь же.
    /// </summary>
    public async Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        string? format,
        bool think,
        CancellationToken ct,
        string? model = null,
        double? temperature = null,
        IProgress<string>? thinkingProgress = null,
        IProgress<string>? tokenProgress = null)
    {
        await EnterQueueAsync(ct);
        try
        {
            return await ChatCoreAsync(messages, tools, format, think, ct, model, temperature, thinkingProgress, tokenProgress);
        }
        finally
        {
            LeaveQueue();
        }
    }

    private async Task<ChatResult> ChatCoreAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        string? format,
        bool think,
        CancellationToken ct,
        string? model,
        double? temperature,
        IProgress<string>? thinkingProgress,
        IProgress<string>? tokenProgress)
    {
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? _model : model;

        // JsonDocument параметров живут до конца отправки: сериализация запроса
        // происходит внутри SendAsync, раньше dispose нельзя.
        var paramDocs = new List<JsonDocument>();
        try
        {
            return await ChatSendAsync();
        }
        finally
        {
            foreach (var d in paramDocs) d.Dispose();
        }

        async Task<ChatResult> ChatSendAsync()
        {
            var wireTools = tools.Select(t =>
            {
                var d = JsonDocument.Parse(t.ParametersJson);
                paramDocs.Add(d);
                return new OllamaChatTool(t.Name, t.Description, d.RootElement);
            }).ToList();

            var request = new OllamaChatRequest
            {
                Model = effectiveModel,
                Messages = messages.Select(m => new OllamaChatMessage
                {
                    Role = m.Role,
                    Content = m.Content,
                    ToolCalls = m.ToolCalls?.Select(c => new OllamaToolCall
                    {
                        Id = c.Id,
                        Function = new OllamaToolFunction
                        {
                            Name = c.Name,
                            Arguments = TryParseArguments(c.Arguments),
                        },
                    }).ToList(),
                }).ToList(),
                Tools = wireTools,
                Stream = think,
                Think = think,
                Format = format,
            Options = new()
            {
                Temperature = temperature ?? 0.1,
                RepeatPenalty = 1.15,
                RepeatLastN = 64,
                NumCtx = 16384,
                // С thinking размышления съедают бюджет: кап как в GenerateCoreAsync.
                NumPredict = think ? 8192 : -1,
            },
            };

            logger.LogInformation(
                "Chat в Ollama. Модель: {Model}, сообщений: {N}, tools: {T}, think={Think}, stream={Stream}",
                effectiveModel, messages.Count, tools.Count, think, think);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = JsonContent.Create(request),
            };

            using var response = await _http.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // think:true — стрим чанков (делюсь мыслями живьём + собираю итог);
            // tokenProgress (чат-агент) — тоже стрим: дельты content идут живьём,
            // thinking-дельт при think:false просто не бывает. Иначе один JSON целиком.
            if (think || tokenProgress != null)
                return await ChatReadStreamAsync(response, thinkingProgress, tokenProgress, ct);

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken: ct);
            var msg = result?.Message;

            var toolCalls = msg?.ToolCalls?.Select((c, i) => new AgentToolCall
            {
                Id = string.IsNullOrWhiteSpace(c.Id) ? $"call_{i}" : c.Id,
                Name = c.Function?.Name ?? "",
                Arguments = c.Function?.Arguments.ValueKind == JsonValueKind.String
                    ? c.Function.Arguments.GetString() ?? ""
                    : c.Function?.Arguments.GetRawText() ?? "",
            }).Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList() ?? [];

        logger.LogInformation(
            "Chat-ответ: символов {Len}, thinking {TLen}, tool_calls {T}",
            msg?.Content?.Length ?? 0, msg?.Thinking?.Length ?? 0, toolCalls.Count);

            return new ChatResult
            {
                Content = msg?.Content ?? "",
                ToolCalls = toolCalls,
                Thinking = msg?.Thinking,
                PromptTokens = result?.PromptEvalCount ?? 0,
                CompletionTokens = result?.EvalCount ?? 0,
                DoneReason = result?.DoneReason,
            };
        }
    }

    /// <summary>
    /// Чтение стрима /api/chat (think:true): копит content/thinking/tool_calls,
    /// дельты thinking отдаёт живьём через thinkingProgress (троттлинг ~20мс,
    /// как в GenerateCoreOnceAsync). Фрагменты аргументов tool_calls склеиваются
    /// по id; Ollama иногда повторяет целый объект вместо дельты — повторы
    /// отбрасываются сверкой, иначе аргументы удвоятся. Частичный мусор
    /// безопасен: AgentTools.Execute терпит непарсибельные аргументы ({}).
    /// </summary>
    private async Task<ChatResult> ChatReadStreamAsync(
        HttpResponseMessage response, IProgress<string>? thinkingProgress, IProgress<string>? tokenProgress, CancellationToken ct)
    {
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        var toolParts = new Dictionary<string, (string Name, StringBuilder Args)>();
        var lastThinkReportAt = Stopwatch.GetTimestamp();
        var thinkReportInterval = TimeSpan.FromMilliseconds(20);
        var lastReportedLen = 0;
        var promptTokens = 0;
        var completionTokens = 0;
        string? doneReason = null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            OllamaChatStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaChatStreamChunk>(line);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Не удалось разобрать chat-chunk");
                continue;
            }

            if (chunk is { Done: true })
            {
                promptTokens = chunk.PromptEvalCount;
                completionTokens = chunk.EvalCount;
                doneReason = chunk.DoneReason;
            }

            if (chunk?.Message is not { } msg)
            {
                if (chunk?.Done == true) break;
                continue;
            }

            if (!string.IsNullOrEmpty(msg.Thinking))
            {
                thinking.Append(msg.Thinking);
                if (thinkingProgress != null
                    && thinking.Length > lastReportedLen
                    && Stopwatch.GetElapsedTime(lastThinkReportAt) >= thinkReportInterval)
                {
                    thinkingProgress.Report(thinking.ToString(lastReportedLen,
                        thinking.Length - lastReportedLen));
                    lastReportedLen = thinking.Length;
                    lastThinkReportAt = Stopwatch.GetTimestamp();
                }
            }

            if (!string.IsNullOrEmpty(msg.Content))
            {
                content.Append(msg.Content);
                tokenProgress?.Report(msg.Content);
            }

            if (msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    var id = string.IsNullOrWhiteSpace(tc.Id)
                        ? $"call_{toolParts.Count}" : tc.Id;
                    if (!toolParts.TryGetValue(id, out var part))
                    {
                        part = ("", new StringBuilder());
                        toolParts[id] = part;
                    }
                    if (!string.IsNullOrWhiteSpace(tc.Function?.Name)
                        && string.IsNullOrWhiteSpace(part.Name))
                        part = (tc.Function.Name, part.Args);
                    var frag = tc.Function?.Arguments.ValueKind == JsonValueKind.String
                        ? tc.Function.Arguments.GetString() ?? ""
                        : tc.Function?.Arguments.GetRawText() ?? "";
                    var acc = part.Args.ToString();
                    if (frag.Length > 0 && frag != acc)
                        part.Args.Append(frag);
                    toolParts[id] = part;
                }
            }

            if (chunk.Done) break;
        }

        if (thinkingProgress != null && thinking.Length > lastReportedLen)
        {
            var tail = thinking.ToString(lastReportedLen, thinking.Length - lastReportedLen);
            if (!string.IsNullOrWhiteSpace(tail))
                thinkingProgress.Report(tail);
        }

        var toolCalls = toolParts
            .Select(kv => new AgentToolCall
            {
                Id = kv.Key,
                Name = kv.Value.Name,
                Arguments = kv.Value.Args.ToString(),
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .ToList();

        logger.LogInformation(
            "Chat-стрим завершён: content {CLen}, thinking {TLen}, tool_calls {T}",
            content.Length, thinking.Length, toolCalls.Count);

        return new ChatResult
        {
            Content = content.ToString(),
            ToolCalls = toolCalls,
            Thinking = thinking.Length == 0 ? null : thinking.ToString(),
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            DoneReason = doneReason,
        };
    }

    private class OllamaChatStreamChunk
    {
        [JsonPropertyName("message")] public OllamaChatResponseMessage? Message { get; set; }
        [JsonPropertyName("done")] public bool Done { get; set; }
        [JsonPropertyName("done_reason")] public string? DoneReason { get; set; }
        [JsonPropertyName("prompt_eval_count")] public int PromptEvalCount { get; set; }
        [JsonPropertyName("eval_count")] public int EvalCount { get; set; }
    }

    private static JsonElement TryParseArguments(string args)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    private class OllamaChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("tool_calls")] public List<OllamaToolCall>? ToolCalls { get; set; }
    }

    private class OllamaToolCall
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("function")] public OllamaToolFunction? Function { get; set; }
    }

    private class OllamaToolFunction
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("arguments")] public JsonElement Arguments { get; set; }
    }

    private class OllamaChatTool(string name, string description, JsonElement parameters)
    {
        [JsonPropertyName("type")] public string Type { get; } = "function";
        [JsonPropertyName("function")] public OllamaChatFunction Function { get; } = new(name, description, parameters);
    }

    private class OllamaChatFunction(string name, string description, JsonElement parameters)
    {
        [JsonPropertyName("name")] public string Name { get; } = name;
        [JsonPropertyName("description")] public string Description { get; } = description;
        [JsonPropertyName("parameters")] public JsonElement Parameters { get; } = parameters;
    }

    private class OllamaChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public List<OllamaChatMessage> Messages { get; set; } = [];
        [JsonPropertyName("tools")] public List<OllamaChatTool> Tools { get; set; } = [];
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonPropertyName("think")] public bool Think { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Format { get; set; }
        [JsonPropertyName("options")] public OllamaOptions Options { get; set; } = new();
    }

    private class OllamaChatResponse
    {
        [JsonPropertyName("message")] public OllamaChatResponseMessage? Message { get; set; }
        [JsonPropertyName("done_reason")] public string? DoneReason { get; set; }
        [JsonPropertyName("prompt_eval_count")] public int PromptEvalCount { get; set; }
        [JsonPropertyName("eval_count")] public int EvalCount { get; set; }
    }

    private class OllamaChatResponseMessage
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("thinking")] public string? Thinking { get; set; }
        [JsonPropertyName("tool_calls")] public List<OllamaToolCall>? ToolCalls { get; set; }
    }

    private class OllamaStreamChunk
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
        [JsonPropertyName("thinking")] public string? Thinking { get; set; }
        [JsonPropertyName("done")] public bool Done { get; set; }
    }

    private class OllamaGenerateRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
        [JsonPropertyName("stream")] public bool Stream { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Format { get; set; }
        [JsonPropertyName("think")] public bool Think { get; set; } = true;
        [JsonPropertyName("options")] public OllamaOptions Options { get; set; } = new();
    }

    private class OllamaOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.1;
        [JsonPropertyName("repeat_penalty")] public double RepeatPenalty { get; set; } = 1.1;
        [JsonPropertyName("repeat_last_n")] public int RepeatLastN { get; set; } = 64;
        [JsonPropertyName("num_ctx")] public int NumCtx { get; set; } = 8192;
        [JsonPropertyName("num_predict")] public int NumPredict { get; set; } = -1;
    }

    private class OllamaGenerateResponse
    {
        [JsonPropertyName("response")] public string Response { get; set; } = "";
    }
}