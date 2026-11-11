using AiService.Models;
using AiService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiService.Tests;

/// <summary>
/// Агентский цикл (стадия 1: каркас, прод не затрагивается).
/// Chat-клиент — заглушка: проверяются ветки «финал сразу», «один tool-раунд»,
/// «лимит итераций → fallback», «сбой → fallback», плюс загрузка скелета промпта.
/// </summary>
public class AgentLoopServiceTests
{
    private sealed class StubChat(Func<IReadOnlyList<ChatMessage>, ChatResult> next) : IAgentChatClient
    {
        public List<IReadOnlyList<ChatMessage>> SeenMessages { get; } = [];
        public List<bool> SeenThink { get; } = [];
        public int Calls { get; private set; }

        public Task<ChatResult> ChatAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
            string? format, bool think, CancellationToken ct,
            string? model = null, double? temperature = null,
            IProgress<string>? thinkingProgress = null,
            IProgress<string>? tokenProgress = null)
        {
            Calls++;
            SeenMessages.Add(messages);
            SeenThink.Add(think);
            return Task.FromResult(next(messages));
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
    {
        var dict = values.ToDictionary(v => v.Key, v => (string?)v.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    private static AnalyzeRequest Request() => new()
    {
        Machine = "Станок1",
        ShiftDate = "2026-09-23",
        Parts =
        [
            new PartContext
            {
                PartName = "Корпус",
                Order = "З1",
                Setup = 1,
                MasterSetupComment = "Освоение",
                SetupRatio = 0.45,
                SetupTimePlan = 100,
                SetupTimeFact = 200,
            },
        ],
    };

    private static AgentLoopService Service(StubChat stub, IConfiguration? config = null) =>
        new(stub,
            new PromptBuilder(NullLogger<PromptBuilder>.Instance),
            config ?? Config(),
            NullLogger<AgentLoopService>.Instance,
            RequestAgentDataSource.Instance);

    private static readonly HardRuleResult NoHard = new([], []);
    private static readonly ShiftReportRuleResult NoShift = new([], []);

    [Fact]
    public async Task FinalWithoutTools_ReturnsJson()
    {
        var stub = new StubChat(_ => new ChatResult
        {
            Content = """{"requires_review":false}""",
        });
        var outcome = await Service(stub).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.Equal("""{"requires_review":false}""", outcome.FinalJson);
        Assert.False(outcome.Trace.FellBack);
        Assert.Equal(1, outcome.Trace.IterationsUsed);
        Assert.Empty(outcome.Trace.Rounds);
        Assert.EndsWith("@agent", outcome.Trace.PromptVersion);
    }

    [Fact]
    public async Task NoParts_StatesNoWorkExplicitly_AndGivesNoTools()
    {
        var req = Request();
        req.Parts = [];
        IReadOnlyList<ChatTool>? seenTools = null;
        var stub = new ToolCapturingChat(t => seenTools = t, """{"requires_review":false}""");
        var service = new AgentLoopService(stub,
            new PromptBuilder(NullLogger<PromptBuilder>.Instance), Config(),
            NullLogger<AgentLoopService>.Instance, RequestAgentDataSource.Instance);

        await service.AnalyzeAsync(req, NoHard, NoShift, CancellationToken.None);

        Assert.NotNull(seenTools);
        Assert.Empty(seenTools!);
        var user = stub.UserMessage!;
        Assert.Contains("ЗАПИСЕЙ ЗА СУТКИ НЕТ", user);
        Assert.DoesNotContain("\"parts\"", user);
    }

    private sealed class ToolCapturingChat(Action<IReadOnlyList<ChatTool>> onTools, string json) : IAgentChatClient
    {
        public string? UserMessage { get; private set; }

        public Task<ChatResult> ChatAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
            string? format, bool think, CancellationToken ct,
            string? model = null, double? temperature = null,
            IProgress<string>? thinkingProgress = null,
            IProgress<string>? tokenProgress = null)
        {
            onTools(tools);
            UserMessage = messages.First(m => m.Role == "user").Content;
            return Task.FromResult(new ChatResult { Content = json });
        }
    }

    private static AnalyzeRequest RequestWithSignal()
    {
        var req = Request();
        req.Parts[0].Signals = ["КПД наладки 45% без объяснения"];
        return req;
    }

    [Fact]
    public async Task VerdictWithoutTools_OnSignalWithReason_IsNudgedOnce()
    {
        var calls = 0;
        var stub = new StubChat(_ => ++calls switch
        {
            1 => new ChatResult { Content = """{"requires_review":false}""" },
            2 => new ChatResult
            {
                ToolCalls =
                [
                    new AgentToolCall
                    {
                        Id = "c1", Name = "get_reason_semantics",
                        Arguments = """{"reason":"Освоение","category":"setup"}""",
                    },
                ],
            },
            _ => new ChatResult { Content = """{"requires_review":true}""" },
        });

        var outcome = await Service(stub).AnalyzeAsync(RequestWithSignal(), NoHard, NoShift, CancellationToken.None);

        Assert.Equal("""{"requires_review":true}""", outcome.FinalJson);
        Assert.True(outcome.Trace.ContextNudged);
        Assert.Single(outcome.Trace.Rounds);
        // SeenMessages хранит ссылку на общий список: толчок — последняя user-реплика.
        var nudge = stub.SeenMessages[1].Last(m => m.Role == "user");
        Assert.Contains("«Освоение» (setup)", nudge.Content);
    }

    [Fact]
    public async Task Nudge_IsOnlyOnce_SecondVerdictAccepted()
    {
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });

        var outcome = await Service(stub).AnalyzeAsync(RequestWithSignal(), NoHard, NoShift, CancellationToken.None);

        Assert.Equal(2, stub.Calls);
        Assert.True(outcome.Trace.ContextNudged);
        Assert.Equal("""{"requires_review":false}""", outcome.FinalJson);
        Assert.False(outcome.Trace.FellBack);
    }

