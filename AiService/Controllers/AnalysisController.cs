using AiService.Models;
using AiService.Services;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AiService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AnalysisController(OllamaService ollama, PromptBuilder promptBuilder, RequestLog requestLog, AgentLoopService agent, ILogger<AnalysisController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions _camelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [HttpGet("health")]
    public async Task<ActionResult> Health()
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "(unknown)";
        logger.LogDebug("Health check from {IP}", ip);

        var ollamaOk = await ollama.CheckHealthAsync();
        logger.LogDebug("Health: IP={IP}, ollama={Ollama}", ip, ollamaOk);

        return Ok(new { status = "ok", ollama = ollamaOk });
    }

    [HttpGet("queue-length")]
    public ActionResult QueueLength()
    {
        return Ok(new { queueLength = ollama.QueueLength });
    }

    [HttpPost]
    public async Task<ActionResult<AnalyzeResponse>> Analyze(
        [FromBody] AnalyzeRequest request,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? promptVersion = null;
        try
        {
            RequestShaper.Shape(request);
            var mastering = MasteringAutoApprover.Apply(request);
            if (mastering.RemovedSignals.Count > 0)
                logger.LogInformation(
                    "Освоение подтверждено детерминированно ({Machine} {Date}), сняты сигналы: {Signals}",
                    request.Machine, request.ShiftDate, string.Join(" | ", mastering.RemovedSignals));

            var hardRules = HardRuleEvaluator.Evaluate(request);
            var shiftRules = ShiftReportRuleEvaluator.Evaluate(request);

            var promptBuild = promptBuilder.Build(request, hardRules, shiftRules);
            promptVersion = promptBuild.Version;
            var thinkCapture = new StringBuilder();

            // Агентский контур (стадия 1): подменяется ТОЛЬКО источник сырого ответа
            // модели. Весь downstream (парсинг, фильтры, склейка) — общий, без изменений.
            // Agent:Enabled=false (дефолт) → всегда штатный путь, поведение 1:1 как раньше.
            string raw;
            AgentTrace? agentTrace = null;
            AgentLoopOutcome? agentOutcome = null;
            var useAgent = request.UseAgent && agent.Enabled;
            if (request.UseAgent && !agent.Enabled)
                logger.LogDebug("Клиент просил агента ({Machine} {Date}), но Agent:Enabled=false — штатный путь",
                    request.Machine, request.ShiftDate);
            if (useAgent)
            {
                var outcome = await agent.AnalyzeAsync(request, hardRules, shiftRules, ct);
                agentTrace = outcome.Trace;
                agentOutcome = outcome;
                if (!outcome.Trace.FellBack && !string.IsNullOrWhiteSpace(outcome.FinalJson))
                {
                    raw = outcome.FinalJson;
                    promptVersion = outcome.Trace.PromptVersion ?? promptVersion;
                    logger.LogInformation(
                        "Агент {Machine} {Date}: финал за {N} итераций, tools {T}",
                        request.Machine, request.ShiftDate,
                        outcome.Trace.IterationsUsed, outcome.Trace.Rounds.Count);
                }
                else
                {
                    logger.LogInformation(
                        "Агент {Machine} {Date}: fallback ({Reason}), штатный путь",
                        request.Machine, request.ShiftDate, outcome.Trace.FallbackReason);
                    (raw, _) = await ollama.GenerateAsync(promptBuild.Prompt, think: false, thinkingProgress: null, ct: ct, model: request.Model);
                }
            }
            else
            {
                (raw, _) = await ollama.GenerateAsync(promptBuild.Prompt, think: false, thinkingProgress: null, ct: ct, model: request.Model);
            }
            var llmResult = ParseResponse(raw);
            if (agentOutcome is { Trace.FellBack: false })
                agentOutcome.Trace.BaseSignals = [.. llmResult.Signals];
            MergeVerifierOpinions(agentOutcome, llmResult, request);
            // Отчётные эхо из общего signals — в shift-вопросы, до пост-фильтра.
            var (dataSignals, shiftEchoes) = ShiftReportRuleEvaluator.SplitShiftEchoes(llmResult.Signals);
            llmResult.Signals = dataSignals;
            var modelShiftIssues = ShiftReportRuleEvaluator.CleanModelIssues(
                llmResult.ShiftReportIssues.Concat(shiftEchoes));
            // Вердикт «релевантно» — одобрение отчёта, ошибочно положенное в проблемы.
            (modelShiftIssues, var droppedRelevanceVerdicts) =
                ShiftReportRuleEvaluator.DropRelevanceVerdicts(modelShiftIssues);
            // Жалоба на связь простоя с КПД — механизм учёта их не связывает никогда.
            (modelShiftIssues, var droppedKpdLink) =
                ShiftReportRuleEvaluator.DropKpdDowntimeLinkComplaints(modelShiftIssues);
            // Требования комментария при самодостаточной причине простоя режутся
            // здесь же — до всех downstream-решений (reset, requiresReview, merge).
            (modelShiftIssues, var droppedCommentDemands) =
                ShiftReportRuleEvaluator.DropSelfSufficientCommentDemands(request, modelShiftIssues);
            (modelShiftIssues, var droppedReasonDemands) =
                ShiftReportRuleEvaluator.DropUnneededReasonDemands(request, modelShiftIssues);
            // Выдумки про отчёт, которого нет («устарел», «мастер не указан»),
            // режутся здесь же — триггер ReportExists тот же, что у иконок главной.
            (modelShiftIssues, var droppedNoReport) =
                ShiftReportRuleEvaluator.DropIssuesWithoutReport(request, modelShiftIssues);
            // Ложь про мастера, который в отчёте указан («мастер не указан»,
            // «оператор <ФИО мастера> отсутствует в системе» — мастера нет
            // в cnc_operators по построению; кейс Rontek HTC420 №1 28.09).
            (modelShiftIssues, var droppedMasterClaims) =
                ShiftReportRuleEvaluator.DropFalseMasterClaims(request, modelShiftIssues);
            // «Работал на другом станке» — релевантная причина простоя,
            // придирки к её детализации/содержанию снимаются.
            (modelShiftIssues, var droppedTwoMachine) =
                ShiftReportRuleEvaluator.DropTwoMachineRelevanceComplaints(request, modelShiftIssues);
            // «Кто отсутствовал / почему / где работал» при непустом содержательном
            // комментарии к «Отсутствию оператора» — комментарий уже объясняет простой.
            (modelShiftIssues, var droppedAbsenceWho) =
                ShiftReportRuleEvaluator.DropAbsenceWhoDemands(request, modelShiftIssues);
            // Фактически неверное «причина неизвестна» при причине из закрытого
            // списка — сверка фактом, а не мнением (пилот 24.09.2026 SKT21 22.09).
            (modelShiftIssues, var droppedUnknownReasons) =
                ShiftReportRuleEvaluator.DropUnknownReasonComplaints(request, modelShiftIssues);
            // Вопрос без привязки к смене («Смена День/Ночь:» буквально) —
            // отнести не к чему, аналитику с ним делать нечего (там же).
            (modelShiftIssues, var droppedUnscoped) =
                ShiftReportRuleEvaluator.DropUnscopedShiftIssues(modelShiftIssues);
            MergeAutoExcludes(llmResult, mastering.AutoExcludes);
            var consistencyConflict = CheckThinkingConsistency(request, agentTrace?.ThinkingTail ?? agentTrace?.ThinkingExcerpt ?? thinkCapture.ToString(), llmResult);
            var notDowngraded = SoftSignalMatcher.GetNotDowngraded(
                hardRules.SoftSignals, llmResult.DowngradedSignals);

            var (filteredSignals, reset, removedSignals) = FalsePositiveFilter.Apply(
                request, hardRules, notDowngraded, llmResult.Signals);
            llmResult.Signals = filteredSignals;
            if (consistencyConflict)
                llmResult.Signals.Add(ThinkingConsistency.ConflictSignal);
            removedSignals.AddRange(droppedRelevanceVerdicts
                .Select(d => (d, "вердикт «релевантно» — одобрение, а не проблема")));
            removedSignals.AddRange(droppedKpdLink
                .Select(d => (d, "простой и КПД не связаны — жалоба неверна")));
            removedSignals.AddRange(droppedCommentDemands
                .Select(d => (d, "причина простоя самодостаточна, комментарий не требуется")));
            removedSignals.AddRange(droppedReasonDemands
                .Select(d => (d, "причина простоя не требуется (порог 5%) или уже указана")));
            removedSignals.AddRange(droppedNoReport
                .Select(d => (d, "отчёта нет — проверять нечего")));
            removedSignals.AddRange(droppedMasterClaims
                .Select(d => (d, "мастер отчёта — не оператор; отсутствие в cnc_operators не сигнал")));
            removedSignals.AddRange(droppedTwoMachine
                .Select(d => (d, "работа на другом станке — релевантная причина простоя")));
            removedSignals.AddRange(droppedAbsenceWho
                .Select(d => (d, "комментарий к «Отсутствию оператора» объясняет простой — личность не требуется")));
            removedSignals.AddRange(droppedUnknownReasons
                .Select(d => (d, "причина есть в закрытом списке — жалоба неверна")));
            removedSignals.AddRange(droppedUnscoped
                .Select(d => (d, "вопрос без привязки к смене — отнести не к чему")));
            LogFilteredSignals(request, removedSignals, reset);

            // Сброс пост-фильтра не действует при вопросах к отчёту мастера:
            // детерминированные hard и несогласия модели (S1) фильтр не рассматривает.
            // Плюс «пустой вердикт»: requires_review=true, но модель не назвала НИ ОДНОГО
            // основания (сигналы и shift-вопросы пусты, hard/soft/клиентских нет) —
            // аналитику разбирать нечего (пилот 24.09, thinking-прогон SKT21 22.09).
            // Сбой парсинга (HasError) под сброс не попадает — он эскалирует всегда.
            var emptyModelVerdict = !llmResult.HasError && llmResult.RequiresReview
                && llmResult.Signals.Count == 0 && modelShiftIssues.Count == 0
                && !hardRules.MustEscalate && !shiftRules.MustEscalate && notDowngraded.Count == 0
                && request.Signals.Count == 0 && !request.Parts.Any(p => p.Signals.Count > 0)
                && !consistencyConflict;
            if (emptyModelVerdict)
                logger.LogInformation(
                    "Пустой вердикт модели ({Machine} {Date}): requires_review без оснований — сброшен",
                    request.Machine, request.ShiftDate);
            var modelWantedEscalation = llmResult.RequiresReview;
            var resetEffective = (reset || emptyModelVerdict) && !consistencyConflict
                && !shiftRules.MustEscalate && modelShiftIssues.Count == 0;
            if (resetEffective)
            {
                llmResult.RequiresReview = false;
                llmResult.Explanation = "";
                llmResult.SuggestedReason = "";
            }
            // Зеркало: модель заявила необъяснённые проблемы, но вердикт False —
            // принудительно True (fail-safe; пилот 24.09.2026, Goodway 01.06).
            else if (FalsePositiveFilter.IsIncoherentOk(llmResult.RequiresReview, llmResult.Signals))
            {
                logger.LogInformation(
                    "Противоречивый вердикт модели ({Machine} {Date}): signals непуст при requires_review=false — поднят в True",
                    request.Machine, request.ShiftDate);
                llmResult.RequiresReview = true;
            }

            // llmResult.HasError (сбой парсинга/вырожденная генерация модели — см.
            // память ai-analysis-improvement-plan, кейс 2026-06-30 Rontek HTC650M:
            // модель зациклилась на повторе одной фразы, съела весь NumPredict до
            // JSON) ВСЕГДА форсирует эскалацию — сбой системы должен привлечь
            // внимание аналитика, а не молча выглядеть как «Всё в порядке».
            var requiresReview = resetEffective ? false
                : hardRules.MustEscalate || shiftRules.MustEscalate || modelShiftIssues.Count > 0 || notDowngraded.Count > 0
                    || llmResult.RequiresReview || llmResult.HasError;

            logger.LogDebug(
                "ДИАГНОСТИКА {Machine} {Date}: " +
                "hardRules.MustEscalate={MustEscalate}, hardRules.HardSignals=[{HardSignals}], " +
                "hardRules.SoftSignals=[{SoftSignals}], notDowngraded=[{NotDowngraded}], " +
                "shiftRules.HardSignals=[{ShiftHard}], shiftRules.SoftSignals=[{ShiftSoft}], " +
                "llmResult.RequiresReview={LlmRR}, llmResult.HasError={LlmErr}, llmResult.Error={LlmErrMsg}, " +
                "llmResult.DowngradedSignals=[{LlmDowngraded}], llmResult.ExcludeFromReports={ExcludeCount} [{ExcludeList}] → итоговый requiresReview={Final}",
                request.Machine, request.ShiftDate,
                hardRules.MustEscalate, string.Join(" | ", hardRules.HardSignals),
                string.Join(" | ", hardRules.SoftSignals), string.Join(" | ", notDowngraded),
                string.Join(" | ", shiftRules.HardSignals), string.Join(" | ", shiftRules.SoftSignals),
                llmResult.RequiresReview, llmResult.HasError, llmResult.Error ?? "(нет)",
                string.Join(" | ", llmResult.DowngradedSignals),
                llmResult.SuggestExcludeFromReports.Count, string.Join(" | ", llmResult.SuggestExcludeFromReports),
                requiresReview);

            var result = new AnalyzeResponse
            {
                RequiresReview = requiresReview,
                Confidence = ShiftReportRuleEvaluator.ComputeConfidence(
                    hardRules.MustEscalate, shiftRules.MustEscalate, llmResult.HasError, llmResult.Confidence),
                Explanation = EnsureExplanation(llmResult, hardRules, shiftRules, modelShiftIssues, notDowngraded),
                SuggestedReason = string.IsNullOrWhiteSpace(llmResult.SuggestedReason)
                    ? FallbackReason(hardRules, shiftRules, modelShiftIssues, notDowngraded, llmResult.HasError)
                    : llmResult.SuggestedReason,
                Error = llmResult.Error,
                SuggestExcludeFromReports = FilterExcludeSuggestions(llmResult.SuggestExcludeFromReports, request),
                DowngradedSignals = llmResult.DowngradedSignals,
                PromptVersion = promptVersion,
            };

            result.ShiftReportIssues = ShiftReportRuleEvaluator.MergeIssues(shiftRules, modelShiftIssues);
            result.ShiftReportSummary = ShiftReportRuleEvaluator.Summarize(request, shiftRules);
            // В общий Signals — только выжившие модельные вопросы: срезанные дедупом
            // дубли иначе остаются видимыми диалогу (Except их не находит).
            var shiftSurvivors = ShiftReportRuleEvaluator.ModelSurvivors(shiftRules, result.ShiftReportIssues);

            result.Signals = [.. llmResult.Signals
                .Concat(request.Signals)
                .Concat(CollectPartSignals(request, hardRules, notDowngraded))
                .Concat(hardRules.HardSignals)
                .Concat(shiftRules.HardSignals)
                .Concat(shiftRules.SoftSignals)
                .Concat(shiftSurvivors)
                .Concat(notDowngraded)
                .Distinct()];

            // Протухшее объяснение: модель описала конкретную деталь, чей сигнал
            // срезан пост-фильтрами, а выжившие сигналы — про другое (кейс 08.10.2026
            // QTS200ML 20.09: explanation про «Корпус верхний», чей сигнал «Другое
            // с детализацией» срезан как объяснённый, выжил только hard по «Крышке»).
            // Вердикт не трогаем (его держат выжившие сигналы), слова модели
            // сбрасываем на базу EnsureExplanation, причину — на детерминированный
            // fallback: претензия опровергнута, повторять её аналитику — враньё.
            if (ExplanationHygiene.ReferencesOnlyRemovedParts(
                result.Explanation, result.Signals,
                [.. removedSignals.Select(r => r.Signal)],
                [.. request.Parts.Select(p => p.PartName)]))
            {
                logger.LogInformation(
                    "Протухшее объяснение ({Machine} {Date}): ссылается только на срезанные сигналы — сброшено на базу",
                    request.Machine, request.ShiftDate);
                llmResult.Explanation = "";
                llmResult.SuggestedReason = "";
                result.Explanation = EnsureExplanation(llmResult, hardRules, shiftRules, modelShiftIssues, notDowngraded);
                result.SuggestedReason = FallbackReason(hardRules, shiftRules, modelShiftIssues, notDowngraded, llmResult.HasError);
            }

            // Вердикт модели зажигает «Данные», только если она назвала проблемы
            // по данным (почищенные сигналы непусты). Формула эскалации не меняется.
            var llmDataCaused = ShiftReportRuleEvaluator.LlmDataCaused(llmResult.RequiresReview, llmResult.Signals);
            var (dataCaused, shiftCaused) = ShiftReportRuleEvaluator.EscalationCauses(
                hardRules, notDowngraded, llmDataCaused, llmResult.HasError,
                resetEffective, shiftRules, modelShiftIssues);
            result.EscalatedByData = dataCaused;
            ApplyAutoClosePolicy(request, result, resetEffective && modelWantedEscalation, agentTrace, llmResult.HasError,
                request.UseAgent && !agent.Enabled);
            result.EscalatedByShiftReport = shiftCaused;

            if (ShiftReportRuleEvaluator.ShiftOnlyExplanation(dataCaused, shiftCaused) is { } cleanExplanation)
                result.Explanation = cleanExplanation;

            // Чистый отчёт мастера фиксируем прямо в объяснении: блок «Отчёт мастера»
            // в UI виден всегда, и пакетное окно (там только Explanation) тоже показывает
            // факт проверки. Только когда клиент реально прислал отчёты — иначе враньё.
            if (result.ShiftReportIssues.Count == 0 && request.ShiftReports.Count > 0)
                result.Explanation = (result.Explanation + " Отчёт мастера проверен, замечаний нет.").Trim();

            result.FlaggedParts = CollectFlaggedPartKeys(hardRules, notDowngraded, request, llmResult.Signals);

            logger.LogInformation(
                "Анализ: {Machine} {Date} → RequiresReview={R} (hard={H}, softNotDowngraded={S}, shiftHard={SH}, shiftSoft={SS}), Confidence={C:F2}, FlaggedParts={F}",
                request.Machine, request.ShiftDate, result.RequiresReview,
                hardRules.HardSignals.Count, notDowngraded.Count,
                shiftRules.HardSignals.Count, shiftRules.SoftSignals.Count,
                result.Confidence, result.FlaggedParts.Count);

            if (hardRules.SoftSignals.Count > 0)
            {
                logger.LogInformation(
                    "Soft-сигналы: {Total} всего, {Downgraded} понижено моделью: {List}",
                    hardRules.SoftSignals.Count,
                    hardRules.SoftSignals.Count - notDowngraded.Count,
                    string.Join(" | ", llmResult.DowngradedSignals));
            }

            if (hardRules.MustEscalate && result.SuggestExcludeFromReports.Count > 0)
            {
                var hardPartNames = hardRules.HardSignals
                    .Select(s =>
                    {
                        // Сигналы вида "[PartName] ..."
                        if (s.StartsWith('['))
                        {
                            var end = s.IndexOf(']');
                            return end > 1 ? s[1..end] : null;
                        }
                        return null;
                    })
                    .Where(n => n != null)
                    .ToHashSet()!;

                result.SuggestExcludeFromReports = [.. result.SuggestExcludeFromReports
                    .Where(entry =>
                    {
                        var name = entry.Split('§')[0];
                        return !hardPartNames.Contains(name);
                    })];
            }

            ClassifyAutoExcludes(request, result);

            await requestLog.WriteAsync(request, result, "analyze", agentTrace);
            return Ok(result);
        }
        catch (DegenerateGenerationException ex)
        {
            // Петля в рассуждении (в т.ч. после ретрая): fail-safe эскалация,
            // как при HasError — сбой системы должен привлечь внимание аналитика.
            logger.LogWarning(ex, "Вырожденная генерация {Machine} {Date}", request.Machine, request.ShiftDate);
            var degResult = DegenerateEscalation(ex, promptVersion);
            await requestLog.WriteAsync(request, degResult, "analyze");
            return Ok(degResult);
        }
        catch (TaskCanceledException ex)
        {
            await requestLog.WriteFailureAsync(request.Machine, request.ShiftDate, "analyze", request.Model, think: false, sw.ElapsedMilliseconds, ex);
            return StatusCode(504, new AnalyzeResponse { Error = "Ollama не ответила за отведённое время" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при анализе {Machine} {Date}", request.Machine, request.ShiftDate);
            await requestLog.WriteFailureAsync(request.Machine, request.ShiftDate, "analyze", request.Model, think: false, sw.ElapsedMilliseconds, ex);
            return StatusCode(500, new AnalyzeResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Фоновая проверка ОДНОЙ записи сутко-станка: релевантно ли комментарии мастера
    /// объясняют присланные клиентом аномалии. Совещательный контур (remeLog,
    /// фича AiMasterCheck): компактный промпт, без thinking, ответ {ok, remark}.
    /// Никакие пайплайны дневного анализа (hard rules, фильтры) здесь не участвуют.
    /// </summary>
    [HttpPost("verify-part")]
    public async Task<ActionResult<VerifyPartResponse>> VerifyPart(
        [FromBody] VerifyPartRequest request,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (request.Anomalies.Count == 0)
                return Ok(new VerifyPartResponse { Ok = true });

            // Аномалии, целиком покрытые самодостаточными причинами («Освоение»,
            // «Отсутствие/Некорректные нормативов»), не требуют обращения к модели —
            // см. MasteringAutoApprover.IsAnomalyFieldSelfSufficient.
            if (request.Anomalies.All(a => MasteringAutoApprover.IsAnomalyFieldSelfSufficient(a.Field, request.Part)))
            {
                var autoResult = new VerifyPartResponse { Ok = true, PromptVersion = "auto-approved" };
                logger.LogInformation(
                    "Verify-part: {Machine} {Date} «{Part}» → Ok=true (авто, причина самодостаточна)",
                    request.Machine, request.ShiftDate, request.Part.PartName);
                await requestLog.WriteVerifyAsync(request, autoResult);
                return Ok(autoResult);
            }

            // Массовые пустые формулировки с устойчивым вердиктом (M4 «нет норм»,
            // «ТП на 230», «работа с технологом»): модель на них флипает между
            // запусками — см. MasterCheckGuards. Совпадение — вердикт без модели.
            if (MasterCheckGuards.CheckKnownFalsePattern(request.Part, request.Anomalies) is { } guardRemark)
            {
                var guardResult = new VerifyPartResponse { Ok = false, Remark = guardRemark, PromptVersion = "known-pattern" };
                logger.LogInformation(
                    "Verify-part: {Machine} {Date} «{Part}» → Ok=false (гард, известный паттерн): {Remark}",
                    request.Machine, request.ShiftDate, request.Part.PartName, guardRemark);
                await requestLog.WriteVerifyAsync(request, guardResult);
                return Ok(guardResult);
            }

            var promptBuild = promptBuilder.BuildMasterCheck(request);

            // Температура 0.05: подсказка мастеру обязана быть детерминированной —
            // одинаковый вход даёт одинаковый вердикт (дневной анализ остаётся на 0.1).
            var (raw, _) = await ollama.GenerateAsync(
                promptBuild.Prompt, think: false, thinkingProgress: null, ct: ct, model: request.Model,
                temperature: 0.05);

            var result = ParseVerifyResponse(raw);
            result.PromptVersion = promptBuild.Version;

            logger.LogInformation(
                "Verify-part: {Machine} {Date} «{Part}» → Ok={Ok}{Remark}",
                request.Machine, request.ShiftDate, request.Part.PartName, result.Ok,
                result.Ok ? "" : $", remark: {result.Remark}");

            await requestLog.WriteVerifyAsync(request, result);
            return Ok(result);
        }
        catch (TaskCanceledException ex)
        {
            await requestLog.WriteFailureAsync(request.Machine, request.ShiftDate, "verify-part", request.Model, think: false, sw.ElapsedMilliseconds, ex);
            return StatusCode(504, new VerifyPartResponse { Ok = true, Error = "Ollama не ответила за отведённое время" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка verify-part {Machine} {Date}", request.Machine, request.ShiftDate);
            await requestLog.WriteFailureAsync(request.Machine, request.ShiftDate, "verify-part", request.Model, think: false, sw.ElapsedMilliseconds, ex);
            return StatusCode(500, new VerifyPartResponse { Ok = true, Error = ex.Message });
        }
    }

    /// <summary>
    /// Парсинг ответа verify-part. Совещательная безопасность: непарсибельный ответ
    /// или отсутствие поля ok трактуются как Ok=true (+Error) — сбой модели не должен
    /// выглядеть замечанием мастеру.
    /// </summary>
    private static VerifyPartResponse ParseVerifyResponse(string raw)
    {
        var json = ExtractJson(raw);
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ok", out var ok) ||
                (ok.ValueKind != JsonValueKind.True && ok.ValueKind != JsonValueKind.False))
            {
                return new VerifyPartResponse { Ok = true, Error = "Ответ модели без поля ok" };
            }

            var remark = root.TryGetProperty("remark", out var r) ? r.GetString() ?? "" : "";
            return new VerifyPartResponse
            {
                Ok = ok.ValueKind == JsonValueKind.True || string.IsNullOrWhiteSpace(remark),
                Remark = ok.ValueKind == JsonValueKind.True ? "" : remark.Trim(),
            };
        }
        catch (JsonException)
        {
            return new VerifyPartResponse
            {
                Ok = true,
                Error = $"Не удалось распарсить ответ модели: {raw[..Math.Min(200, raw.Length)]}",
            };
        }
    }

    [HttpPost("stream")]
    public async Task AnalyzeWithStream(
    [FromBody] AnalyzeRequest request,
    CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var bufferingFeature = HttpContext.Features
            .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        bufferingFeature?.DisableBuffering();

        async Task Send(string evt, string data)
        {
            await Response.WriteAsync($"event: {evt}\ndata: {data}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        logger.LogInformation("Stream-анализ начат: {Machine} {Date}",
            request.Machine, request.ShiftDate);

        RequestShaper.Shape(request);
        var mastering = MasteringAutoApprover.Apply(request);
        if (mastering.RemovedSignals.Count > 0)
            logger.LogInformation(
                "Освоение подтверждено детерминированно ({Machine} {Date}), сняты сигналы: {Signals}",
                request.Machine, request.ShiftDate, string.Join(" | ", mastering.RemovedSignals));

        var hardRules = HardRuleEvaluator.Evaluate(request);
        var shiftRules = ShiftReportRuleEvaluator.Evaluate(request);
        var promptBuild = promptBuilder.Build(request, hardRules, shiftRules);

        logger.LogDebug("Промпт построен, символов: {Len}", promptBuild.Prompt.Length);
        string? promptVersion = promptBuild.Version;

        // Агентский контур в stream-транспорте: цикл с SSE-событиями "tool"
        // («что агент делает»: раунды и вызовы инструментов). Весь downstream ниже —
        // общий: сырой финал агента парсится и фильтруется как обычный ответ модели.
        // Fallback/отмена/ошибка — в обычный thinking-путь (RunThinkingPathAsync).
        string raw;
        string? thinking;
        AgentTrace? agentTrace = null;
        AgentLoopOutcome? agentOutcome = null;
        var useAgent = request.UseAgent && agent.Enabled;
        if (request.UseAgent && !agent.Enabled)
            logger.LogDebug("Клиент просил агента ({Machine} {Date}), но Agent:Enabled=false — thinking-путь",
                request.Machine, request.ShiftDate);

        if (useAgent)
        {
            var toolChannel = System.Threading.Channels.Channel.CreateBounded<string>(
                new System.Threading.Channels.BoundedChannelOptions(32)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                });
            // Синхронный прогресс: Progress<T> постит в пул потоков, и финальное
            // «Вердикт получен.» терялось (канал закрывался раньше колбэка).
            var toolProgress = new SyncProgress(step => toolChannel.Writer.TryWrite(step));
            var drainToolsTask = Task.Run(async () =>
            {
                await foreach (var step in toolChannel.Reader.ReadAllAsync(ct))
                    await Send("tool", JsonSerializer.Serialize(step));
            }, ct);

            // Живые мысли агента — теми же SSE "thinking", что в thinking-режиме:
            // UI складывает их в панель хода без различий.
            var thinkChannel = System.Threading.Channels.Channel.CreateBounded<string>(
                new System.Threading.Channels.BoundedChannelOptions(128)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                });
            var thinkProgress = new SyncProgress(t => thinkChannel.Writer.TryWrite(t));
            var drainThinkTask = Task.Run(async () =>
            {
                await foreach (var thought in thinkChannel.Reader.ReadAllAsync(ct))
                    await Send("thinking", JsonSerializer.Serialize(thought));
            }, ct);

            AgentLoopOutcome outcome;
            try
            {
                outcome = await agent.AnalyzeAsync(request, hardRules, shiftRules, ct, toolProgress, thinkProgress);
                agentOutcome = outcome;
            }
            catch (OperationCanceledException ex)
            {
                toolChannel.Writer.Complete();
                thinkChannel.Writer.Complete();
                await drainToolsTask;
                await drainThinkTask;
                logger.LogWarning("Агент отменён: {Machine} {Date}", request.Machine, request.ShiftDate);
                await requestLog.WriteFailureAsync(request.Machine, request.ShiftDate, "stream", request.Model, request.EnableThinking, 0, ex);
                return;
            }
            catch (Exception ex)
            {
                toolChannel.Writer.Complete();
                thinkChannel.Writer.Complete();
                await drainToolsTask;
                await drainThinkTask;
                logger.LogError(ex, "Агент: {Machine} {Date}", request.Machine, request.ShiftDate);
                await Send("error", JsonSerializer.Serialize(ex.Message));
                return;
            }

            toolChannel.Writer.Complete();
            thinkChannel.Writer.Complete();
            await drainToolsTask;
            await drainThinkTask;
            agentTrace = outcome.Trace;

            if (!outcome.Trace.FellBack && !string.IsNullOrWhiteSpace(outcome.FinalJson))
            {
                raw = outcome.FinalJson;
                thinking = null;
                promptVersion = outcome.Trace.PromptVersion ?? promptVersion;
                logger.LogInformation(
                    "Агент {Machine} {Date}: финал за {N} итераций, tools {T}",
                    request.Machine, request.ShiftDate,
                    outcome.Trace.IterationsUsed, outcome.Trace.Rounds.Count);
            }
            else
            {
                logger.LogInformation(
                    "Агент {Machine} {Date}: fallback ({Reason}), thinking-путь",
                    request.Machine, request.ShiftDate, outcome.Trace.FallbackReason);
                var thought = await RunThinkingPathAsync();
                if (thought == null) return;
                (raw, thinking) = thought.Value;
            }
        }
        else
        {
            var thought = await RunThinkingPathAsync();
            if (thought == null) return;
            (raw, thinking) = thought.Value;
        }

        // Обычный thinking-путь (SSE: очередь/размышления). Null = ответ уже отправлен
        // (вырожденная генерация/отмена/ошибка) — дальше не идём.
        async Task<(string Raw, string? Thinking)?> RunThinkingPathAsync()
        {
            var channel = System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(32)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });

        // Дедупликации по равенству дельт здесь нет сознательно: повторяющаяся
        // дельта (например, одиночный пробел между словами) — валидная часть
        // потока, её пропуск склеивал слова в UI. Дубликатов от Ollama не бывает.
        var progress = new Progress<string>(thought =>
        {
            logger.LogDebug("Think: {T}", thought[..Math.Min(50, thought.Length)]);
            channel.Writer.TryWrite(thought);
        });

        string raw;
        string? thinking;
        int queuePosition;
        var sw = Stopwatch.StartNew();

        try
        {
            queuePosition = await ollama.EnterQueueAsync(ct);
            logger.LogInformation(
                "Вошли в очередь: {QueuePos} для {Machine} {Date}",
                queuePosition, request.Machine, request.ShiftDate);

            if (queuePosition > 1)
                await Send("queue", JsonSerializer.Serialize(new { position = queuePosition }));

            logger.LogInformation("Отправка в Ollama...");

            // Думать или нет решает ТОЛЬКО request.EnableThinking; stream-эндпоинт — это
            // транспорт (SSE: очередь/размышления/результат), а не признак thinking.
            // Клиент remeLog вызывает /stream при включённом thinking и передаёт флаг.
            var generateTask = ollama.GenerateCoreAsync(promptBuild.Prompt, request.EnableThinking, progress, ct, model: request.Model);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var drainTask = Task.Run(async () =>
            {
                await foreach (var thought in channel.Reader.ReadAllAsync(cts.Token))
                {
                    logger.LogDebug("Отправка thinking клиенту");
                    await Send("thinking", JsonSerializer.Serialize(thought));
                }
            }, ct);

            (raw, thinking) = await generateTask;
            logger.LogInformation(
                "Ollama завершила. response={RLen}, thinking={TLen}",
                raw.Length, thinking?.Length ?? 0);

            channel.Writer.Complete();
            await drainTask;
            logger.LogInformation("Drain завершён");
        }
        catch (DegenerateGenerationException ex)
        {
            logger.LogWarning(ex, "Вырожденная генерация {Machine} {Date}", request.Machine, request.ShiftDate);
            var degResult = DegenerateEscalation(ex, promptVersion);
            await requestLog.WriteAsync(request, degResult, "stream");
            await Send("result", JsonSerializer.Serialize(degResult, _camelCase));
            return null;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("Отменён: {Machine} {Date}", request.Machine, request.ShiftDate);
            await requestLog.WriteFailureAsync(request.Machine, request.ShiftDate, "stream", request.Model, request.EnableThinking, sw.ElapsedMilliseconds, ex);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка: {Machine} {Date}", request.Machine, request.ShiftDate);
            await requestLog.WriteFailureAsync(request.Machine, request.ShiftDate, "stream", request.Model, request.EnableThinking, sw.ElapsedMilliseconds, ex);
            await Send("error", JsonSerializer.Serialize(ex.Message));
            return null;
        }
        finally
        {
            ollama.LeaveQueue();
        }

        return (raw, thinking);
        }

        var llmResult = ParseResponse(raw);
        if (agentOutcome is { Trace.FellBack: false })
            agentOutcome.Trace.BaseSignals = [.. llmResult.Signals];
        MergeVerifierOpinions(agentOutcome, llmResult, request);
        // Отчётные эхо из общего signals — в shift-вопросы, до пост-фильтра.
        var (dataSignals, shiftEchoes) = ShiftReportRuleEvaluator.SplitShiftEchoes(llmResult.Signals);
        llmResult.Signals = dataSignals;
        var modelShiftIssues = ShiftReportRuleEvaluator.CleanModelIssues(
            llmResult.ShiftReportIssues.Concat(shiftEchoes));
        // Вердикт «релевантно» — одобрение отчёта, ошибочно положенное в проблемы.
        (modelShiftIssues, var droppedRelevanceVerdicts) =
            ShiftReportRuleEvaluator.DropRelevanceVerdicts(modelShiftIssues);
        // Жалоба на связь простоя с КПД — механизм учёта их не связывает никогда.
        (modelShiftIssues, var droppedKpdLink) =
            ShiftReportRuleEvaluator.DropKpdDowntimeLinkComplaints(modelShiftIssues);
        // Требования комментария при самодостаточной причине простоя режутся
        // здесь же — до всех downstream-решений (reset, requiresReview, merge).
        (modelShiftIssues, var droppedCommentDemands) =
            ShiftReportRuleEvaluator.DropSelfSufficientCommentDemands(request, modelShiftIssues);
            (modelShiftIssues, var droppedReasonDemands) =
                ShiftReportRuleEvaluator.DropUnneededReasonDemands(request, modelShiftIssues);
            // Выдумки про отчёт, которого нет («устарел», «мастер не указан»),
            // режутся здесь же — триггер ReportExists тот же, что у иконок главной.
            (modelShiftIssues, var droppedNoReport) =
                ShiftReportRuleEvaluator.DropIssuesWithoutReport(request, modelShiftIssues);
            // Ложь про мастера, который в отчёте указан («мастер не указан»,
            // «оператор <ФИО мастера> отсутствует в системе» — мастера нет
            // в cnc_operators по построению; кейс Rontek HTC420 №1 28.09).
            (modelShiftIssues, var droppedMasterClaims) =
                ShiftReportRuleEvaluator.DropFalseMasterClaims(request, modelShiftIssues);
            // «Работал на другом станке» — релевантная причина простоя,
            // придирки к её детализации/содержанию снимаются.
            (modelShiftIssues, var droppedTwoMachine) =
                ShiftReportRuleEvaluator.DropTwoMachineRelevanceComplaints(request, modelShiftIssues);
            // «Кто отсутствовал / почему / где работал» при непустом содержательном
            // комментарии к «Отсутствию оператора» — комментарий уже объясняет простой.
            (modelShiftIssues, var droppedAbsenceWho) =
                ShiftReportRuleEvaluator.DropAbsenceWhoDemands(request, modelShiftIssues);
            // Фактически неверное «причина неизвестна» при причине из закрытого
            // списка — сверка фактом, а не мнением (пилот 24.09.2026 SKT21 22.09).
            (modelShiftIssues, var droppedUnknownReasons) =
                ShiftReportRuleEvaluator.DropUnknownReasonComplaints(request, modelShiftIssues);
            // Вопрос без привязки к смене («Смена День/Ночь:» буквально) —
            // отнести не к чему, аналитику с ним делать нечего (там же).
            (modelShiftIssues, var droppedUnscoped) =
                ShiftReportRuleEvaluator.DropUnscopedShiftIssues(modelShiftIssues);
            MergeAutoExcludes(llmResult, mastering.AutoExcludes);
            // В агентском пути thinking живёт в трассе (excerpt), а не в переменной —
            // без этого рассинхрон «в мыслях False, в JSON True» (флипы) не виден в логах.
            var consistencyConflict = CheckThinkingConsistency(request, agentTrace?.ThinkingTail ?? agentTrace?.ThinkingExcerpt ?? thinking, llmResult);

        logger.LogDebug("ParseResponse: RequiresReview={R}, Error={E}, ExcludeFromReports={ExcludeCount} [{ExcludeList}]",
            llmResult.RequiresReview, llmResult.Error ?? "(нет)",
            llmResult.SuggestExcludeFromReports.Count, string.Join(" | ", llmResult.SuggestExcludeFromReports));

        var notDowngraded = SoftSignalMatcher.GetNotDowngraded(
            hardRules.SoftSignals, llmResult.DowngradedSignals);

        var (filteredSignals, reset, removedSignals) = FalsePositiveFilter.Apply(
            request, hardRules, notDowngraded, llmResult.Signals);
        llmResult.Signals = filteredSignals;
            if (consistencyConflict)
                llmResult.Signals.Add(ThinkingConsistency.ConflictSignal);
        removedSignals.AddRange(droppedRelevanceVerdicts
            .Select(d => (d, "вердикт «релевантно» — одобрение, а не проблема")));
        removedSignals.AddRange(droppedKpdLink
            .Select(d => (d, "простой и КПД не связаны — жалоба неверна")));
        removedSignals.AddRange(droppedCommentDemands
            .Select(d => (d, "причина простоя самодостаточна, комментарий не требуется")));
            removedSignals.AddRange(droppedReasonDemands
                .Select(d => (d, "причина простоя не требуется (порог 5%) или уже указана")));
            removedSignals.AddRange(droppedNoReport
                .Select(d => (d, "отчёта нет — проверять нечего")));
            removedSignals.AddRange(droppedMasterClaims
                .Select(d => (d, "мастер отчёта — не оператор; отсутствие в cnc_operators не сигнал")));
            removedSignals.AddRange(droppedTwoMachine
                .Select(d => (d, "работа на другом станке — релевантная причина простоя")));
            removedSignals.AddRange(droppedAbsenceWho
                .Select(d => (d, "комментарий к «Отсутствию оператора» объясняет простой — личность не требуется")));
            removedSignals.AddRange(droppedUnknownReasons
                .Select(d => (d, "причина есть в закрытом списке — жалоба неверна")));
            removedSignals.AddRange(droppedUnscoped
                .Select(d => (d, "вопрос без привязки к смене — отнести не к чему")));
            LogFilteredSignals(request, removedSignals, reset);

            // Сброс пост-фильтра не действует при вопросах к отчёту мастера — см. Analyze().
            // Плюс «пустой вердикт» (там же): requires_review без единого основания.
            var emptyModelVerdict = !llmResult.HasError && llmResult.RequiresReview
                && llmResult.Signals.Count == 0 && modelShiftIssues.Count == 0
                && !hardRules.MustEscalate && !shiftRules.MustEscalate && notDowngraded.Count == 0
                && request.Signals.Count == 0 && !request.Parts.Any(p => p.Signals.Count > 0)
                && !consistencyConflict;
            if (emptyModelVerdict)
                logger.LogInformation(
                    "Пустой вердикт модели ({Machine} {Date}): requires_review без оснований — сброшен",
                    request.Machine, request.ShiftDate);
        var modelWantedEscalation = llmResult.RequiresReview;
        var resetEffective = (reset || emptyModelVerdict) && !consistencyConflict && !shiftRules.MustEscalate && modelShiftIssues.Count == 0;
        if (resetEffective)
        {
            llmResult.RequiresReview = false;
            llmResult.Explanation = "";
            llmResult.SuggestedReason = "";
        }
        // Зеркало: модель заявила необъяснённые проблемы, но вердикт False —
        // принудительно True (fail-safe; пилот 24.09.2026, Goodway 01.06).
        else if (FalsePositiveFilter.IsIncoherentOk(llmResult.RequiresReview, llmResult.Signals))
        {
            logger.LogInformation(
                "Противоречивый вердикт модели ({Machine} {Date}): signals непуст при requires_review=false — поднят в True",
                request.Machine, request.ShiftDate);
            llmResult.RequiresReview = true;
        }

        // llmResult.HasError форсирует эскалацию — см. комментарий в Analyze().
        var requiresReview = resetEffective ? false
            : hardRules.MustEscalate || shiftRules.MustEscalate || modelShiftIssues.Count > 0 || notDowngraded.Count > 0
                || llmResult.RequiresReview || llmResult.HasError;

        var result = new AnalyzeResponse
        {
            RequiresReview = requiresReview,
            Confidence = ShiftReportRuleEvaluator.ComputeConfidence(
                hardRules.MustEscalate, shiftRules.MustEscalate, llmResult.HasError, llmResult.Confidence),
            Explanation = EnsureExplanation(llmResult, hardRules, shiftRules, modelShiftIssues, notDowngraded),
            SuggestedReason = string.IsNullOrWhiteSpace(llmResult.SuggestedReason)
                ? FallbackReason(hardRules, shiftRules, modelShiftIssues, notDowngraded, llmResult.HasError) : llmResult.SuggestedReason,
            ThinkingProcess = thinking,
            SuggestExcludeFromReports = FilterExcludeSuggestions(llmResult.SuggestExcludeFromReports, request),
            DowngradedSignals = llmResult.DowngradedSignals,
            Error = llmResult.Error,
            PromptVersion = promptVersion,
        };

        if (hardRules.MustEscalate && result.SuggestExcludeFromReports.Count > 0)
        {
            var hardPartNames = hardRules.HardSignals
                .Where(s => s.StartsWith('['))
                .Select(s => s[1..s.IndexOf(']')])
                .ToHashSet();
            result.SuggestExcludeFromReports = [.. result.SuggestExcludeFromReports
            .Where(e => !hardPartNames.Contains(e.Split('§')[0]))];
        }

        ClassifyAutoExcludes(request, result);

        result.ShiftReportIssues = ShiftReportRuleEvaluator.MergeIssues(shiftRules, modelShiftIssues);
        result.ShiftReportSummary = ShiftReportRuleEvaluator.Summarize(request, shiftRules);
        // В общий Signals — только выжившие модельные вопросы (см. Analyze()).
        var shiftSurvivors = ShiftReportRuleEvaluator.ModelSurvivors(shiftRules, result.ShiftReportIssues);

        result.Signals = [.. llmResult.Signals
        .Concat(request.Signals)
        .Concat(CollectPartSignals(request, hardRules, notDowngraded))
        .Concat(hardRules.HardSignals)
        .Concat(shiftRules.HardSignals)
        .Concat(shiftRules.SoftSignals)
        .Concat(shiftSurvivors)
        .Concat(notDowngraded)
        .Distinct()];

        // Протухшее объяснение — см. комментарий в Analyze(): слова модели про
        // срезанную деталь сбрасываем на базу, причину — на fallback.
        if (ExplanationHygiene.ReferencesOnlyRemovedParts(
            result.Explanation, result.Signals,
            [.. removedSignals.Select(r => r.Signal)],
            [.. request.Parts.Select(p => p.PartName)]))
        {
            logger.LogInformation(
                "Протухшее объяснение ({Machine} {Date}): ссылается только на срезанные сигналы — сброшено на базу",
                request.Machine, request.ShiftDate);
            llmResult.Explanation = "";
            llmResult.SuggestedReason = "";
            result.Explanation = EnsureExplanation(llmResult, hardRules, shiftRules, modelShiftIssues, notDowngraded);
            result.SuggestedReason = FallbackReason(hardRules, shiftRules, modelShiftIssues, notDowngraded, llmResult.HasError);
        }

        var llmDataCaused = ShiftReportRuleEvaluator.LlmDataCaused(llmResult.RequiresReview, llmResult.Signals);
        var (dataCaused, shiftCaused) = ShiftReportRuleEvaluator.EscalationCauses(
            hardRules, notDowngraded, llmDataCaused, llmResult.HasError,
            resetEffective, shiftRules, modelShiftIssues);
        result.EscalatedByData = dataCaused;
        ApplyAutoClosePolicy(request, result, resetEffective && modelWantedEscalation, agentTrace, llmResult.HasError,
            request.UseAgent && !agent.Enabled);
        result.EscalatedByShiftReport = shiftCaused;

        if (ShiftReportRuleEvaluator.ShiftOnlyExplanation(dataCaused, shiftCaused) is { } cleanExplanation)
            result.Explanation = cleanExplanation;

        if (result.ShiftReportIssues.Count == 0 && request.ShiftReports.Count > 0)
            result.Explanation = (result.Explanation + " Отчёт мастера проверен, замечаний нет.").Trim();

        result.FlaggedParts = CollectFlaggedPartKeys(hardRules, notDowngraded, request, llmResult.Signals);

        logger.LogInformation(
            "Stream-анализ завершён: {Machine} {Date} → RequiresReview={R}, Confidence={C:F2}, FlaggedParts={F}, ShiftIssues={SI}",
            request.Machine, request.ShiftDate, result.RequiresReview, result.Confidence,
            result.FlaggedParts.Count, result.ShiftReportIssues.Count);

        await requestLog.WriteAsync(request, result, "stream", agentTrace);
        await Send("result", JsonSerializer.Serialize(result, _camelCase));
    }

    /// <summary> Синхронный IProgress: вызывает колбэк в потоке рапорта,
    /// без постановки в пул (у Progress&lt;T&gt; финальные события терялись). </summary>
    private sealed class SyncProgress(Action<string> onNext) : IProgress<string>
    {
        public void Report(string value) => onNext(value);
    }

    /// <summary>
    /// Fail-safe вердикт при вырожденной генерации (петля в reasoning, в т.ч.
    /// после ретрая): день уходит на проверку, как при HasError. Confidence 1.0 —
    /// система уверена, что человеку надо посмотреть, а не в содержании вердикта.
    /// </summary>
    private static AnalyzeResponse DegenerateEscalation(DegenerateGenerationException ex, string? promptVersion) => new()
    {
        RequiresReview = true,
        Confidence = 1.0,
        Explanation = "Автоматическая эскалация: модель зациклилась при рассуждении.",
        SuggestedReason = "Сбой анализа — требуется ручная проверка",
        Error = ex.Message,
        PromptVersion = promptVersion,
    };

    /// <summary>
    /// Добавляет авто-предложения исключения (детерминированно подтверждённое
    /// освоение с КПД наладки &lt;100%) к предложениям модели, не дублируя строки,
    /// которые модель предложила сама (ключ — PartName§Setup§Order).
    /// </summary>
    private static void MergeAutoExcludes(AnalyzeResponse llmResult, List<string> autoExcludes)
    {
        foreach (var entry in autoExcludes)
        {
            var key = RowKey(entry);
            if (!llmResult.SuggestExcludeFromReports.Any(x => RowKey(x) == key))
                llmResult.SuggestExcludeFromReports.Add(entry);
        }
    }

    private static string RowKey(string excludeEntry) =>
        string.Join('§', excludeEntry.Split('§').Take(3));

    /// <summary>
    /// Рассинхрон рассуждения и ответа (см. <see cref="ThinkingConsistency"/>): вердикт
    /// поднимается в True, возвращает true, чтобы автосброс (пустой вердикт, пост-фильтр)
    /// его не отменил. Рассуждение в агентском пути — только выдержка (ThinkingExcerptCap),
    /// поэтому «хвост» приблизителен; лишняя эскалация здесь дешевле пропуска.
    /// </summary>
    private bool CheckThinkingConsistency(AnalyzeRequest request, string? thinking, AnalyzeResponse llmResult)
    {
        if (llmResult.RequiresReview || !ThinkingConsistency.HasEscalationConclusion(thinking))
            return false;

        logger.LogWarning(
            "Рассинхрон рассуждения и ответа ({Machine} {Date}): requires_review=false в JSON, " +
            "но в конце рассуждения модель писала об эскалации — вердикт поднят в True. " +
            "Хвост рассуждения: {Tail}",
            request.Machine, request.ShiftDate, ThinkingConsistency.Tail(thinking!));
        llmResult.RequiresReview = true;
        return true;
    }

    private static void ClassifyAutoExcludes(AnalyzeRequest request, AnalyzeResponse result)
    {
        var (auto, ignored) = AutoExcludePolicy.Classify(request, result.SuggestExcludeFromReports);
        result.AutoExcludeEntries = auto;
        result.IgnoredExcludeEntries = ignored;
    }

    private static void ApplyAutoClosePolicy(
        AnalyzeRequest request, AnalyzeResponse result, bool resetOverrodeModel, AgentTrace? agentTrace,
        bool hasError, bool agentRequestedButDisabled)
    {
        result.AutoCloseBlockers = AutoClosePolicy.Blockers(
            result.RequiresReview, resetOverrodeModel, agentTrace?.FellBack == true, hasError,
            agentRequestedButDisabled, AutoClosePolicy.MentionsProcessDeviation(request));
        result.AutoCloseEligible = AutoClosePolicy.IsEligible(result.RequiresReview, result.AutoCloseBlockers);
    }

    /// <summary>
    /// Логирует КАЖДЫЙ сигнал, снятый FalsePositiveFilter, с указанием причины —
    /// иначе постфактум невозможно отличить «фильтр верно погасил галлюцинацию»
    /// от «фильтр по ошибке съел реальную аномалию» (см. память
    /// ai-analysis-improvement-plan, кейс 2026-06-30 Rontek HTC650M: сигнал про
    /// «кулачки» ошибочно попал под б/н-фильтр из-за других б/н-деталей того же дня).
    /// </summary>
    private void LogFilteredSignals(
        AnalyzeRequest request, List<(string Signal, string Reason)> removed, bool reset)
    {
        if (removed.Count == 0) return;

        foreach (var (signal, reason) in removed)
            logger.LogInformation(
                "Пост-фильтр снял сигнал ({Machine} {Date}): [{Reason}] {Signal}",
                request.Machine, request.ShiftDate, reason, signal);

        if (reset)
            logger.LogInformation(
                "Пост-фильтр сбросил requiresReview: {Machine} {Date} — " +
                "после отсева не осталось оснований для эскалации",
                request.Machine, request.ShiftDate);
    }

    /// <summary>
    /// Объяснение — только про данные записей. Детали по суточному отчёту живут
    /// в отдельном UI-блоке (ShiftReportIssues/ShiftReportSummary), сюда добавляется
    /// лишь одна фраза-ссылка, чтобы вердикт «требует проверки» не висел без пояснения.
    /// </summary>
    private static string EnsureExplanation(
    AnalyzeResponse llmResult, HardRuleResult hardRules, ShiftReportRuleResult shiftRules, List<string> modelShiftIssues, List<string> notDowngraded)
    {
        string explanation;
        if (hardRules.MustEscalate)
        {
            // Только счётчик: сами правила уже лежат в signals буллетами ниже,
            // дублировать их текстом не нужно.
            var hardBase = $"Эскалация по жёстким правилам ({hardRules.HardSignals.Count}).";
            explanation = !string.IsNullOrWhiteSpace(llmResult.Explanation)
                && !llmResult.Explanation.Contains("отсутствует")
                && !llmResult.Explanation.Contains("нет объяснения")
                && !llmResult.Explanation.Contains("не объяснен")
                && !ExplanationHygiene.EnumeratesRules(llmResult.Explanation)
                ? hardBase + " " + llmResult.Explanation
                : hardBase;
        }
        else if (notDowngraded.Count > 0)
        {
            var softBase = "Эскалация: объяснение мастера не подтверждено "
                + $"({notDowngraded.Count}).";
            explanation = !string.IsNullOrWhiteSpace(llmResult.Explanation)
                && !ExplanationHygiene.EnumeratesRules(llmResult.Explanation)
                ? softBase + " " + llmResult.Explanation
                : softBase;
        }
        else if (!string.IsNullOrWhiteSpace(llmResult.Explanation))
        {
            explanation = llmResult.Explanation;
        }
        else
        {
            explanation = llmResult.HasError
                ? "Не удалось получить объяснение от модели (ошибка разбора ответа)."
                : "Явных отклонений не обнаружено.";
        }

        if ((shiftRules.MustEscalate || modelShiftIssues.Count > 0)
            && !explanation.Contains("отчёту мастера"))
            explanation = (explanation + " Есть вопросы к суточному отчёту мастера.").Trim();

        return explanation;
    }

    private static string FallbackReason(HardRuleResult hardRules, ShiftReportRuleResult shiftRules, List<string> modelShiftIssues, List<string> notDowngraded, bool hasError = false)
    {
        if (hardRules.MustEscalate) return hardRules.HardSignals.FirstOrDefault() ?? "Требует проверки";
        if (shiftRules.MustEscalate) return shiftRules.HardSignals.FirstOrDefault() ?? "Требует проверки";
        if (hasError) return "Сбой анализа — требуется ручная проверка";
        if (notDowngraded.Count > 0)
        {
            var kinds = notDowngraded.Select(SoftSignalMatcher.Classify).ToHashSet();
            if (kinds.Count == 1)
            {
                return kinds.First() switch
                {
                    SoftSignalMatcher.SignalKind.MachiningTime => "Машинное время превышает норматив",
                    SoftSignalMatcher.SignalKind.OperatorComplaint => "Жалоба оператора на норматив не опровергнута",
                    _ => "Объяснение мастера не подтверждено",
                };
            }
            return "Объяснение мастера не подтверждено";
        }
        if (modelShiftIssues.Count > 0) return modelShiftIssues[0];
        return "Без замечаний";
    }

    /// <summary>
    /// Не предлагаем исключать строки, которые и так не участвуют в расчёте премии:
    /// наладка не считается при б/н или КПД=0, изготовление — при б/и, КПД=0 или
    /// штучной партии (пороги регламента). Если у строки нет ни одной участвующей
    /// категории, предложение исключить её бессмысленно и только отвлекает аналитика.
    /// Триггерные причины («освоение», «работа ученика») без подтверждающих отметок —
    /// выдумка модели: галка по умолчанию стоит, неверное основание превратилось бы
    /// в неверное исключение из К1 (см. <see cref="ExcludeTriggerValidator"/>).
    /// </summary>
    private List<string> FilterExcludeSuggestions(List<string> entries, AnalyzeRequest request)
    {
        if (entries.Count == 0) return entries;

        return [.. entries.Select(e => FilterExcludeSuggestion(e, request)).OfType<string>()];
    }

    /// <summary>
    /// Одна запись exclude. Неоднозначный ключ (несколько строк суток с одним
    /// именем/установкой/заказом — кейс 30.09.2026, SKT21 25.09 «Винт…») решается
    /// по доказательствам строки, а не по порядку; вердикт клиенту уходит с
    /// хвостом «§#N», иначе галка встала бы на первую по списку (кейс 24.09,
    /// «Гайка нажимная АР32-01-002»: частичная наладка во второй строке).
    /// Выбрать единственную строку нельзя — запись не привязываем вовсе.
    /// </summary>
    private string? FilterExcludeSuggestion(string entry, AnalyzeRequest request)
    {
        // Форматный мусор («Уст.1» вместо цифры, голая причина вместо конкретики)
        // режем до привязки: иначе запись проходит как «непривязанная» мимо всех
        // содержательных проверок (кейс пилота 25.09, QTS350 23.09).
        if (!ExcludeTriggerValidator.IsWellFormedExcludeEntry(entry)) return null;

        var seg = entry.Split('§');
        if (seg.Length < 3) return entry; // нераспознанный формат — не трогаем

        var reason = ExcludeTriggerValidator.ReasonOf(entry);
        var candidates = ExcludeTriggerValidator.FindAllParts(request.Parts, seg[0], seg[1], seg[2]);
        if (candidates.Count == 0) return entry; // консервативно: клиент покажет непривязанной
        if (candidates.Count == 1)
        {
            var only = candidates[0].Part;
            if (!AffectsReports(only)) return null;
            if (!ExcludeTriggerValidator.HasGroundedReason(only, reason)) return null;
            if (!ExcludeTriggerValidator.HasGrounds(only, reason)) return null;
            return entry;
        }

        var picked = ExcludeTriggerValidator.PickRowForExclude(candidates, reason);
        if (picked == null)
        {
            logger.LogWarning(
                "Exclude не привязан: {Entry} — строк с таким ключом {N}, выбор неоднозначен",
                entry, candidates.Count);
            return null;
        }
        if (!AffectsReports(picked.Value.Part))
        {
            logger.LogInformation(
                "Exclude снят: {Entry} — строка #{Ordinal} не влияет на отчёты (нет считаемого КПД)",
                entry, picked.Value.Ordinal);
            return null;
        }
        if (!ExcludeTriggerValidator.HasGroundedReason(picked.Value.Part, reason)) return null;
        if (!ExcludeTriggerValidator.HasGrounds(picked.Value.Part, reason)) return null;
        logger.LogInformation(
            "Exclude привязан к строке #{Ordinal}: {Entry}", picked.Value.Ordinal, entry);
        return ExcludeTriggerValidator.WithOrdinal(entry, picked.Value.Ordinal);
    }

    private static bool AffectsReports(PartContext p)
    {
        var setupCounts = !p.NoSetupHappened && p.SetupRatio is > 0;
        var productionCounts = !p.NoProductionHappened
            && p.ProductionRatio is > 0
            && !p.IsSmallBatch;
        return setupCounts || productionCounts;
    }

    /// <summary>
    /// Клиентские сигналы деталей для ответа. Если soft-сигнал по детали понижен
    /// моделью, его клиентский дубль («> штучного норматива» / «Оператор сообщает
    /// о некорректном нормативе») тоже не должен попасть в ответ — иначе аналитик
    /// видит сигнал, который система уже сочла объяснённым.
    /// </summary>
    private static IEnumerable<string> CollectPartSignals(
        AnalyzeRequest request, HardRuleResult hardRules, List<string> notDowngraded)
    {
        var downgraded = hardRules.SoftSignals.Except(notDowngraded).ToList();
        if (downgraded.Count == 0)
            return request.Parts.SelectMany(p => p.Signals);

        // Серверный soft-текст может совпадать с клиентским дословно (сервер
        // забирает текст клиента, чтобы не шуметь дублем — кейс 06.10.2026,
        // HTC420 №2 17.09): тогда покрытие детали именем не извлекается,
        // сверяем точным совпадением строк.
        var downgradedTexts = downgraded.ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> PartsOfKind(SoftSignalMatcher.SignalKind kind) => downgraded
            .Where(s => SoftSignalMatcher.Classify(s) == kind)
            .Select(SoftSignalMatcher.ExtractPartName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var machiningParts = PartsOfKind(SoftSignalMatcher.SignalKind.MachiningTime);
        var operatorParts = PartsOfKind(SoftSignalMatcher.SignalKind.OperatorComplaint);

        return request.Parts.SelectMany(p => p.Signals.Where(s =>
            !downgradedTexts.Contains(s)
            && !(machiningParts.Contains(p.PartName) && SoftSignalMatcher.IsMachiningTimeEcho(s))
            && !(operatorParts.Contains(p.PartName) && SoftSignalMatcher.IsOperatorComplaintEcho(s))));
    }

    /// <summary>
    /// Ключи строк, которые ИИ предлагает отметить проблемными (флаги СГТ):
    /// все детали с hard-сигналами + детали, чьи soft-сигналы модель не понизила,
    /// + детали, явно названные в уцелевших сигналах модели (иначе вердикт
    /// «про Гильзу» не подсвечивает ни одну строку — кейс 22.09.2026 Mazak).
    /// Пониженные soft-сигналы считаются объяснёнными — их детали не флагуются.
    /// Несопоставленные имена (галлюцинации) игнорируются.
    /// hard/soft-ключи приходят с номером строки (HardRuleEvaluator.PartKey) —
    /// привязка точная. Ключи, собранные по ИМЕНИ из текста сигнала модели, строку
    /// не различают: при нескольких совпадениях выбираем по доказательствам
    /// (ExcludeRowScore), а НЕ флагуем все строки дубля — иначе аналитику
    /// прилетало по флагу на каждую строку (жалоба 30.09.2026, SKT21 27.09:
    /// 7 строк → 9). Выбрать строку нельзя — флаг не ставим: сам сигнал идёт
    /// в диалоге и без флага.
    /// </summary>
    public static List<string> CollectFlaggedPartKeys(
        HardRuleResult hardRules, List<string> notDowngraded,
        AnalyzeRequest request, IEnumerable<string> modelSignals)
    {
        var surviving = notDowngraded.ToHashSet();
        var exact = hardRules.HardFlaggedPartKeys
            .Concat(hardRules.SoftFlagged
                .Where(t => surviving.Contains(t.Signal))
                .Select(t => t.PartKey))
            .ToHashSet();
        var named = modelSignals
            .SelectMany(s => ResolveNamedKeys(s, request.Parts))
            .Where(k => !exact.Contains(k));
        return [.. exact.Concat(named).Distinct()];
    }

    /// <summary>
    /// Ключи строк, названных в сигнале модели. Имя совпало с несколькими
    /// строками — берём ту, где есть доказательства (сигнал/причина/КПД/
    /// частичная наладка); иначе не привязываем вовсе.
    /// </summary>
    private static IEnumerable<string> ResolveNamedKeys(string signal, List<PartContext> parts)
    {
        var lower = signal.ToLowerInvariant();
        var named = parts
            .Select((p, i) => (Ordinal: i + 1, Part: p))
            .Where(x => x.Part.PartName.Trim().Length > 0
                && lower.Contains(x.Part.PartName.Trim().ToLowerInvariant()))
            .ToList();
        if (named.Count == 0)
            return [];
        if (named.Count == 1)
            return [KeyWithOrdinal(named[0].Part, named[0].Ordinal)];

        var scored = named
            .Select(x => (x.Ordinal, x.Part,
                Score: ExcludeTriggerValidator.ExcludeRowScore(x.Part, signal)))
            .OrderByDescending(x => x.Score)
            .ToList();
        if (scored[0].Score <= 0 || (scored.Count > 1 && scored[1].Score == scored[0].Score))
            return [];
        return [KeyWithOrdinal(scored[0].Part, scored[0].Ordinal)];
    }

    private static string KeyWithOrdinal(PartContext p, int ordinal) =>
        ExcludeTriggerValidator.WithOrdinal($"{p.PartName}§{p.Setup}§{p.Order}", ordinal);

    /// <summary>
    /// Вторые мнения агента в общий downstream: True-несогласия верифаеров сливаются
    /// ДО фильтров — выдуманные сигналы режутся там же, где у базового вердикта,
    /// а пустой итог обработают штатные reset/empty-правила. Base True не трогаем.
    /// </summary>
    private void MergeVerifierOpinions(AgentLoopOutcome? outcome, AnalyzeResponse llmResult, AnalyzeRequest request)
    {
        if (outcome == null || outcome.VerifierJsons.Count == 0) return;

        foreach (var vj in outcome.VerifierJsons)
        {
            var vr = ParseResponse(vj);
            if (!vr.RequiresReview || vr.HasError) continue;
            llmResult.Signals.AddRange(vr.Signals);
            llmResult.ShiftReportIssues.AddRange(vr.ShiftReportIssues);
            if (!llmResult.RequiresReview)
            {
                llmResult.RequiresReview = true;
                llmResult.Explanation =
                    (llmResult.Explanation + " Второе мнение выявило основания для проверки.").Trim();
                logger.LogInformation("Второе мнение подняло вердикт ({Machine} {Date})",
                    request.Machine, request.ShiftDate);
            }
        }
    }

    /// <summary> Пробуем спарсить JSON из ответа модели, обрабатываем типичные огрехи </summary>
    private static AnalyzeResponse ParseResponse(string raw)
    {
        var json = ExtractJson(raw);

        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AnalyzeResponse
            {
                RequiresReview = root.TryGetProperty("requires_review", out var rr) && rr.ValueKind == JsonValueKind.True,
                Confidence = root.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number
                    ? cf.GetDouble()
                    : 0.5,
                Signals = root.TryGetProperty("signals", out var sg) && sg.ValueKind == JsonValueKind.Array
                    ? [.. sg.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0)]
                    : [],
                ShiftReportIssues = root.TryGetProperty("shift_report_issues", out var sri) && sri.ValueKind == JsonValueKind.Array
                    ? [.. sri.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0)]
                    : [],
                DowngradedSignals = root.TryGetProperty("downgraded_signals", out var ds) && ds.ValueKind == JsonValueKind.Array
                    ? [.. ds.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0)]
                    : [],
                SuggestExcludeFromReports = root.TryGetProperty("suggest_exclude_from_reports", out var se)
                    && se.ValueKind == JsonValueKind.Array
                    ? [.. se.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0)]
                    : [],
                Explanation = root.TryGetProperty("explanation", out var ex) ? ex.GetString() ?? "" : "",
                SuggestedReason = root.TryGetProperty("suggested_reason", out var sr) ? sr.GetString() ?? "" : "",
            };
        }
        catch (JsonException)
        {
            return new AnalyzeResponse
            {
                Error = $"Не удалось распарсить ответ модели: {raw[..Math.Min(200, raw.Length)]}",
                Confidence = 0,
            };
        }
    }

    private static string ExtractJson(string raw)
    {
        var text = raw.Trim();

        var thinkEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
            text = text[(thinkEnd + "</think>".Length)..].Trim();

        if (text.StartsWith("```"))
        {
            var fenceStart = text.IndexOf('\n');
            if (fenceStart >= 0) text = text[(fenceStart + 1)..];
            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) text = text[..fenceEnd];
            text = text.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}