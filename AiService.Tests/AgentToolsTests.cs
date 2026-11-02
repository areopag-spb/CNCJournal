using AiService.Models;
using AiService.Services;
using System.Text.Json;

namespace AiService.Tests;

/// <summary>
/// Инструменты агентского контура (стадия 1, in-memory поверх AnalyzeRequest).
/// Проверяется: каталог причин (известная/неизвестная/пустая), история детали,
/// прошлые решения, нормативы дня, отчёт мастера + обрезка длинных результатов.
/// </summary>
public class AgentToolsTests
{
    private static AnalyzeRequest Request(params PartContext[] parts) => new()
    {
        Machine = "Станок1",
        ShiftDate = "2026-09-23",
        Parts = [.. parts],
    };

    private static PartContext BnPart() => new()
    {
        PartName = "Корпус",
        Order = "З1",
        Setup = 1,
        SetupTimePlan = 114,
        SetupTimeFact = 0,
        NoSetupHappened = true,
        ProductionRatio = 1.0,
        FinishedCount = 10,
        SingleProductionTimePlan = 5,
        PartsHistory = new PartsHistoryDto
        {
            RecordsFound = 1,
            Lines =
            [
                new PartsHistoryLineDto
                {
                    ShiftDate = "2026-09-20",
                    ProductionRatio = "100%",
                    SetupRatio = "б/н",
                    FinishedCount = 12,
                    AnalystDecision = "ok",
                },
            ],
        },
    };

    [Fact]
    public void Definitions_KnownReasons()
    {
        Assert.Equal(6, AgentTools.Definitions.Count);
        Assert.Contains(AgentTools.Definitions, d => d.Name == "get_reason_semantics");
        Assert.Contains(AgentTools.Definitions, d => d.Name == "get_part_history");
        Assert.Contains(AgentTools.Definitions, d => d.Name == "get_operator_info");
    }

    [Fact]
    public void ReasonSemantics_KnownReason_ReturnsRule()
    {
        var r = AgentTools.Execute(Request(), "get_reason_semantics",
            """{"reason":"Освоение","category":"setup"}""");
        Assert.Contains("ЛЮБУЮ аномалию наладки", r);
        Assert.DoesNotContain("ВНИМАНИЕ", r);
    }

    [Fact]
    public void ReasonSemantics_WrongCategory_Warns()
    {
        // Причина наладки не объясняет изготовление — привязку определяет поле.
        var r = AgentTools.Execute(Request(), "get_reason_semantics",
            """{"reason":"Освоение","category":"machining"}""");
        Assert.Contains("ВНИМАНИЕ", r);
    }

    [Fact]
    public void ReasonSemantics_DowntimeReason_FramedSeparately()
    {
        // Причина простоя — другой справочник: не «неизвестна», а «неприменима к деталям».
        // Иначе модель читает «неизвестна каталогу» как «подозрительна» (пилот 24.09.2026).
        var req = Request(BnPart());
        req.DowntimeReasons.Add("Отсутствие оператора");
        var r = AgentTools.Execute(req, "get_reason_semantics",
            """{"reason":"Отсутствие оператора","category":"setup"}""");
        Assert.Contains("ПРОСТОЯ", r);
        Assert.Contains("самодостаточна", r);
        Assert.DoesNotContain("неизвестна каталогу", r);
    }

    [Fact]
    public void ReasonSemantics_NonSerialMachining_NotJudged()
    {
        // Несерийный станок: причины изготовления не разбираем — изготовление
        // не оценивается. Разбираются только те, что проверяются на любом станке:
        // «не по техпроцессу» (отклонение от технологии) и «Некорректное
        // заполнение» (достоверность данных). Причины про нормативы — нет.
        var req = Request(BnPart());
        req.IsSerialMachine = false;
        var r = AgentTools.Execute(req, "get_reason_semantics",
            """{"reason":"Штучная/длительная работа","category":"machining"}""");
        Assert.Contains("НЕ оценивается", r);

        var normative = AgentTools.Execute(req, "get_reason_semantics",
            """{"reason":"Некорректные нормативы","category":"machining"}""");
        Assert.Contains("НЕ оценивается", normative);

        var missingNormatives = AgentTools.Execute(req, "get_reason_semantics",
            """{"reason":"Отсутствие нормативов","category":"machining"}""");
        Assert.Contains("НЕ оценивается", missingNormatives);

        // Отклонение от технологии и недостоверные данные — разбираются как обычно.
        var techProcess = AgentTools.Execute(req, "get_reason_semantics",
            """{"reason":"Изготовление не по техпроцессу","category":"machining"}""");
        Assert.DoesNotContain("НЕ оценивается", techProcess);

        var badFill = AgentTools.Execute(req, "get_reason_semantics",
            """{"reason":"Некорректное заполнение","category":"machining"}""");
        Assert.DoesNotContain("НЕ оценивается", badFill);

        // Нормативная причина НАЛАДКИ на несерийном разбирается (глушится только изготовление).
        var setupNormatives = AgentTools.Execute(req, "get_reason_semantics",
            """{"reason":"Некорректные нормативы","category":"setup"}""");
        Assert.DoesNotContain("НЕ оценивается", setupNormatives);
    }

