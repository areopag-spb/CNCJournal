// tools/AgentReplay — A/B-прогон исторических дней из request_logs/ через агентский
// контур с живой Ollama. НЕ часть прод-сервиса: отдельный exe для экспериментов.
// Использование:
//   dotnet run --project tools/AgentReplay -- --logs C:\AiService\request_logs\2026-09 \
//     --out %TEMP%\agent-replay.json --limit 10 --model qwen3:14b
// На каждый файл: запрос → HardRule/ShiftReport (как в проде) → AgentLoopService →
// разбор финала → строка результата (agent vs prod-вердикт из того же файла).
// Сверка с Decision аналитика — отдельным SQL по machine/shiftDate (см. ai_day_reviews).

using AiService.Models;
using AiService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

var opts = ParseArgs(args);
var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Agent:Enabled"] = "true",
        ["Agent:MaxIterations"] = opts.MaxIter.ToString(),
        ["Agent:TimeoutSeconds"] = opts.Timeout.ToString(),
        ["Agent:Temperature"] = opts.Temperature,
        ["Agent:Thinking"] = opts.Thinking.ToString(),
        ["Ollama:BaseUrl"] = opts.Ollama,
        ["Ollama:Model"] = opts.Model,
    })
    .Build();

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Warning));
var ollama = new OllamaService(config, loggerFactory.CreateLogger<OllamaService>());
var prompts = new PromptBuilder(loggerFactory.CreateLogger<PromptBuilder>());
var agent = new AgentLoopService(ollama, prompts, config,
    loggerFactory.CreateLogger<AgentLoopService>(),
    RequestAgentDataSource.Instance);

var files = Directory.GetFiles(opts.Logs, "*.json")
    .Where(f => !Path.GetFileName(f).StartsWith("verify_", StringComparison.OrdinalIgnoreCase)
             && !Path.GetFileName(f).StartsWith("error_", StringComparison.OrdinalIgnoreCase)
             && (string.IsNullOrWhiteSpace(opts.Match) || Path.GetFileName(f).Contains(opts.Match, StringComparison.OrdinalIgnoreCase)))
    .OrderByDescending(f => f)
    .Take(opts.Limit)
    .ToList();

Console.WriteLine($"Файлов: {files.Count}, модель: {opts.Model}, итераций: {opts.MaxIter}, таймаут: {opts.Timeout}с");

var results = new List<object>();
foreach (var file in files)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        var req = root.GetProperty("request").Deserialize<AnalyzeRequest>(jsonOpts)!;
        if (!string.IsNullOrWhiteSpace(opts.Model))
            req.Model = opts.Model;
        if (opts.Serial.Equals("off", StringComparison.OrdinalIgnoreCase))
            req.IsSerialMachine = false;
        else if (opts.Serial.Equals("on", StringComparison.OrdinalIgnoreCase))
            req.IsSerialMachine = true;

        var prod = root.GetProperty("response");
        var prodRR = prod.TryGetProperty("requiresReview", out var prr) && prr.ValueKind == JsonValueKind.True;
        var prodSignals = prod.TryGetProperty("signals", out var ps) && ps.ValueKind == JsonValueKind.Array
            ? ps.GetArrayLength() : -1;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hard = HardRuleEvaluator.Evaluate(req);
        var shift = ShiftReportRuleEvaluator.Evaluate(req);
        var outcome = await agent.AnalyzeAsync(req, hard, shift, CancellationToken.None);
        sw.Stop();

        var (agentRR, agentSigCount, agentSigTexts, agentExplanation, agentDowngraded, agentShiftIssues) = outcome.FinalJson != null
            ? ExtractVerdict(outcome.FinalJson) : ((bool?)null, -1, new List<string>(), "", new List<string>(), new List<string>());

        // Пайплайн-уровень (зеркало формулы AnalysisController): сырое решение модели
        // складывается с детерминированным контуром. Сравнение сырого requires_review
        // с финалом прода — некорректно: hard/soft/shift форсируют эскалацию мимо модели.
        var notDowngraded = SoftSignalMatcher.GetNotDowngraded(hard.SoftSignals, agentDowngraded);
        // Пост-фильтр ложных сигналов (как в AnalysisController): без него сырая придирка
        // модели, которую прод гасит, считалась бы эскалацией. Упрощение: сброс не действует
        // только при вопросах модели к отчёту мастера (shift issues остаются).
        var fpReset = agentRR == true && FalsePositiveFilter.Apply(req, hard, notDowngraded, agentSigTexts).ShouldReset
            && agentShiftIssues.Count == 0;
        var pipelineRR = hard.MustEscalate || shift.MustEscalate || notDowngraded.Count > 0 || (agentRR == true && !fpReset);

        var row = new
        {
            file = Path.GetFileName(file),
            machine = req.Machine,
            shiftDate = req.ShiftDate,
            promptVersion = outcome.Trace.PromptVersion,
            iterations = outcome.Trace.IterationsUsed,
            toolRounds = outcome.Trace.Rounds.Select(r => r.Name).ToList(),
            fellBack = outcome.Trace.FellBack,
            fallbackReason = outcome.Trace.FallbackReason,
            elapsedSec = Math.Round(sw.Elapsed.TotalSeconds, 1),
            agentRequiresReview = agentRR,
            agentSignals = agentSigCount,
            agentSignalTexts = agentSigTexts.Take(6).ToList(),
            agentExplanation = agentExplanation,
            agentDowngraded = agentDowngraded,
            agentShiftIssues = agentShiftIssues.Take(6).ToList(),
            hardMust = hard.MustEscalate,
            shiftMust = shift.MustEscalate,
            softLeft = notDowngraded.Count,
            fpReset,
            pipelineRequiresReview = pipelineRR,
            pipelineMatch = pipelineRR == prodRR,
            prodRequiresReview = prodRR,
            prodSignals,
            match = agentRR == prodRR,
        };
        results.Add(row);
        Console.WriteLine($"{req.Machine} {req.ShiftDate}: agent={Fmt(agentRR)} pipe={pipelineRR} (sig {agentSigCount}, {row.elapsedSec}с, ит.{row.iterations}, tools [{string.Join(",", (List<string>)row.toolRounds)}]{(row.fellBack ? $" FALLBACK: {row.fallbackReason}" : "")}) [hard={hard.MustEscalate} shift={shift.MustEscalate} softLeft={notDowngraded.Count}] vs prod={prodRR} → {(pipelineRR == prodRR ? "СОВПАЛО" : "РАСХОЖДЕНИЕ")}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{Path.GetFileName(file)}: ОШИБКА {ex.Message}");
        results.Add(new { file = Path.GetFileName(file), error = ex.Message });
    }
}