    [Fact]
    public async Task NoNudge_WithoutSignals_OrWhenHardDecided()
    {
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });
        var noSignals = await Service(stub).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);
        Assert.Equal(1, stub.Calls);
        Assert.False(noSignals.Trace.ContextNudged);

        var hardStub = new StubChat(_ => new ChatResult { Content = """{"requires_review":true}""" });
        var hard = new HardRuleResult(["[Д] hard"], []);
        var decided = await Service(hardStub).AnalyzeAsync(RequestWithSignal(), hard, NoShift, CancellationToken.None);
        Assert.Equal(1, hardStub.Calls);
        Assert.False(decided.Trace.ContextNudged);
    }

    [Fact]
    public async Task InlineReasons_PutSemanticsIntoMessage_AndSkipNudge()
    {
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });

        var outcome = await Service(stub, Config(("Agent:InlineReasonSemantics", "true")))
            .AnalyzeAsync(RequestWithSignal(), NoHard, NoShift, CancellationToken.None);

        Assert.Equal(1, stub.Calls);
        Assert.False(outcome.Trace.ContextNudged);
        Assert.Equal(1, outcome.Trace.ReasonsInlined);
        var user = stub.SeenMessages[0].First(m => m.Role == "user").Content!;
        Assert.Contains("Смысл причин отклонения", user);
        Assert.Contains("наладка: «Освоение»", user);
        Assert.Contains("ЛЮБУЮ аномалию наладки", user);
    }

    [Fact]
    public async Task InlineReasons_NothingToInline_MessageUnchanged()
    {
        // Без сигналов на детали и при hard-дне приложить нечего: сообщение как без флага.
        var plain = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });
        await Service(plain).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);
        var inline = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });
        var outcome = await Service(inline, Config(("Agent:InlineReasonSemantics", "true")))
            .AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.Equal(0, outcome.Trace.ReasonsInlined);
        Assert.Equal(
            plain.SeenMessages[0].First(m => m.Role == "user").Content,
            inline.SeenMessages[0].First(m => m.Role == "user").Content);

        var hardStub = new StubChat(_ => new ChatResult { Content = """{"requires_review":true}""" });
        var hard = new HardRuleResult(["[Д] hard"], []);
        var decided = await Service(hardStub, Config(("Agent:InlineReasonSemantics", "true")))
            .AnalyzeAsync(RequestWithSignal(), hard, NoShift, CancellationToken.None);
        Assert.Equal(0, decided.Trace.ReasonsInlined);
        Assert.DoesNotContain("Смысл причин отклонения",
            hardStub.SeenMessages[0].First(m => m.Role == "user").Content);
    }

    [Fact]
    public async Task InlineReasons_OffByDefault_NudgeStillWorks()
    {
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });

        var outcome = await Service(stub).AnalyzeAsync(RequestWithSignal(), NoHard, NoShift, CancellationToken.None);

        Assert.True(outcome.Trace.ContextNudged);
        Assert.Equal(0, outcome.Trace.ReasonsInlined);
        Assert.DoesNotContain("Смысл причин отклонения",
            stub.SeenMessages[0].First(m => m.Role == "user").Content);
    }

    private static AnalyzeRequest EmptyDay(params ShiftReportContext[] reports)
    {
        var req = Request();
        req.Parts = [];
        req.DowntimeReasons = ["Нет заказов", "Другое"];
        req.ShiftReports = [.. reports];
        return req;
    }

    private static ShiftReportContext FullDowntime(string shift, string reason, string comment = "") => new()
    {
        Shift = shift,
        ReportExists = true,
        ShiftMinutes = 660,
        FreshUnspecifiedDowntimes = 660,
        StoredUnspecifiedDowntimes = 660,
        DowntimeReason = reason,
        MasterComment = comment,
        Master = "Мастер М.М.",
        HasParts = false,
    };

    [Fact]
    public async Task SkipModel_EmptyDayDecidedBySystem_NoModelCall()
    {
        var stub = new StubChat(_ => throw new InvalidOperationException("модель не должна вызываться"));
        var req = EmptyDay(FullDowntime("День", "Нет заказов"), FullDowntime("Ночь", "Нет заказов"));

        var outcome = await Service(stub, Config(("Agent:SkipModelWhenNothingToJudge", "true")))
            .AnalyzeAsync(req, NoHard, ShiftReportRuleEvaluator.Evaluate(req), CancellationToken.None);

        Assert.Equal(0, stub.Calls);
        Assert.True(outcome.Trace.ModelSkipped);
        Assert.False(outcome.Trace.FellBack);
        Assert.Equal(AgentLoopService.NothingToJudgeJson, outcome.FinalJson);
        Assert.False(outcome.Trace.BaseRequiresReview);
    }

    [Fact]
    public async Task SkipModel_NotApplied_WhenModelMustJudgeOrFlagOff()
    {
        // Частичный простой с причиной и комментарием — релевантность судит модель.
        var partial = FullDowntime("День", "Нет заказов", "ждали заготовки");
        partial.FreshUnspecifiedDowntimes = 300;
        partial.StoredUnspecifiedDowntimes = 300;
        var needsModel = EmptyDay(partial, FullDowntime("Ночь", "Нет заказов"));
        Assert.Contains(ShiftReportRuleEvaluator.AgentPreverdicts(needsModel, ShiftReportRuleEvaluator.Evaluate(needsModel)),
            p => p.NeedsModel);
        Assert.False(AgentLoopService.NothingToJudge(needsModel, ShiftReportRuleEvaluator.Evaluate(needsModel)));

        // Есть записи — судит модель.
        Assert.False(AgentLoopService.NothingToJudge(Request(), NoShift));

        // Есть сигнал дня — судит модель.
        var withDaySignal = EmptyDay(FullDowntime("День", "Нет заказов"));
        withDaySignal.Signals = ["сигнал дня"];
        Assert.False(AgentLoopService.NothingToJudge(withDaySignal, ShiftReportRuleEvaluator.Evaluate(withDaySignal)));

        // Флаг выключен (дефолт) — модель вызывается как раньше.
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });
        var req = EmptyDay(FullDowntime("День", "Нет заказов"));
        var outcome = await Service(stub).AnalyzeAsync(req, NoHard, ShiftReportRuleEvaluator.Evaluate(req), CancellationToken.None);
        Assert.Equal(1, stub.Calls);
        Assert.False(outcome.Trace.ModelSkipped);
    }

    [Fact]
    public void NudgeReasons_NonSerial_SkipsMachining()
    {
        var req = RequestWithSignal();
        req.IsSerialMachine = false;
        req.Parts[0].MasterMachiningComment = "Некорректные нормативы";

        var reasons = AgentLoopService.ContextNudgeReasons(req, NoHard, NoShift);

        Assert.Equal([("Освоение", "setup")], reasons);
    }

    [Fact]
    public async Task OneToolRound_ExecutesAndReturnsFinal()
    {
        var calls = 0;
        var stub = new StubChat(_ => ++calls == 1
            ? new ChatResult
            {
                Content = "",
                ToolCalls =
                [
                    new AgentToolCall
                    {
                        Id = "c1",
                        Name = "get_reason_semantics",
                        Arguments = """{"reason":"Освоение","category":"setup"}""",
                    },
                ],
            }
            : new ChatResult { Content = """{"requires_review":true}""" });

        var outcome = await Service(stub).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.Equal("""{"requires_review":true}""", outcome.FinalJson);
        Assert.False(outcome.Trace.FellBack);
        Assert.Single(outcome.Trace.Rounds);
        Assert.Equal("get_reason_semantics", outcome.Trace.Rounds[0].Name);
        // Второй раунд видит assistant-запрос и результат tool в истории сообщений.
        Assert.Equal(4, stub.SeenMessages[1].Count);
        Assert.Equal("tool", stub.SeenMessages[1][3].Role);
        Assert.Contains("ЛЮБУЮ аномалию наладки", stub.SeenMessages[1][3].Content);
    }

    [Fact]
    public async Task AlwaysTools_FallsBackByLimit()
    {
        var stub = new StubChat(_ => new ChatResult
        {
            ToolCalls = [new AgentToolCall { Id = "c", Name = "get_shift_report", Arguments = "{}" }],
        });
        var svc = Service(stub, Config(("Agent:MaxIterations", "2")));

        var outcome = await svc.AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.Null(outcome.FinalJson);
        Assert.True(outcome.Trace.FellBack);
        Assert.Contains("Лимит итераций (2)", outcome.Trace.FallbackReason);
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task ChatThrows_FallsBack()
    {
        var stub = new StubChat(_ => throw new InvalidOperationException("бум"));
        var outcome = await Service(stub).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.Null(outcome.FinalJson);
        Assert.True(outcome.Trace.FellBack);
        Assert.Contains("бум", outcome.Trace.FallbackReason);
    }

    [Fact]
    public async Task UnknownToolName_ReturnsErrorResult_Continues()
    {
        // Неизвестный tool — не падение цикла: исполнитель возвращает подсказку,
        // модель получает её следующим сообщением.
        var calls = 0;
        var stub = new StubChat(msgs =>
        {
            if (++calls == 1)
                return new ChatResult
                {
                    ToolCalls = [new AgentToolCall { Id = "c", Name = "no_such_tool", Arguments = "{}" }],
                };
            return new ChatResult { Content = """{"requires_review":false}""" };
        });

        var outcome = await Service(stub).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.False(outcome.Trace.FellBack);
        Assert.Equal("no_such_tool", outcome.Trace.Rounds[0].Name);
        Assert.Contains("Неизвестный инструмент", stub.SeenMessages[1][3].Content);
    }

    [Fact]
    public async Task Progress_ReportsRoundsAndTools()
    {
        var calls = 0;
        var stub = new StubChat(_ => ++calls == 1
            ? new ChatResult
            {
                ToolCalls =
                [
                    new AgentToolCall
                    {
                        Id = "c1",
                        Name = "get_part_history",
                        Arguments = """{"partName":"Корпус"}""",
                    },
                ],
            }
            : new ChatResult { Content = """{"requires_review":false}""" });

        var steps = new List<string>();
        var progress = new Progress<string>(s => steps.Add(s));
        var outcome = await Service(stub).AnalyzeAsync(
            Request(), NoHard, NoShift, CancellationToken.None, progress);

        Assert.False(outcome.Trace.FellBack);
        Assert.Contains(steps, s => s.Contains("Раунд 1"));
        Assert.Contains(steps, s => s.Contains("Корпус"));
        Assert.Contains(steps, s => s.Contains("Вердикт"));
    }

    [Fact]
    public void DescribeCall_Humanizes()
    {
        Assert.Contains("Освоение", AgentLoopService.DescribeCall(
            "get_reason_semantics", """{"reason":"Освоение","category":"setup"}"""));
        Assert.Contains("отчёты мастера", AgentLoopService.DescribeCall(
            "get_shift_report", "{}").ToLowerInvariant());
    }

    [Fact]
    public async Task ThinkingFlag_PassedToChat_AndCharsAccumulated()
    {
        var stub = new StubChat(_ => new ChatResult
        {
            Content = """{"requires_review":false}""",
            Thinking = new string('д', 100),
        });
        var svc = Service(stub, Config(("Agent:Thinking", "true")));

        var outcome = await svc.AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.True(stub.SeenThink[0]);
        Assert.Equal(100, outcome.Trace.ThinkingChars);
        Assert.Contains("д", outcome.Trace.ThinkingExcerpt);
    }

    [Fact]
    public async Task ThinkingFlag_DefaultOff()
    {
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });

        await Service(stub).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.False(stub.SeenThink[0]);
    }

    [Fact]
    public async Task ThinkingAuto_SkippedOnHard_UsedOtherwise()
    {
        // "auto": hard-эскалация — вердикт предрешён, thinking не платим.
        var stubHard = new StubChat(_ => new ChatResult { Content = """{"requires_review":true}""" });
        var hard = new HardRuleResult(["[Д] hard"], []);
        await Service(stubHard, Config(("Agent:Thinking", "auto")))
            .AnalyzeAsync(Request(), hard, NoShift, CancellationToken.None);
        Assert.False(stubHard.SeenThink[0]);

        var stubClean = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });
        await Service(stubClean, Config(("Agent:Thinking", "auto")))
            .AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);
        Assert.True(stubClean.SeenThink[0]);
    }

    [Fact]
    public async Task ThinkingProgress_ForwardedToChat()
    {
        IProgress<string>? seen = null;
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });
        // Заглушка ниже не принимает thinkingProgress — проверяем через обёртку.
        var fwd = new ForwardStub(stub, p => seen = p);
        var svc = new AgentLoopService(fwd,
            new PromptBuilder(NullLogger<PromptBuilder>.Instance),
            Config(("Agent:Thinking", "true")),
            NullLogger<AgentLoopService>.Instance,
            RequestAgentDataSource.Instance);

        var think = new Progress<string>(_ => { });
        await svc.AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None, null, think);

        Assert.Same(think, seen);
    }

    private sealed class VerifierStub(
        string baseJson,
        string[] verifierJsons,
        List<string> seenPrompts) : IAgentChatClient
    {
        public Task<ChatResult> ChatAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
            string? format, bool think, CancellationToken ct,
            string? model = null, double? temperature = null,
            IProgress<string>? thinkingProgress = null,
            IProgress<string>? tokenProgress = null)
        {
            // Первый вызов без tools и короткий — это верифаер (у базового есть tools).
            if (tools.Count == 0)
            {
                seenPrompts.Add(messages.Last().Content ?? "");
                var next = verifierJsons[seenPrompts.Count - 1];
                return Task.FromResult(new ChatResult { Content = next });
            }
            return Task.FromResult(new ChatResult { Content = baseJson });
        }
    }

    [Fact]
    public async Task VerifyFalse_FalseBase_TrueVerifier_Escalates()
    {
        var seen = new List<string>();
        var stub = new VerifierStub(
            """{"requires_review":false,"signals":[],"explanation":"ok"}""",
            ["""{"requires_review":true,"signals":["КПД наладки 40% без объяснения"],"explanation":"плохо"}"""],
            seen);
        var svc = new AgentLoopService(stub,
            new PromptBuilder(NullLogger<PromptBuilder>.Instance),
            Config(("Agent:VerifyFalse", "true"), ("Agent:VerifyRounds", "1")),
            NullLogger<AgentLoopService>.Instance,
            RequestAgentDataSource.Instance);

        var outcome = await svc.AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.Single(outcome.VerifierJsons);
        Assert.Contains("40%", outcome.VerifierJsons[0]);
        Assert.Single(seen);
    }

    [Fact]
    public async Task VerifyFalse_DisabledByDefault_NoExtraCalls()
    {
        var calls = 0;
        var stub = new StubChat(_ =>
        {
            calls++;
            return new ChatResult { Content = """{"requires_review":false}""" };
        });

        await Service(stub).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task VerifyFalse_TrueBase_Skipped()
    {
        // Базовый True не перепроверяем — лишняя эскалация приемлема.
        var seen = new List<string>();
        var stub = new VerifierStub(
            """{"requires_review":true,"signals":["x"],"explanation":"плохо"}""",
            [],
            seen);
        var svc = new AgentLoopService(stub,
            new PromptBuilder(NullLogger<PromptBuilder>.Instance),
            Config(("Agent:VerifyFalse", "true")),
            NullLogger<AgentLoopService>.Instance,
            RequestAgentDataSource.Instance);

        var outcome = await svc.AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.Empty(outcome.VerifierJsons);
        Assert.Empty(seen);
    }

    private sealed class ForwardStub(StubChat inner, Action<IProgress<string>?> capture) : IAgentChatClient
    {
        public Task<ChatResult> ChatAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
            string? format, bool think, CancellationToken ct,
            string? model = null, double? temperature = null,
            IProgress<string>? thinkingProgress = null,
            IProgress<string>? tokenProgress = null)
        {
            capture(thinkingProgress);
            return inner.ChatAsync(messages, tools, format, think, ct, model, temperature, thinkingProgress);
        }
    }

    [Fact]
    public void BuildAgentSystem_LoadsSkeletonWithSuffix()
    {
        var pb = new PromptBuilder(NullLogger<PromptBuilder>.Instance);
        var built = pb.BuildAgentSystem();

        Assert.EndsWith("@agent", built.Version);
        Assert.Contains("TOOLS", built.Prompt);
        Assert.Contains("get_reason_semantics", built.Prompt);
    }

    [Fact]
    public async Task UserMessage_NonSerial_MarksAndHidesProductionKpd()
    {
        // Несерийный станок: маркер в шапке + скрытые числа изготовления.
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });
        var req = Request();
        req.IsSerialMachine = false;
        req.Parts[0].ProductionRatio = 0.44;
        req.Parts[0].FinishedCount = 5;

        await Service(stub).AnalyzeAsync(req, NoHard, NoShift, CancellationToken.None);

        var user = stub.SeenMessages[0][1].Content ?? "";
        Assert.Contains("[несерийный", user);
        Assert.Contains("не оценивается", user);
        Assert.DoesNotContain("КПД изготовления=", user);
    }

    [Fact]
    public async Task UserMessage_ContainsCommentsAndDowntimes()
    {
        // Аудит 25.09: модель судила релевантность текстов, которых не видела.
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });
        var req = Request();
        req.Parts[0].OperatorComment = "уборка станка";
        req.Parts[0].MasterSetupDetail = "замена сверла";
        req.Parts[0].SpecifiedDowntimesComment = "простой по вине цеха";

        await Service(stub).AnalyzeAsync(req, NoHard, NoShift, CancellationToken.None);

        var user = stub.SeenMessages[0][1].Content ?? "";
        Assert.Contains("уборка станка", user);
        Assert.Contains("замена сверла", user);
        Assert.Contains("по вине цеха", user);
    }

    [Fact]
    public async Task SystemMessage_WithSoftSignals_AppendsSoftRules()
    {
        // Аудит 25.09: soft-правила в агентском пути отсутствовали.
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":false}""" });
        var hard = new HardRuleResult([], ["[Д] soft-сигнал"]);

        await Service(stub).AnalyzeAsync(Request(), hard, NoShift, CancellationToken.None);

        var system = stub.SeenMessages[0][0].Content ?? "";
        Assert.Contains("SOFT", system);
    }

    [Fact]
    public async Task UserMessage_ContainsHardSignals_AndNoKpdFrameForBn()
    {
        // A/B кейс Mazak QTS200ML 2026-09-21: агент не видел hard-сигналы.
        // A/B кейс Goodway 2026-09-23: рендер «КПД=б/н» провоцировал б/н-галлюцинацию.
        var stub = new StubChat(_ => new ChatResult { Content = """{"requires_review":true}""" });
        var hard = new HardRuleResult(
            ["[Д] Причина требует пересмотра технологии: «Некорректные нормативы»"], []);
        var req = Request();
        req.Parts[0].NoSetupHappened = true;
        req.Parts[0].SetupRatio = null;

        await Service(stub).AnalyzeAsync(req, hard, NoShift, CancellationToken.None);

        var user = stub.SeenMessages[0][1].Content ?? "";
        Assert.Contains("HARD: [Д] Причина требует пересмотра", user);
        Assert.Contains(@"""наладкиНеБыло"":true", user);
        Assert.Contains(@"""кпдНаладки"":null", user);
        Assert.DoesNotContain("б/н", user);
    }

    // ── P2.3: один повтор после кратковременного сбоя ──

    private static IConfiguration FastRetry(params (string Key, string Value)[] extra) =>
        Config([("Agent:RetryDelayMs", "0"), .. extra]);

    [Fact]
    public async Task TransientFailure_RetriedOnce_ThenSucceeds()
    {
        var calls = 0;
        var stub = new StubChat(_ => ++calls == 1
            ? throw new HttpRequestException("обрыв связи")
            : new ChatResult { Content = """{"requires_review":false}""" });

        var outcome = await Service(stub, FastRetry()).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.False(outcome.Trace.FellBack);
        Assert.Equal("""{"requires_review":false}""", outcome.FinalJson);
        Assert.Equal(1, outcome.Trace.Retries);
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task TransientFailure_SecondFailure_FallsBackWithBothReasons()
    {
        var stub = new StubChat(_ => throw new HttpRequestException("обрыв связи"));

        var outcome = await Service(stub, FastRetry()).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.True(outcome.Trace.FellBack);
        Assert.Equal(1, outcome.Trace.Retries);
        Assert.Equal(2, stub.Calls);
        Assert.Contains("после повтора", outcome.Trace.FallbackReason);
        Assert.Contains("первая попытка", outcome.Trace.FallbackReason);
    }

    [Fact]
    public async Task IterationLimit_NotRetried()
    {
        var stub = new StubChat(_ => new ChatResult
        {
            Content = "",
            ToolCalls = [new AgentToolCall { Id = "c", Name = "get_reason_semantics", Arguments = """{"reason":"Освоение","category":"setup"}""" }],
        });

        var outcome = await Service(stub, FastRetry(("Agent:MaxIterations", "2")))
            .AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.True(outcome.Trace.FellBack);
        Assert.Equal(0, outcome.Trace.Retries);
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task EmptyFinal_NotRetried()
    {
        var stub = new StubChat(_ => new ChatResult { Content = "" });

        var outcome = await Service(stub, FastRetry()).AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.True(outcome.Trace.FellBack);
        Assert.Equal(0, outcome.Trace.Retries);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task RetryDisabledByConfig_NoSecondAttempt()
    {
        var stub = new StubChat(_ => throw new HttpRequestException("обрыв связи"));

        var outcome = await Service(stub, FastRetry(("Agent:RetryOnFailure", "false")))
            .AnalyzeAsync(Request(), NoHard, NoShift, CancellationToken.None);

        Assert.True(outcome.Trace.FellBack);
        Assert.Equal(0, outcome.Trace.Retries);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task Cancelled_NotRetried()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubChat(_ =>
        {
            cts.Cancel();
            throw new HttpRequestException("обрыв связи");
        });

        var outcome = await Service(stub, FastRetry()).AnalyzeAsync(Request(), NoHard, NoShift, cts.Token);

        Assert.True(outcome.Trace.FellBack);
        Assert.Equal(1, stub.Calls);
    }
}