    [Theory]
    [InlineData("Деталь§1§З1§замена сверла", true)]
    [InlineData("Деталь§З1", true)] // короткий legacy — не трогаем
    [InlineData("Деталь§Уст.1§З1§замена сверла", false)] // «Уст.» вместо цифры
    [InlineData("Деталь§1§З1§Другое", false)] // голое название причины
    [InlineData("Деталь§1§З1§", false)] // пустая причина
    [InlineData("Деталь§1§З1§отсутствие наладки при наличии заказа", false)] // запрещённая формулировка
    [InlineData("Деталь§1§З1§КПД вне нормы (б/н)", false)] // б/н — отсутствие значения, не аномалия
    public void ExcludeEntry_FormatGate(string entry, bool expected)
    {
        // Пилот 25.09 (QTS350 23.09): «§Уст.1§» обходило привязку, голое «Другое»
        // проходило как конкретика.
        Assert.Equal(expected, ExcludeTriggerValidator.IsWellFormedExcludeEntry(entry));
    }

    private static PartContext DentPart() => new()
    {
        PartName = "Корпус проточной части АРМ1-119П-01-011",
        Order = "УЧ2609-0069.1.1",
        Setup = 1,
        MasterSetupComment = "Другое",
        MasterSetupDetail = "На корпусе очень много вмятин по наружному диаметру (180мм). Реставрация.",
    };

    [Fact]
    public void ExcludeReason_Grounded_Kept()
    {
        Assert.True(ExcludeTriggerValidator.HasGroundedReason(
            DentPart(), "реставрация вмятин корпуса 180мм"));
    }

    [Fact]
    public void ExcludeReason_Confabulated_Dropped()
    {
        // Пилот 25.09 (QTS350 23.09): «внутренняя резьба» выдумана — в текстах
        // только вмятины и реставрация.
        Assert.False(ExcludeTriggerValidator.HasGroundedReason(
            DentPart(), "недостаток заготовки с внутренней резьбой"));
    }

    [Fact]
    public void ReasonSemantics_UnknownReason_ListsCatalog()
    {
        var r = AgentTools.Execute(Request(), "get_reason_semantics",
            """{"reason":"Какая-то ерунда","category":"setup"}""");
        Assert.Contains("неизвестна каталогу", r);
        Assert.Contains("Освоение", r);
    }

    [Fact]
    public void ReasonSemantics_EmptyReason_ListsNonAnomalies()
    {
        // Пустая причина: короткий перечень НЕ-аномалий (одно место вместо трёх повторов промпта).
        var r = AgentTools.Execute(Request(), "get_reason_semantics",
            """{"reason":"","category":"setup"}""");
        Assert.Contains("б/н/б/и", r);
        Assert.Contains("без системного сигнала", r);
    }

    [Fact]
    public void PartHistory_Found()
    {
        var r = AgentTools.Execute(Request(BnPart()), "get_part_history",
            """{"partName":"Корпус","setup":1}""");
        Assert.Contains("2026-09-20", r);
        Assert.Contains("ok", r);
    }

    [Fact]
    public void PartHistory_NoHistory_ConfirmsFirstTime()
    {
        var part = BnPart();
        part.PartsHistory = null;
        var r = AgentTools.Execute(Request(part), "get_part_history",
            """{"partName":"Корпус"}""");
        Assert.Contains("ПОДТВЕРЖДЕНО", r);
    }

    [Fact]
    public void PartHistory_MissingPart()
    {
        var r = AgentTools.Execute(Request(BnPart()), "get_part_history",
            """{"partName":"Неведома зверушка"}""");
        Assert.Contains("нет в данных дня", r);
    }

    [Fact]
    public void PastDecisions_Aggregates()
    {
        var r = AgentTools.Execute(Request(BnPart()), "get_past_decisions", """{"limit":5}""");
        Assert.Contains("2026-09-20", r);
        Assert.Contains("ok", r);
    }

    [Fact]
    public void Normatives_FromDayData()
    {
        // Нормативы — JSON: числа числами, естьЗаказ считает C#.
        var r = AgentTools.Execute(Request(BnPart()), "get_normatives",
            """{"partName":"Корпус"}""");
        using var doc = JsonDocument.Parse(r);
        // BnPart — б/н с планом 114: факт скрыт так же, как в JSON деталей, план виден.
        Assert.Equal(114, doc.RootElement.GetProperty("планНаладки").GetDouble());
        Assert.False(doc.RootElement.TryGetProperty("фактНаладки", out _));
        Assert.True(doc.RootElement.GetProperty("естьЗаказ").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("норматив", out _));
    }

    [Fact]
    public void ReasonSemantics_PartialSetupWithMinutes_Normalized()
    {
        // Кейс QTS350 2026-09-24: модель цитирует «частичная наладка: 38 мин»,
        // точного ключа нет — без нормализации отвечалось «неизвестна каталогу»,
        // что читалось как «подозрительна» и эскалировалось.
        var r = AgentTools.Execute(Request(), "get_reason_semantics",
            """{"reason":"частичная наладка: 38 мин","category":"setup"}""");
        Assert.Contains("Переходная", r);
        Assert.Contains("нормализован", r);
        Assert.DoesNotContain("неизвестна каталогу", r);
    }