File.WriteAllText(opts.Out, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Готово: {results.Count} строк → {opts.Out}");

static (bool? rr, int signals, List<string> texts, string explanation, List<string> downgraded, List<string> shiftIssues) ExtractVerdict(string raw)
{
    try
    {
        var text = raw.Trim();
        var thinkEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0) text = text[(thinkEnd + "</think>".Length)..].Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return (null, -1, [], "", [], []);
        using var doc = JsonDocument.Parse(text[start..(end + 1)]);
        var root = doc.RootElement;
        bool? rr = root.TryGetProperty("requires_review", out var r) && r.ValueKind == JsonValueKind.True ? true
            : root.TryGetProperty("requires_review", out var r2) && r2.ValueKind == JsonValueKind.False ? false : null;
        var sig = root.TryGetProperty("signals", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : -1;
        var texts = root.TryGetProperty("signals", out var s2) && s2.ValueKind == JsonValueKind.Array
            ? s2.EnumerateArray().Select(e => e.GetString() ?? "").Where(t => t.Length > 0).ToList() : [];
        var expl = root.TryGetProperty("explanation", out var e) ? e.GetString() ?? "" : "";
        var down = root.TryGetProperty("downgraded_signals", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(e => e.GetString() ?? "").Where(t => t.Length > 0).ToList() : [];
        var shift = root.TryGetProperty("shift_report_issues", out var sh) && sh.ValueKind == JsonValueKind.Array
            ? sh.EnumerateArray().Select(e => e.GetString() ?? "").Where(t => t.Length > 0).ToList() : [];
        return (rr, sig, texts, expl, down, shift);
    }
    catch (JsonException) { return (null, -1, [], "", [], []); }
}

static string Fmt(bool? v) => v.HasValue ? v.Value.ToString() : "?";

static ReplayOpts ParseArgs(string[] args)
{
    string Get(string name, string def = "")
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }
    return new ReplayOpts(
        Get("--logs", @"C:\AiService\request_logs\2026-09"),
        Get("--out", Path.Combine(Path.GetTempPath(), "agent-replay.json")),
        int.TryParse(Get("--limit", "10"), out var l) ? l : 10,
        Get("--model", "qwen3:14b"),
        Get("--ollama", "http://localhost:11434"),
        int.TryParse(Get("--max-iter", "4"), out var m) ? m : 4,
        int.TryParse(Get("--timeout", "180"), out var t) ? t : 180,
        Get("--match", ""),
        Get("--temp", "0.0"),
        Get("--think", "off"),
        Get("--serial", ""));
}

sealed record ReplayOpts(string Logs, string Out, int Limit, string Model, string Ollama, int MaxIter, int Timeout, string Match, string Temperature, string Thinking, string Serial);
