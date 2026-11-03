using libeLog.Extensions;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using static remeLog.Infrastructure.Extensions.Part;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace remeLog.Infrastructure
{
    public record AiHealthResult(bool Server, bool Ollama, string? Error = null, string? AnalysisVersion = null);

    /// <summary>
    /// Подготовленный запрос анализа суток: тело запроса и отпечаток данных. Отпечаток считается по всему
    /// запросу (строки, отчёты мастера, история, модель, режим), поэтому совпадение отпечатка и версии
    /// анализа службы значит, что вердикт не изменится (см. <see cref="remeLog.Models.AiVerdictReuse"/>).
    /// </summary>
    public sealed class PreparedAnalysis
    {
        internal PreparedAnalysis(object request, string fingerprint)
        {
            Request = request;
            Fingerprint = fingerprint;
        }

        internal object Request { get; }
        public string Fingerprint { get; }
    }

    /// <summary>
    /// Тип обновления прогресса анализа. Очередь и размышления идут одним каналом,
    /// но с разной семантикой: очередь ЗАМЕНЯЕТ текст (транзитный статус), а приход
    /// первого чанка размышлений ОЧИЩАЕТ поле — иначе «позиция 2» склеивается
    /// с началом thinking («...позиция 2Хорошо, давайте...»).
    /// Tool — шаги агентского контура («что агент делает»): добавляются строками.
    /// </summary>
    public enum AiProgressKind { Queue, Thinking, Tool }

    public record AiProgress(AiProgressKind Kind, string Text);

    public class AiServiceClient
    {
        private static readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None}) { Timeout = TimeSpan.FromSeconds(300) };
        private readonly string? _baseUrl;

        public AiServiceClient(string? baseUrl = null)
        {
            _baseUrl = baseUrl;
        }

        private string GetUrl() =>
            (_baseUrl ?? $"http://{AppSettings.AiIp.GetIpOrDefault()}:{AppSettings.AiPort}").TrimEnd('/');

        public async Task<AiHealthResult> CheckHealthAsync(CancellationToken ct = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var response = await _http.GetAsync($"{GetUrl()}/api/analysis/health", cts.Token);
                if (!response.IsSuccessStatusCode)
                    return new AiHealthResult(false, false,
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                var ollama = json.TryGetProperty("ollama", out var o) && o.GetBoolean();
                var version = json.TryGetProperty("analysisVersion", out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
                return new AiHealthResult(true, ollama, null, version);
            }
            catch (OperationCanceledException)
            {
                return new AiHealthResult(false, false, "Timeout: сервер не ответил за 10 с");
            }
            catch (HttpRequestException ex)
            {
                return new AiHealthResult(false, false, $"HTTP: {ex.Message}");
            }
            catch (Exception ex)
            {
                return new AiHealthResult(false, false, ex.Message);
            }
        }

        public async Task<AiAnalysisResult> AnalyzeAsync(
            string machine, DateTime shiftDate, IEnumerable<Part> parts,
            IProgress<AiProgress>? thinkingProgress = null,
            CancellationToken ct = default)
            => await AnalyzeAsync(await PrepareAnalysisAsync(machine, shiftDate, parts, ct), thinkingProgress, ct);

        /// <summary> Отправляет подготовленный запрос на анализ. </summary>
        public Task<AiAnalysisResult> AnalyzeAsync(
            PreparedAnalysis prepared, IProgress<AiProgress>? thinkingProgress = null, CancellationToken ct = default)
            => AnalyzeWithStreamAsync(prepared.Request, thinkingProgress, ct);

        /// <summary>
        /// Собирает запрос анализа (история деталей, отчёты мастера, профиль) и считает отпечаток данных,
        /// не отправляя его. По отпечатку можно решить, нужен ли анализ вообще.
        /// </summary>
        public async Task<PreparedAnalysis> PrepareAnalysisAsync(
            string machine, DateTime shiftDate, IEnumerable<Part> parts, CancellationToken ct = default)
        {
            var partList = parts.ToList();
            var partsHistories = await LoadPartsHistoriesAsync(
                machine, shiftDate, partList, ct);
            var promptProfile = await GetPromptProfileCachedAsync(machine, ct);
            var isSerial = await GetSerialCachedAsync(machine, ct);
            var shiftReports = BuildShiftReports(machine, shiftDate, partList);
            // Чекбокс «Агент»: вкл — агентский контур, выкл — обычный thinking-режим.
            // Оба идут через /stream (SSE): thinking чанки либо tool-события.
            var useAgent = AppSettings.Instance.AiThinkingEnabled;
            var request = BuildRequest(machine, shiftDate, partList, partsHistories, promptProfile, shiftReports, useAgent, isSerial);

            var hash = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
            return new PreparedAnalysis(request, Convert.ToHexString(hash)[..32]);
        }

        /// <summary>
        /// Фоновая проверка ОДНОЙ записи (фича AiMasterCheck): релевантно ли комментарии
        /// мастера объясняют аномалии строки. Без thinking, компактный промпт. Совещательная
        /// семантика: ЛЮБАЯ ошибка (транспорт, таймаут, пустой ответ) → Ok=true + Error,
        /// чтобы сбой не выглядел замечанием и ничего не блокировал.
        /// </summary>
        public async Task<AiVerifyResult> VerifyPartAsync(
            string machine, DateTime shiftDate, Part part, CancellationToken ct = default)
        {
            try
            {
                var isSerial = await GetSerialCachedAsync(machine, ct);
                // Несерийный станок: аномалии КПД изготовления не поднимаем вообще —
                // проверять нечего ни модели verify-part, ни человеку в подсказке.
                var anomalies = part.GetAiCheckAnomalies()
                    .Where(a => isSerial
                        || a.Field != nameof(Part.MasterMachiningDetail)
                        || !a.Description.StartsWith("КПД изготовления"))
                    .ToList();
                if (anomalies.Count == 0)
                    return new AiVerifyResult { Ok = true };

                var partsHistories = await LoadPartsHistoriesAsync(
                    machine, shiftDate, new List<Part> { part }, ct);

                var request = new
                {
                    machine = machine,
                    shiftDate = shiftDate.ToString("yyyy-MM-dd"),
                    part = BuildPartContext(part, partsHistories, useEffectiveReasons: false),
                    anomalies = anomalies
                        .Select(a => new { field = a.Field, description = a.Description })
                        .ToList(),
                    model = AppSettings.AiModel,
                };

                // Не держим строку заложницей 300-секундного таймаута статического клиента:
                // задержка в очереди Ollama (дневной анализ) тоже съедает этот бюджет.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(120));

                var response = await _http.PostAsJsonAsync(
                    $"{GetUrl()}/api/analysis/verify-part", request, cts.Token);
                response.EnsureSuccessStatusCode();
                var result = await response.Content
                    .ReadFromJsonAsync<AiVerifyResult>(cancellationToken: cts.Token);
                return result ?? new AiVerifyResult { Ok = true, Error = "Пустой ответ сервиса" };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // отмена вызвавшим кодом (строку переправили/окно закрыто) — не результат
            }
            catch (OperationCanceledException)
            {
                return new AiVerifyResult { Ok = true, Error = "Превышено время ожидания" };
            }
            catch (Exception ex)
            {
                return new AiVerifyResult { Ok = true, Error = $"Ошибка связи с AI сервисом: {ex.Message}" };
            }
        }

        private async Task<AiAnalysisResult> AnalyzeSimpleAsync(
            object request, IProgress<AiProgress>? thinkingProgress, CancellationToken ct)
        {
            try
            {
                thinkingProgress?.Report(new AiProgress(AiProgressKind.Thinking, "думает (немного)"));
                var response = await _http.PostAsJsonAsync(
                    $"{GetUrl()}/api/analysis", request, ct);
                response.EnsureSuccessStatusCode();
                var result = await response.Content
                    .ReadFromJsonAsync<AiAnalysisResult>(cancellationToken: ct);
                return result ?? new AiAnalysisResult { Error = "Пустой ответ сервиса" };
            }
            catch (TaskCanceledException)
            {
                return new AiAnalysisResult { Error = "Превышено время ожидания" };
            }
            catch (Exception ex)
            {
                return new AiAnalysisResult
                { Error = $"Ошибка связи с AI сервисом: {ex.Message}" };
            }
        }

        private async Task<AiAnalysisResult> AnalyzeWithStreamAsync(
            object request,
            IProgress<AiProgress>? thinkingProgress,
            CancellationToken ct)
        {
            try
            {
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post, $"{GetUrl()}/api/analysis/stream")
                { Content = content };

                using var response = await _http.SendAsync(
                    httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                return await Task.Run(async () =>
                {
                    using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var reader = new StreamReader(stream, Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: false, bufferSize: 1);

                    AiAnalysisResult? finalResult = null;
                    string? currentEvent = null;

                    while (!reader.EndOfStream)
                    {
                        ct.ThrowIfCancellationRequested();
                        var line = await reader.ReadLineAsync();
                        if (line == null) continue;

                        System.Diagnostics.Debug.WriteLine($"SSE line: [{line}]");

                        if (line.StartsWith("event:"))
                        {
                            currentEvent = line["event:".Length..].Trim();
                            continue;
                        }

                        if (!line.StartsWith("data:")) continue;
                        var data = line["data:".Length..].Trim();

                        switch (currentEvent)
                        {
                            case "queue":
                                var queueInfo = JsonSerializer.Deserialize<QueueInfo>(data);
                                if (queueInfo != null)
                                    thinkingProgress?.Report(new AiProgress(AiProgressKind.Queue,
                                        $"Анализ в очереди: позиция {queueInfo.Position}"));
                                break;

                            case "thinking":
                                var thought = JsonSerializer.Deserialize<string>(data);
                                if (!string.IsNullOrWhiteSpace(thought))
                                    thinkingProgress?.Report(new AiProgress(AiProgressKind.Thinking, thought));
                                break;

                            case "tool":
                                // Шаг агентского контура («Проверяет причину…», «Смотрит историю…»).
                                var step = JsonSerializer.Deserialize<string>(data);
                                if (!string.IsNullOrWhiteSpace(step))
                                    thinkingProgress?.Report(new AiProgress(AiProgressKind.Tool, step));
                                break;

                            case "result":
                                finalResult = JsonSerializer.Deserialize<AiAnalysisResult>(data);
                                break;

                            case "error":
                                return new AiAnalysisResult { Error = data };
                        }

                        currentEvent = null;
                    }

                    return finalResult ?? new AiAnalysisResult { Error = "Пустой ответ сервиса" };
                }, ct);
            }
            catch (TaskCanceledException)
            {
                return new AiAnalysisResult { Error = "Превышено время ожидания" };
            }
            catch (Exception ex)
            {
                return new AiAnalysisResult
                { Error = $"Ошибка связи с AI сервисом: {ex.Message}" };
            }
        }

        /// <summary>
        /// Для каждой уникальной (PartName, Order) среди частей текущих суток
        /// подгружает и агрегирует историю той же детали за прошлые даты на том же станке.
        /// Ключ словаря — (PartName, Order), Machine один для всего вызова.
        /// </summary>
        private static async Task<Dictionary<(string PartName, string Order, int Setup), PartsHistorySummary>>
            LoadPartsHistoriesAsync(
                string machine, DateTime shiftDate, List<Part> parts, CancellationToken ct)
        {
            var result = new Dictionary<(string, string, int), PartsHistorySummary>();

            var uniqueParts = parts
                .Select(p => (p.PartName, p.Order, p.Setup))
                .Distinct()
                .ToList();

            foreach (var (partName, order, setup) in uniqueParts)
            {
                ct.ThrowIfCancellationRequested();
                var history = await Database.ReadPartsHistoryAsync(
                    partName, order, machine, setup, shiftDate,
                    AppSettings.PartsHistoryMaxRecords,
                    AppSettings.PartsHistoryMaxDaysBack,
                    ct);
                result[(partName, order, setup)] = SummarizePartsHistory(history);
            }

            return result;
        }

        /// <summary>
        /// Агрегирует список прошлых записей в построчный список смен.
        /// Использует Part.ProductionRatio/SetupRatio — формула не дублируется.
        /// </summary>
        private static PartsHistorySummary SummarizePartsHistory(
            List<PartsHistoryEntry> history)
        {
            var summary = new PartsHistorySummary { RecordsFound = history.Count };
            if (history.Count == 0) return summary;

            var byDate = history
                .GroupBy(e => e.Part.ShiftDate.Date)
                .OrderByDescending(g => g.Key);

            foreach (var group in byDate)
            {
                var groupParts = group.Select(e => e.Part).ToList();
                var entry = group.First();

                var validPr = groupParts
                    .Select(p => p.ProductionRatio)
                    .Where(r => !double.IsNaN(r) && !double.IsInfinity(r) && r > 0)
                    .ToList();
                double? avgPr = validPr.Count > 0
                    ? Math.Round(validPr.Average(), 2)
                    : (double?)null;

                var validSr = groupParts
                    .Select(p => p.SetupRatio)
                    .Where(r => !double.IsNaN(r) && !double.IsInfinity(r) && r > 0)
                    .ToList();
                double? avgSr = validSr.Count > 0
                    ? Math.Round(validSr.Average(), 2)
                    : (double?)null;

                double totalFinished = groupParts.Sum(p => p.FinishedCount);

                bool hasLowEfficiency = groupParts.Any(p =>
                {
                    var pr = p.ProductionRatio;
                    return !double.IsNaN(pr) && !double.IsInfinity(pr)
                        && pr > 0 && pr < 0.7
                        && string.IsNullOrWhiteSpace(p.EffectiveMachiningReason);
                });

                summary.Lines.Add(new PartsHistoryLine
                {
                    ShiftDate = group.Key,
                    ProductionRatio = avgPr,
                    SetupRatio = avgSr,
                    FinishedCount = totalFinished,
                    AnalystDecision = entry.AnalystDecision,
                    AnalystComment = entry.AnalystComment,
                    AiExplanation = entry.AiExplanation,
                    AiFeedback = entry.AiFeedback,
                    HasUnexplainedLowEfficiency = hasLowEfficiency,
                });
            }

            return summary;
        }

        private static object BuildRequest(
                string machine, DateTime shiftDate, IEnumerable<Part> parts,
                Dictionary<(string PartName, string Order, int Setup), PartsHistorySummary> partsHistories,
                string? promptProfile = null,
                List<object>? shiftReports = null,
                bool useAgent = false,
                bool isSerialMachine = true)
        {
            var partList = parts.ToList();
            var partContexts = partList.Select(p => BuildPartContext(p, partsHistories, isSerialMachine: isSerialMachine)).ToList();
            var daySignals = DetectDaySignals(partList);

            return new
            {
                machine = machine,
                shiftDate = shiftDate.ToString("yyyy-MM-dd"),
                signals = daySignals,
                parts = partContexts,
                shiftReports = shiftReports ?? new List<object>(),
                downtimeReasons = AppSettings.Instance.UnspecifiedDowntimesReasons,
                model = AppSettings.AiModel,
                promptProfile = string.IsNullOrWhiteSpace(promptProfile) ? null : promptProfile.Trim(),
                // Единственный источник истины «думать или нет» — сервер уважает его
                // на обоих эндпоинтах; выбор /stream — только транспорт (SSE).
                // Агентский путь идёт без thinking (ответ целиком), thinking-режим — всегда с ним.
                enableThinking = !useAgent,
                // Агентский контур: действует только вместе с серверным Agent:Enabled.
                useAgent = useAgent,
                // Несерийный станок: КПД изготовления не оценивается ни в одном контуре.
                isSerialMachine = isSerialMachine,
            };
        }

        /// <summary>
        /// Снимки суточных отчётов мастера (cnc_shifts, день + ночь) + свежие значения,
        /// пересчитанные по текущим parts теми же формулами, что рисует DailyReportWindow.
        /// Сервер сверяет снимок со свежим значением (R4) и применяет остальные правила.
        /// Ошибка чтения БД — отчёт помечается отсутствующим, сервер эскалирует (hard).
        /// </summary>
        private static List<object> BuildShiftReports(
            string machine, DateTime shiftDate, List<Part> partList)
        {
            var reports = new List<object>();
            foreach (var shiftType in new[] { ShiftType.Day, ShiftType.Night })
            {
                var shiftName = new Shift(shiftType).Name;
                string master = string.Empty;
                double stored = 0;
                string reason = string.Empty;
                string comment = string.Empty;
                bool exists = false;

                var read = Database.ReadShiftInfo(new ShiftInfo(shiftDate, shiftType, machine));
                if (read.IsOk && read.Value is { Count: 1 })
                {
                    var shift = read.Value[0];
                    master = shift.Master ?? string.Empty;
                    stored = shift.UnspecifiedDowntimes;
                    reason = shift.DowntimesComment ?? string.Empty;
                    comment = shift.CommonComment ?? string.Empty;
                    exists = true;
                }

                reports.Add(new
                {
                    shift = shiftName,
                    shiftMinutes = (int)shiftType,
                    reportExists = exists,
                    master = master,
                    storedUnspecifiedDowntimes = stored,
                    freshUnspecifiedDowntimes = partList.UnspecifiedDowntimes(shiftDate, shiftDate, shiftType),
                    downtimeReason = reason,
                    masterComment = comment,
                    hasParts = partList.Any(p => p.Shift == shiftName),
                    partialSetupRatio = partList.PartialSetupRatio(shiftDate, shiftDate, shiftType),
                });
            }
            return reports;
        }

        /// <summary>
        /// Профиль промпта станка из cnc_machines.AiPromptProfile с кэшем на 5 минут —
        /// пакетный анализ дергает AnalyzeAsync по каждому дню, запрос в БД на каждый день лишний.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Profile, DateTime LoadedAt)>
            _promptProfileCache = new();

        private static async Task<string?> GetPromptProfileCachedAsync(string machine, CancellationToken ct)
        {
            if (_promptProfileCache.TryGetValue(machine, out var cached)
                && (DateTime.UtcNow - cached.LoadedAt) < TimeSpan.FromMinutes(5))
                return cached.Profile;

            var profile = await Database.GetMachineAiPromptProfileAsync(machine, ct);
            _promptProfileCache[machine] = (profile, DateTime.UtcNow);
            return profile;
        }



        /// <summary>
        /// Серийность станка из cnc_machines.IsSerial с кэшем на 5 минут
        /// (копия паттерна GetPromptProfileCachedAsync). Дефолт true (серийный):
        /// сбой БД не должен тихо выключать проверку КПД изготовления.
        /// null не возвращаем — сервер трактует отсутствие флага как серийный.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Serial, DateTime LoadedAt)>
            _serialCache = new();

        private static async Task<bool> GetSerialCachedAsync(string machine, CancellationToken ct)
        {
            if (_serialCache.TryGetValue(machine, out var cached)
                && (DateTime.UtcNow - cached.LoadedAt) < TimeSpan.FromMinutes(5))
                return cached.Serial;

            // Дефолт true — в CatalogDb.GetMachineSerialStatusOrDefault.
            var serial = await Database.GetMachineSerialStatusOrDefault(machine, defaultValue: true, ct);
            _serialCache[machine] = (serial, DateTime.UtcNow);
            return serial;
        }

        /// <param name="useEffectiveReasons">
        /// true (дневной анализ) — в причины уходит эффективное значение: переопределение СГТ,
        /// если оно есть. false (verify-part, фича AiMasterCheck) — уходит ОТМЕТКА МАСТЕРА:
        /// там проверяется качество заполнения именно мастером, и список аномалий приходит из
        /// Part.GetAiCheckAnomalies(), который тоже считает по его полям. Рассинхрон дал бы
        /// модели вопрос «объясняет ли комментарий мастера аномалию» при показанной причине СГТ.
        /// </param>
        private static object BuildPartContext(
            Part p,
            Dictionary<(string PartName, string Order, int Setup), PartsHistorySummary> partsHistories,
            bool useEffectiveReasons = true,
            bool isSerialMachine = true)
        {
            var setupReason = useEffectiveReasons ? p.EffectiveSetupReason : p.MasterSetupComment;
            var machiningReason = useEffectiveReasons ? p.EffectiveMachiningReason : p.MasterMachiningComment;
            var setupDetail = useEffectiveReasons ? p.EffectiveSetupDetail : p.MasterSetupDetail;
            var machiningDetail = useEffectiveReasons ? p.EffectiveMachiningDetail : p.MasterMachiningDetail;

            bool noSetup = p.StartSetupTime == p.StartMachiningTime;
            // Изготовления не было: деталей по факту нет И времени нет, ЛИБО единственная
            // деталь партии выполнена в наладке (FinishedCount=1 при наладке → Fact=0) — б/и.
            // Случай «время есть, деталей нет вообще» (raw=0) остаётся противоречием данных.
            bool noProduction = p.FinishedCountFact <= 0
                && (p.FinishedCount > 0 || p.ProductionTimeFact <= 0);
            var manualComment = GetManualOperatorComment(p.OperatorComment);

            partsHistories.TryGetValue((p.PartName, p.Order, p.Setup), out var history);

            // Сериализуемое представление истории для JSON — анонимные типы,
            // null если истории нет (деталь делается впервые).
            object? partsHistoryObj = null;
            if (history != null && history.HasHistory)
            {
                var lines = history.Lines.Select(l => new
                {
                    shiftDate = l.ShiftDate.ToString("yyyy-MM-dd"),
                    productionRatio = l.ProductionRatio.HasValue
                        ? l.ProductionRatio.Value.ToString("0%")
                        : "б/и",
                    setupRatio = l.SetupRatio.HasValue
                        ? l.SetupRatio.Value.ToString("0%")
                        : "б/н",
                    finishedCount = (int)l.FinishedCount,
                    analystDecision = l.AnalystDecision ?? "не проверено",
                    analystComment = l.AnalystComment,
                    aiExplanation = l.AiExplanation,
                    aiFeedback = l.AiFeedback,
                    hasUnexplainedLowEfficiency = l.HasUnexplainedLowEfficiency,
                 }).ToList();

                partsHistoryObj = new
                {
                    recordsFound = history.RecordsFound,
                    lines = lines,
                };
            }

            return new
            {
                partName = p.PartName,
                order = p.Order,
                setup = p.Setup,

                setupRatio = SafeDouble(p.SetupRatio),
                productionRatio = SafeDouble(p.ProductionRatio),

                finishedCount = p.FinishedCountFact,
                setupTimePlan = p.SetupTimePlan,
                setupTimeFact = p.SetupTimeFact,
                singleProductionTimePlan = p.SingleProductionTimePlan,
                productionTimeFact = p.ProductionTimeFact,
                partialSetup = p.PartialSetupTime,
                machiningTime = Math.Round(p.MachiningTime.TotalMinutes, 1),
                downtimeRatio = SafeDouble(p.SpecifiedDowntimesRatio),

                operatorComment = manualComment,
                // Причины уходят ЭФФЕКТИВНЫЕ (переопределение СГТ, если есть). Тогда весь
                // серверный контур — HardRule, FalsePositiveFilter, MasteringAutoApprover,
                // правила промптов — работает по итоговому решению без единой правки: имя поля
                // прежнее, меняется только значение. Исходная отметка мастера и обоснование
                // переопределения идут ниже отдельными полями, как дополнительный контекст.
                masterSetupComment = setupReason ?? string.Empty,
                masterMachiningComment = machiningReason ?? string.Empty,
                masterComment = p.MasterComment ?? string.Empty,
                masterSetupDetail = setupDetail ?? string.Empty,
                masterMachiningDetail = machiningDetail ?? string.Empty,
                specifiedDowntimesList = GetSpecifiedDowntimesList(p.OperatorComment),
                specifiedDowntimesComment = p.SpecifiedDowntimesComment ?? string.Empty,

                // Переопределение причин аналитиком (СГТ 1) — контекст, не замена полей выше.
                // При useEffectiveReasons=false (verify-part) шлём пустыми: там в причинах лежит
                // отметка мастера, и упоминание переопределения только сбивало бы проверку.
                setupReasonOverride = useEffectiveReasons ? p.SetupReasonOverride ?? string.Empty : string.Empty,
                setupReasonOverrideComment = useEffectiveReasons ? p.SetupReasonOverrideComment ?? string.Empty : string.Empty,
                machiningReasonOverride = useEffectiveReasons ? p.MachiningReasonOverride ?? string.Empty : string.Empty,
                machiningReasonOverrideComment = useEffectiveReasons ? p.MachiningReasonOverrideComment ?? string.Empty : string.Empty,
                reasonOverrideBy = useEffectiveReasons ? p.ReasonOverrideBy ?? string.Empty : string.Empty,

                noManualOperatorComment = string.IsNullOrWhiteSpace(manualComment),
                noSetupHappened = noSetup,
                noProductionHappened = noProduction,

                signals = DetectPartSignals(p, noSetup, noProduction, manualComment, isSerialMachine),

                partsHistory = partsHistoryObj,
            };
        }

        private static List<string> DetectPartSignals(
            Part p, bool noSetup, bool noProduction, string manualComment, bool isSerialMachine = true)
        {
            var s = new List<string>();
            var machMins = p.MachiningTime.TotalMinutes;
            var hasOrder = !string.IsNullOrWhiteSpace(p.Order) && !p.Order.Equals("Без М/Л", StringComparison.OrdinalIgnoreCase);

            // Норматив наладки/изготовления отсутствует при реальном заказе — требует объяснения
            // ВСЕГДА, независимо от факта работы в смену (см. Part.cs.HasOrder/Error — там это
            // блокирует сохранение; здесь ловим случаи, сохранённые до появления этой проверки).
            // Здесь и ниже причина берётся ЭФФЕКТИВНАЯ: если аналитик переопределил, запись
            // объяснена — пусть и не мастером, — и сигнал «нет объяснения» гасится.
            if (p.SetupTimePlan <= 0 && hasOrder && string.IsNullOrWhiteSpace(p.EffectiveSetupReason))
                s.Add("Отсутствует норматив наладки при реальном заказе — комментарий мастера не указан");
            if (p.SingleProductionTimePlan <= 0 && hasOrder && string.IsNullOrWhiteSpace(p.EffectiveMachiningReason))
                s.Add("Отсутствует норматив изготовления при реальном заказе — комментарий мастера не указан");

            // Машинное время > штучного норматива (условия синхронизированы с серверным HardRuleEvaluator).
            // Не шлём: при б/и (норматив штучный, изготовления не было), для штучных партий
            // (в отчёты не попадают, малая партия не показательна) и при КПД изготовления > 100%
            // (показатели не пострадали — эскалация подождёт проблем).
            if (!noProduction && machMins > 0.5 && p.SingleProductionTimePlan > 0
                && machMins > p.SingleProductionTimePlan
                && !p.IsSmallBatch
                && !(p.ProductionRatio > 1))
                s.Add($"Машинное время {machMins:0.#}мин > штучного норматива {p.SingleProductionTimePlan:0.#}мин ({machMins / p.SingleProductionTimePlan:0%})");

            // КПД частичной наладки < 70%
            if (p.PartialSetupTime > 0 && p.SetupTimePlan > 0
                && p.PartialSetupTime > p.SetupTimePlan / 0.695)
                s.Add($"КПД частичной наладки {p.SetupTimePlan / p.PartialSetupTime:0%} < 70% (план {p.SetupTimePlan:0}мин, факт {p.PartialSetupTime:0}мин)");

            // Оператор сообщает о проблеме с нормативом (жёсткое правило!)
            if (OperatorMentionsNormativeIssue(manualComment))
                s.Add("Оператор сообщает о некорректном нормативе или технологии");

            // КПД наладки < 70% без объяснения мастера (освоение — исключение)
            var sr = p.SetupRatio;
            if (IsValidRatio(sr) && sr < 0.695 && p.SetupTimeFact > 0 && (p.SetupTimePlan > 0 || hasOrder)
                && string.IsNullOrWhiteSpace(p.EffectiveSetupReason)
                && !OperatorMentionsExcusableReason(manualComment))
                s.Add($"КПД наладки {sr:0%} без объяснения мастера");

            // КПД наладки > 200% без объяснения — норматив завышен?
            // Зеркало изготовления >120% (единственный предел без сигнала — агентский
            // промпт с v15 пределы не проверяет, нарушения приходят только сигналами).
            // Наладка оценивается и на несерийных — флага isSerialMachine нет.
            if (IsValidRatio(sr) && sr > 2.0 && p.SetupTimeFact > 0
                && string.IsNullOrWhiteSpace(p.EffectiveSetupReason))
                s.Add($"КПД наладки {sr:0%} > 200% — возможно норматив завышен");

            // КПД изготовления < 70% без объяснения мастера.
            // Несерийный станок: КПД изготовления не оценивается — пропускаем
            // (остальное: нормативы, машинное время, противоречия — в силе).
            var pr = p.ProductionRatio;
            if (isSerialMachine
                && IsValidRatio(pr) && pr < 0.695 && p.FinishedCount > 0 && (p.SingleProductionTimePlan > 0 || hasOrder)
                && string.IsNullOrWhiteSpace(p.EffectiveMachiningReason)
                && !OperatorMentionsExcusableReason(manualComment))
                s.Add($"КПД изготовления {pr:0%} без объяснения мастера");

            // КПД изготовления > 120% без объяснения — норматив занижен?
            // Несерийный станок: см. выше.
            if (isSerialMachine
                && IsValidRatio(pr) && pr > 1.2 && p.FinishedCount > 0
                && string.IsNullOrWhiteSpace(p.EffectiveMachiningReason))
                s.Add($"КПД изготовления {pr:0%} > 120% — возможно норматив занижен");

            // Сигнала по простоям нет: порога аномальности у простоев не существует,
            // комментарий мастера при >50% собирает валидация (Part.Error) — модель
            // проверяет только его релевантность.

            // Время изготовления записано но деталей нет — противоречие в данных
            if (p.ProductionTimeFact > 5 && p.FinishedCount == 0
                && string.IsNullOrWhiteSpace(p.EffectiveMachiningReason))
                s.Add($"Время изготовления {p.ProductionTimeFact:0}мин но finishedCount = 0");

            // Машинное время = 0 при наличии изготовления
            if (machMins < 0.5 && p.FinishedCountFact > 0 && p.SingleProductionTimePlan > 0)
                s.Add("Машинное время = 0 при наличии изготовления");

            return s;
        }

        // Сигналы уровня дня

        private static List<string> DetectDaySignals(List<Part> parts)
        {
            var s = new List<string>();
            return s;
            // Повторяющиеся жалобы на оборудование в ручных комментариях (≥2 записей)
            var withEquipment = parts
                .Where(p => ContainsEquipmentKeywords(GetManualOperatorComment(p.OperatorComment)))
                .ToList();
            if (withEquipment.Count >= 2)
                s.Add($"Повторяющиеся жалобы на оборудование в {withEquipment.Count} записях");

            return s;
        }

        // Вспомогательные

        /// <summary>
        /// Ручная часть комментария оператора — текст ДО авто-раздела "Отмеченные простои:".
        /// Именно этот текст является реальным сообщением оператора.
        /// </summary>
        private static string GetManualOperatorComment(string? comment)
        {
            if (string.IsNullOrWhiteSpace(comment)) return "";
            var idx = comment.IndexOf("Отмеченные простои", StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? comment[..idx].Trim() : comment.Trim();
        }

        /// <summary>
        /// Авто-сгенерированный перечень отмеченных простоев — текст ОТ авто-раздела
        /// "Отмеченные простои:" до конца исходного комментария оператора.
        /// Содержит причины простоев с пометками [н] (в наладке) / [и] (в изготовлении).
        /// </summary>
        private static string GetSpecifiedDowntimesList(string? comment)
        {
            if (string.IsNullOrWhiteSpace(comment)) return "";
            var idx = comment.IndexOf("Отмеченные простои", StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? comment[idx..].Trim() : "";
        }

        private static double? SafeDouble(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? null : Math.Round(v, 4);

        private static bool IsValidRatio(double v) =>
            !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;

        private static bool OperatorMentionsNormativeIssue(string? comment)
        {
            if (string.IsNullOrWhiteSpace(comment)) return false;
            var l = comment.ToLowerInvariant();

            if ((l.Contains("укладыва") || l.Contains("уложиться")) && l.Contains("норматив"))
                return false;

            if (l.Contains("норматив") || l.Contains("не соответствует")
                || l.Contains("некорректн") || l.Contains("режимы не")
                || l.Contains("программа не соответ") || l.Contains("скорректировать")
                // жалоба числами без слова «норматив»: «время наладки на 1 шт 320 мин»
                || l.Contains("на 1 шт") || l.Contains("на 1шт"))
                return true;

            // Зеркало HardRuleEvaluator.OperatorMentionsNormativeIssue — держать в синхроне.
            if (l.Contains("технолог") || l.Contains("техпроцесс") || l.Contains("техпроцес"))
            {
                return l.Contains("некорректн") || l.Contains("не соответств")
                    || l.Contains("провер") || l.Contains("требу") || l.Contains("нужн")
                    || l.Contains("неверн") || l.Contains("неправильн") || l.Contains("скорректир")
                    || l.Contains("сомнева") || l.Contains("ошиб");
            }

            return false;
        }

        private static bool OperatorMentionsExcusableReason(string manual)
        {
            if (string.IsNullOrWhiteSpace(manual)) return false;
            var l = manual.ToLowerInvariant();
            return l.Contains("освоен");
        }

        private static bool ContainsEquipmentKeywords(string manual)
        {
            if (string.IsNullOrWhiteSpace(manual)) return false;
            var l = manual.ToLowerInvariant();
            return false; // заглушка
        }
    }


    public record QueueInfo([property: JsonPropertyName("position")] int Position);

    /// <summary>
    /// Результат фоновой проверки одной записи (verify-part). Ok=true по умолчанию:
    /// проверка совещательная, любой сбой должен выглядеть как «проверка недоступна»,
    /// а не как замечание.
    /// </summary>
    public class AiVerifyResult
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
        [JsonPropertyName("remark")] public string Remark { get; set; } = "";
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("promptVersion")] public string? PromptVersion { get; set; }
        public bool HasError => !string.IsNullOrEmpty(Error);
    }

}