    [Fact]
    public void ReasonSemantics_TwoMachineService_ExplainsKpdAndTime()
    {
        // Решение владельца 06.10.2026: двустаночное обслуживание релевантно
        // низкому КПД и росту машинного времени, но не простоям смены.
        var r = AgentTools.Execute(Request(), "get_reason_semantics",
            """{"reason":"Двустаночное обслуживание","category":"machining"}""");
        Assert.Contains("ДЕЛИЛ", r);
        Assert.Contains("ДРУГАЯ причина", r);
        Assert.Contains("Простои смены", r);
        Assert.DoesNotContain("Особых условий нет", r);
    }

    [Theory]
    [InlineData("Зайцев Максим Борисович")]
    [InlineData("Зайцев М.Б.")]
    public void OperatorInfo_ShiftMaster_BlockedBeforeDb(string query)
    {
        // Кейс 06.10.2026, Rontek HTC420 №1 28.09: модель взяла мастера из отчёта
        // смены и получила бы «не найден» — дальше записала «Оператор Зайцев
        // отсутствует в системе». Мастера нет в cnc_operators по построению:
        // запрос не идёт в БД, ответ сразу объясняет роль.
        var req = Request();
        req.ShiftReports.Add(new ShiftReportContext
        {
            Shift = "День",
            ShiftMinutes = 660,
            ReportExists = true,
            Master = "Зайцев Максим Борисович",
        });
        var r = AgentTools.Execute(req, "get_operator_info",
            "{\"query\":\"" + query + "\"}");
        Assert.Contains("мастер смены", r);
        Assert.DoesNotContain("не найден в cnc_operators", r);
    }

    [Fact]
    public void OperatorInfo_SameSurnameDifferentInitials_GoesToDb()
    {
        // Однофамилец без совпадающих инициалов — другой человек, идёт в БД
        // (RequestAgentDataSource: честное «не найден», без выдумки).
        var req = Request();
        req.ShiftReports.Add(new ShiftReportContext
        {
            Shift = "День",
            ShiftMinutes = 660,
            ReportExists = true,
            Master = "Зайцев Максим Борисович",
        });
        var r = AgentTools.Execute(req, "get_operator_info",
            """{"query":"Зайцев А.С."}""");
        Assert.Contains("не найден в cnc_operators", r);
    }

    [Fact]
    public void ReasonSemantics_DrugoeWithDetail_Normalized()
    {
        var r = AgentTools.Execute(Request(), "get_reason_semantics",
            """{"reason":"Другое: ждал деталь с другого станка","category":"setup"}""");
        Assert.Contains("Только если ничего из списка", r);
        Assert.DoesNotContain("неизвестна каталогу", r);
    }

    [Fact]
    public void ShiftReport_NoReports()
    {
        var r = AgentTools.Execute(Request(BnPart()), "get_shift_report", "{}");
        Assert.Contains("проверять нечего", r);
    }

    [Fact]
    public void ShiftReport_WithReport()
    {
        var req = Request(BnPart());
        req.ShiftReports.Add(new ShiftReportContext
        {
            Shift = "День",
            ShiftMinutes = 660,
            ReportExists = true,
            Master = "",
            FreshUnspecifiedDowntimes = 0,
        });
        var r = AgentTools.Execute(req, "get_shift_report", "{}");
        using var doc = JsonDocument.Parse(r);
        Assert.True(doc.RootElement.GetProperty("мастерНеУказан").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("мастер").ValueKind);
    }

    [Fact]
    public void UnknownTool_ListsAvailable()
    {
        var r = AgentTools.Execute(Request(), "no_such_tool", "{}");
        Assert.Contains("Неизвестный инструмент", r);
        Assert.Contains("get_reason_semantics", r);
    }

    [Fact]
    public void LongResult_Truncated()
    {
        // Длинный результат режется до MaxResultChars с маркером.
        var parts = Enumerable.Range(1, 3).Select(i => new PartContext
        {
            PartName = $"Деталь{i}",
            Order = "З1",
            Setup = 1,
            PartsHistory = new PartsHistoryDto
            {
                RecordsFound = 5,
                Lines = Enumerable.Range(1, 5).Select(d => new PartsHistoryLineDto
                {
                    ShiftDate = $"2026-09-{d:00}",
                    ProductionRatio = "50%",
                    SetupRatio = "50%",
                    FinishedCount = 1,
                    AnalystDecision = "escalated",
                    AiFeedback = new string('я', 500),
                }).ToList(),
            },
        }).ToArray();
        var r = AgentTools.Execute(Request(parts), "get_past_decisions", """{"limit":10}""");
        Assert.True(r.Length <= AgentTools.MaxResultChars + 20);
        Assert.EndsWith("[обрезано]", r);
    }
}
