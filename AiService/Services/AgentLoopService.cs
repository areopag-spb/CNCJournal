using AiService.Models;
using System.Text.Json;

namespace AiService.Services;

/// <summary>
/// Агентский цикл дневного анализа (стадия 1: каркас).
///
/// Правила цикла (согласованы как требования точности, а не скорости):
///  • очередь Ollama занимается на КАЖДЫЙ раунд отдельно (см. OllamaService.ChatAsync) —
///    между раундами её держат другие клиенты, голодания нет;
///  • максимум MaxIterations раундов (Agent:MaxIterations, дефолт 4);
///  • общий таймаут цикла (Agent:TimeoutSeconds, дефолт 180);
///  • любой сбой/лимит/пустота → FellBack: контроллер идёт штатным путём v22,
///    поведение прод-анализа не меняется ни на байт.
/// Включается флагом Agent:Enabled (дефолт false). Пока флаг выключен, этот класс
/// в проде не вызывается вообще — существует для тестов и стадии 2 (A/B).
/// </summary>
public class AgentLoopService(
    IAgentChatClient chat,
    PromptBuilder prompts,
    IConfiguration config,
    ILogger<AgentLoopService> logger,
    IAgentDataSource dataSource)
{
    public bool Enabled => config.GetValue("Agent:Enabled", false);
    public int MaxIterations => Math.Clamp(config.GetValue("Agent:MaxIterations", 4), 1, 10);
    public TimeSpan Timeout => TimeSpan.FromSeconds(
        Math.Clamp(config.GetValue("Agent:TimeoutSeconds", 180), 10, 900));
    /// <summary>
    /// Температура агентского цикла (Agent:Temperature, дефолт 0.0 — жадное декодирование,
    /// практически детерминированный ответ на том же входе; 0.1 как в штатном пути —
    /// для экспериментов со стохастикой).
    /// </summary>
    public double Temperature => Math.Clamp(config.GetValue("Agent:Temperature", 0.0), 0.0, 1.0);

    /// <summary>
    /// Режим рассуждений модели (Agent:Thinking): "off" (дефолт), "on" или "auto".
    /// Замер 24.09.2026: on чинит семантику (SKT21 22.09: bare-причина признана
    /// достаточной), но ×4 время (25–33с vs 4–9с) и добавляет сомнения в
    /// самодостаточных причинах. "auto": думать, только когда вердикт решает модель
    /// (нет hard-эскалации) — за thinking не платим, когда ответ предрешён кодом.
    /// </summary>
    public string ThinkingMode =>
        (config.GetValue("Agent:Thinking", "off") ?? "off").Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "on" => "on",
            "auto" => "auto",
            _ => "off",
        };

    public bool Thinking => config.GetValue("Agent:Thinking", false);

    /// <summary>
    /// Второе мнение (Agent:VerifyFalse, дефолт false): если базовый вердикт False,
    /// два дешёвых верифаера (без thinking, T=VerifyTemperature) независимо решают
    /// по ГОТОВОМУ контексту (данные дня + результаты tools уже в messages —
    /// повторный сбор не нужен, это и есть экономия против 3 полных прогонов).
    /// Fail-safe асимметрия: True базового не перепроверяем (лишняя эскалация —
    /// приемлемо, её разберёт человек), False — перепроверяем (пропуск критичен).
    /// Разнообразие даёт только T>0: при T=0 все голоса совпадут всегда.
    /// </summary>
    public bool VerifyFalse => config.GetValue("Agent:VerifyFalse", false);
    public double VerifyTemperature => Math.Clamp(
        config.GetValue("Agent:VerifyTemperature", 0.7), 0.0, 2.0);
    public int VerifyRounds => Math.Clamp(config.GetValue("Agent:VerifyRounds", 2), 1, 3);

    public async Task<AgentLoopOutcome> AnalyzeAsync(
        AnalyzeRequest request,
        HardRuleResult hardRules,
        ShiftReportRuleResult shiftRules,
        CancellationToken ct,
        IProgress<string>? progress = null,
        IProgress<string>? thinkingProgress = null)
    {
        PromptBuildResult prompt;
        try
        {
            prompt = prompts.BuildAgentSystem();
        }
        catch (Exception ex)
        {
            // Нет скелета промпта — агентский путь невозможен, молча на старый.
            return new AgentLoopOutcome(null, new AgentTrace { FellBack = true, FallbackReason = $"Нет промпта: {ex.Message}" });
        }

        var trace = new AgentTrace { PromptVersion = prompt.Version };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        // Soft-правила — в систему, только если есть soft-сигналы (как в штатном Build).
        var system = prompt.Prompt;
        var softPrompt = prompts.GetSoftPrompt(hardRules.SoftSignals);
        if (!string.IsNullOrWhiteSpace(softPrompt))
            system += "\n" + softPrompt;

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = system },
            new() { Role = "user", Content = AgentUserMessage(request, hardRules, shiftRules) },
        };

        try
        {
            for (var i = 1; i <= MaxIterations; i++)
            {
                trace.IterationsUsed = i;
                progress?.Report($"── Раунд {i} ──");
                // Думаем, только когда вердикт решает модель: при hard-эскалации
                // ответ предрешён кодом, thinking лишь жжёт очередь (см. ThinkingMode).
                var think = ThinkingMode == "on"
                    || (ThinkingMode == "auto" && !hardRules.MustEscalate && !shiftRules.MustEscalate);
                // format json только в последнем допустимом раунде нет — финал парсится
                // штатным ParseResponse контроллера (терпит markdown/преамбулы), поэтому null.
                var round = await chat.ChatAsync(
                    messages, AgentTools.Definitions, format: null,
                    think: think, cts.Token, model: request.Model, temperature: Temperature,
                    thinkingProgress: thinkingProgress);
                trace.ThinkingChars += round.Thinking?.Length ?? 0;
                AppendExcerpt(trace, i, round.Thinking);

                if (round.ToolCalls.Count == 0)
                {
                    if (string.IsNullOrWhiteSpace(round.Content))
                        return new AgentLoopOutcome(null, Fail(trace, "Пустой финал без tool_calls"));
                    progress?.Report("✓ Вердикт получен.");
                    var outcome = new AgentLoopOutcome(round.Content, trace);
                    trace.BaseRequiresReview = TryGetRequiresReview(round.Content);
                    await CollectSecondOpinionsAsync(
                        request, messages, round.Content, outcome, progress, cts.Token);
                    return outcome;
                }

                messages.Add(new ChatMessage { Role = "assistant", Content = round.Content, ToolCalls = round.ToolCalls });

                foreach (var call in round.ToolCalls)
                {
                    progress?.Report("→ " + DescribeCall(call.Name, call.Arguments));
                    var result = await AgentTools.Execute(
                        request, dataSource, call.Name, call.Arguments, cts.Token);
                    trace.Rounds.Add(new AgentToolRound(call.Name, call.Arguments, result.Length));
                    messages.Add(new ChatMessage { Role = "tool", Content = result });
                    logger.LogInformation(
                        "Агент {Machine} {Date}: tool {Tool} ({ArgsLen} симв. аргументов → {ResLen} симв.)",
                        request.Machine, request.ShiftDate, call.Name,
                        call.Arguments.Length, result.Length);
                }
            }

            return new AgentLoopOutcome(null, Fail(trace, $"Лимит итераций ({MaxIterations})"));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new AgentLoopOutcome(null, Fail(trace, "Таймаут агентского цикла"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Агент {Machine} {Date}: сбой цикла", request.Machine, request.ShiftDate);
            return new AgentLoopOutcome(null, Fail(trace, $"Сбой: {ex.Message}"));
        }
    }

    private static AgentTrace Fail(AgentTrace trace, string reason)
    {
        trace.FellBack = true;
        trace.FallbackReason = reason;
        return trace;
    }

    private const string VerifierPrompt =
        "Ты — второй проверяющий (СГТ-1). Выше: данные суток, результаты запросов "
        + "к данным и вердикт первого аналитика. Независимо реши: требует ли день "
        + "ручного разбора технологами. Лучше эскалировать зря, чем пропустить "
        + "проблему. Ответь СТРОГО JSON без markdown: "
        + "{\"requires_review\": true или false, \"confidence\": число 0.0-1.0, "
        + "\"signals\": [\"необъяснённые проблемы\"], \"explanation\": \"общий вывод\"}";

    /// <summary>
    /// Вторые мнения по готовому контексту (см. VerifyFalse). Сбои/таймауты отдельных
    /// голосов гасятся здесь: страдает только глубина проверки, базовый вердикт цел.
    /// </summary>
    private async Task CollectSecondOpinionsAsync(
        AnalyzeRequest request,
        List<ChatMessage> messages,
        string baseJson,
        AgentLoopOutcome outcome,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!VerifyFalse) return;
        if (TryGetRequiresReview(baseJson) != false) return;

        var verifyMessages = new List<ChatMessage>(messages)
        {
            new() { Role = "assistant", Content = baseJson },
            new() { Role = "user", Content = VerifierPrompt },
        };

        for (var v = 1; v <= VerifyRounds; v++)
        {
            progress?.Report($"── Второе мнение {v} ──");
            try
            {
                var vr = await chat.ChatAsync(verifyMessages, [],
                    format: "json", think: false, ct,
                    model: request.Model, temperature: VerifyTemperature);
                if (TryGetRequiresReview(vr.Content) == true
                    && !string.IsNullOrWhiteSpace(vr.Content))
                {
                    outcome.VerifierJsons.Add(vr.Content);
                    logger.LogInformation(
                        "Агент {Machine} {Date}: второе мнение {V} — за эскалацию",
                        request.Machine, request.ShiftDate, v);
                }
            }
            catch (OperationCanceledException)
            {
                break; // таймаут/отмена — базовый вердикт уже есть, выходим тихо
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Агент {Machine} {Date}: сбой второго мнения {V}",
                    request.Machine, request.ShiftDate, v);
                break;
            }
        }
    }

    /// <summary> Терпимое чтение requires_review (зеркало ExtractJson/ParseResponse). </summary>
    private static bool? TryGetRequiresReview(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var text = raw.Trim();
            var thinkEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (thinkEnd >= 0)
                text = text[(thinkEnd + "</think>".Length)..].Trim();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (doc.RootElement.TryGetProperty("requires_review", out var rr)
                && (rr.ValueKind == JsonValueKind.True || rr.ValueKind == JsonValueKind.False))
                return rr.ValueKind == JsonValueKind.True;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string s, int max)
    {
        var t = s.Trim().Replace("\r", "").Replace("\n", " / ");
        return t.Length <= max ? t : t[..max] + "…";
    }

    /// <summary>
    /// Детали дня одним JSON-объектом {"parts":[...]}. Числа — числами, статусы —
    /// флагами: модель ничего не парсит из строк. Ключи — слова промпта, а не
    /// C#-модели («естьЗаказ», «кпдНаладки», «планНаладки»): модель оперирует
    /// теми же терминами в рассуждениях и вердикте. естьЗаказ считает C# по
    /// правилу HardRule (непустой и не «Без М/Л»); КПД — доля или null +
    /// явные флаги наладкиНеБыло/изготовленияНеБыло; несерийный —
    /// изготовлениеНеОценивается=true, числа изготовления скрыты. Кириллица —
    /// как есть (UnsafeRelaxedJsonEscaping): \u-последовательности жрут токены.
    /// Тексты — только непустые (комментарии/причины/сигналы).
    /// </summary>
    public static string RenderPartsJson(AnalyzeRequest request)
    {
        var list = new List<Dictionary<string, object?>>(request.Parts.Count);
        foreach (var p in request.Parts)
        {
            var noSetup = p.NoSetupHappened || !p.SetupRatio.HasValue;
            var nonSerial = request.IsSerialMachine == false;
            var noProd = nonSerial || p.NoProductionHappened || !p.ProductionRatio.HasValue;
            var d = new Dictionary<string, object?>
            {
                ["деталь"] = p.PartName,
                ["уст"] = p.Setup,
                ["заказ"] = p.Order,
                ["естьЗаказ"] = AgentTools.HasOrder(p.Order),
                ["кпдНаладки"] = noSetup ? null : Math.Round(p.SetupRatio!.Value, 4),
                ["наладкиНеБыло"] = noSetup,
                ["кпдИзготовления"] = noProd ? null : Math.Round(p.ProductionRatio!.Value, 4),
                ["изготовленияНеБыло"] = noProd,
                ["готовоШт"] = p.FinishedCount,
                ["планНаладки"] = p.SetupTimePlan,
                ["фактНаладки"] = p.SetupTimeFact,
                ["норматив"] = p.SingleProductionTimePlan,
                ["машВремя"] = p.MachiningTime,
                // Частичная наладка (куски одной наладки, идут через механизм простоев).
                // Числом и по строке: без него модель не видела, КАКАЯ именно строка несёт
                // частичную наладку, и при двух строках с одинаковым ключом имя/установка/
                // заказ ни на что не указывали (кейс 24.09.2026, SKT21 «Гайка нажимная…»:
                // частичная 3 мин во второй строке, модель предложила exclude по первой).
                ["частичнаяНаладкиМин"] = p.PartialSetup,
            };
            if (nonSerial) d["изготовлениеНеОценивается"] = true;
            AddText(d, "причинаОтклоненияВНаладке", p.MasterSetupComment, 200);
            AddText(d, "причинаОтклоненияВИзготовлении", p.MasterMachiningComment, 200);
            AddText(d, "комментарийОператора", p.OperatorComment, 300);
            AddText(d, "мастерНаладка", p.MasterSetupDetail, 200);
            AddText(d, "мастерИзготовление", p.MasterMachiningDetail, 200);
            if (string.IsNullOrWhiteSpace(p.MasterSetupDetail)
                && string.IsNullOrWhiteSpace(p.MasterMachiningDetail))
                AddText(d, "мастерАрхив", p.MasterComment, 200);
            AddText(d, "указанныеПростои", p.SpecifiedDowntimesList, 200);
            AddText(d, "комментарийКПростоям", p.SpecifiedDowntimesComment, 200);
            if (p.Signals.Count > 0) d["сигналы"] = p.Signals;
            list.Add(d);
        }
        return JsonSerializer.Serialize(new { parts = list }, AgentTools.JsonOptions);
    }

    private static void AddText(Dictionary<string, object?> d, string key, string value, int max)
    {
        if (!string.IsNullOrWhiteSpace(value)) d[key] = Clip(value, max);
    }

    /// <summary> Первые символы рассуждений по раундам (см. AgentTrace.ThinkingExcerpt). </summary>
    public const int ThinkingExcerptCap = 3000;

    private static void AppendExcerpt(AgentTrace trace, int round, string? thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking)) return;
        var have = trace.ThinkingExcerpt?.Length ?? 0;
        if (have >= ThinkingExcerptCap) return;
        var chunk = $"\n[раунд {round}]\n{thinking.Trim()}";
        trace.ThinkingExcerpt = ((trace.ThinkingExcerpt ?? "") + chunk)[..Math.Min(
            (trace.ThinkingExcerpt?.Length ?? 0) + chunk.Length, ThinkingExcerptCap)];
    }

    /// <summary>
    /// Человекочитаемое описание вызова tool для прогресса в UI
    /// («что агент делает прямо сейчас»). Аргументы — JSON, разбираем терпимо.
    /// </summary>
    public static string DescribeCall(string name, string argsJson)
    {
        string Arg(string field)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(field, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()
                    : "";
            }
            catch (JsonException) { return ""; }
        }

        return name switch
        {
            "get_reason_semantics" => $"Проверяет причину «{Arg("reason")}» ({Arg("category")})",
            "get_part_history" => $"Смотрит историю детали «{Arg("partName")}»",
            "get_past_decisions" => "Смотрит прошлые решения аналитиков",
            "get_normatives" => $"Сверяет нормативы детали «{Arg("partName")}»",
            "get_shift_report" => "Проверяет отчёты мастера",
            "get_operator_info" => $"Ищет оператора «{Arg("query")}»",
            _ => $"Вызывает {name}",
        };
    }

    /// <summary>
    /// Факты дня: станок/дата/сигналы + детали JSON + СИСТЕМНЫЕ hard/soft-сигналы
    /// как неотменяемые факты.
    /// Без этого блока агент не видит причин эскалации, найденных кодом (кейс A/B:
    /// Mazak QTS200ML 2026-09-21 — «Некорректные нормативы» прошли мимо модели).
    /// Детали — JSON, а не проза: модель не должна выпарсивать «Без М/Л»,
    /// «КПД=б/н» и проценты из буллетов (кейс QTS350 2026-09-24: «plan=0
    /// при наличии заказа» для Без М/Л-строки). Ключевое вычисляет C#,
    /// схема описана в скелете (═══ ДАННЫЕ ═══).
    /// </summary>
    private static string AgentUserMessage(
        AnalyzeRequest request, HardRuleResult hardRules, ShiftReportRuleResult shiftRules)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Станок: {request.Machine}" +
                      (request.IsSerialMachine == false
                           ? " [несерийный — изготовление не оценивается: ни КПД, ни машинное время]"
                           : ""));
        sb.AppendLine($"Дата: {request.ShiftDate}");
        foreach (var s in request.Signals)
            sb.AppendLine($"Сигнал дня: {s}");
        sb.AppendLine(RenderPartsJson(request));

        if (hardRules.HardSignals.Count > 0)
        {
            sb.AppendLine("СИСТЕМА УЖЕ РЕШИЛА: requires_review=true (неотменяемо).");
            foreach (var h in hardRules.HardSignals)
                sb.AppendLine($" • HARD: {h}");
        }
        if (shiftRules.HardSignals.Count > 0)
        {
            foreach (var h in shiftRules.HardSignals)
                sb.AppendLine($" • HARD (отчёт мастера): {h}");
        }
        if (hardRules.SoftSignals.Count > 0)
        {
            sb.AppendLine("Soft-сигналы (понижаются только причинно-релевантным объяснением, дословной строкой в downgraded_signals):");
            foreach (var s in hardRules.SoftSignals)
                sb.AppendLine($" • SOFT: {s}");
        }
        sb.Append(PromptBuilder.RenderShiftReports(request, ShiftReportRuleEvaluator.ClearShiftKeys(ShiftReportRuleEvaluator.AgentPreverdicts(request, shiftRules))));
        // Предрешение системы по отчётам: механику исключений модель нарушает
        // детерминированно (пилот 24.09.2026), поэтому код сообщает итог как факт.
        // Строки СИСТЕМА модель не перепроверяет; судит только ТВОЯ ПРОВЕРКА.
        // Блоков Clear-смен в данных уже нет (скрыты выше — судить нечего
        // и не по чему); shift_report_issues по ним пуст всегда, без исключений.
        sb.AppendLine("Проверка отчёта (предрешение системы):");
        foreach (var pv in ShiftReportRuleEvaluator.AgentPreverdicts(request, shiftRules))
            sb.AppendLine(pv.NeedsModel
                ? $" • Смена {pv.Shift} — ТВОЯ ПРОВЕРКА: {pv.Note}"
                : $" • Смена {pv.Shift} — СИСТЕМА: вопросов нет ({pv.Note})");
        sb.AppendLine("Верни JSON вердикта. Фактологию (смысл причин, историю, отчёт мастера) бери ТОЛЬКО через tools.");
        return sb.ToString();
    }
}
