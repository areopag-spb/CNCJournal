using AiService.Models;
using AiService.Services;
using System.Text.Json;

namespace AiService.Tests;

/// <summary>
/// Детали дня — JSON, а не проза (кейс QTS350 2026-09-24: «plan=0 при наличии
/// заказа» для Без М/Л-строки — модель выпарсила заказ из строки М/Л).
/// Проверяется: естьЗаказ считает C# (пусто/«Без М/Л» = false), КПД — число или
/// null + флаг, несерийный — изготовлениеНеОценивается, кириллица без \u-последовательностей.
/// </summary>
public class AgentPartsJsonTests
{
    private static AnalyzeRequest Request(bool serial, params PartContext[] parts) => new()
    {
        Machine = "Станок1",
        ShiftDate = "2026-09-24",
        IsSerialMachine = serial,
        Parts = [.. parts],
    };

    private static PartContext Part(string name, string order, double? setupKpd, double plan) => new()
    {
        PartName = name,
        Order = order,
        Setup = 1,
        SetupRatio = setupKpd,
        NoSetupHappened = setupKpd is null,
        ProductionRatio = 0.8,
        SetupTimePlan = plan,
        SetupTimeFact = plan,
        SingleProductionTimePlan = 30,
    };

    private static JsonElement Render(bool serial, params PartContext[] parts)
    {
        var json = AgentLoopService.RenderPartsJson(Request(serial, parts));
        // Кириллица — как есть: \u-последовательности жрут токены.
        Assert.DoesNotContain(@"\u", json);
        return JsonDocument.Parse(json).RootElement.GetProperty("parts");
    }

    [Fact]
    public void HasOrder_BezMlAndEmpty_False()
    {
        var parts = Render(true,
            Part("Кольцо АРМ2-49.3-01-041", "УЧ2608-0028.1.1", 0.45, 86),
            Part("Кольцо АРМ2-49.3-01-041 расточка кулачков", "Без М/Л", null, 0),
            Part("Втулка", "", null, 0));

        Assert.True(parts[0].GetProperty("естьЗаказ").GetBoolean());
        Assert.False(parts[1].GetProperty("естьЗаказ").GetBoolean());
        Assert.False(parts[2].GetProperty("естьЗаказ").GetBoolean());
    }

    [Fact]
    public void Kpd_NumberOrNullWithFlag()
    {
        var parts = Render(true,
            Part("Кольцо", "З1", 0.45, 86),
            Part("Втулка", "Без М/Л", null, 0));

        Assert.Equal(0.45, parts[0].GetProperty("кпдНаладки").GetDouble());
        Assert.False(parts[0].GetProperty("наладкиНеБыло").GetBoolean());
        Assert.Equal(JsonValueKind.Null, parts[1].GetProperty("кпдНаладки").ValueKind);
        Assert.True(parts[1].GetProperty("наладкиНеБыло").GetBoolean());
    }

    [Fact]
    public void NonSerial_ProductionNotEvaluated()
    {
        var parts = Render(false, Part("Кольцо", "З1", 0.45, 86));

        Assert.True(parts[0].GetProperty("изготовлениеНеОценивается").GetBoolean());
        Assert.Equal(JsonValueKind.Null, parts[0].GetProperty("кпдИзготовления").ValueKind);
        Assert.True(parts[0].GetProperty("изготовленияНеБыло").GetBoolean());
    }

    [Fact]
    public void EmptyTexts_Omitted()
    {
        // Пустые комментарии/причины в JSON не попадают — только непустые.
        var parts = Render(true, Part("Кольцо", "З1", 0.45, 86));
        Assert.False(parts[0].TryGetProperty("причинаОтклоненияВНаладке", out _));
        Assert.False(parts[0].TryGetProperty("комментарийОператора", out _));
    }

    [Fact]
    public void NoSetupWithPlan_FactHidden_PlanKept()
    {
        // Октябрь 2026: пара «план>0 + факт=0» при б/н провоцировала «отсутствие наладки».
        var parts = Render(true, Part("Кольцо", "З1", null, 86));

        // План остаётся: без него модель читает «плана нет» и пишет «отсутствие плана наладки».
        Assert.Equal(86, parts[0].GetProperty("планНаладки").GetDouble());
        Assert.False(parts[0].TryGetProperty("фактНаладки", out _));
        Assert.True(parts[0].GetProperty("наладкиНеБыло").GetBoolean());
    }

    [Fact]
    public void NoSetupWithZeroPlan_FactKept()
    {
        // План=0 при заказе — отдельная аномалия «норматив отсутствует», её скрывать нельзя.
        var parts = Render(true, Part("Кольцо", "З1", null, 0));

        Assert.Equal(0, parts[0].GetProperty("планНаладки").GetDouble());
        Assert.Equal(0, parts[0].GetProperty("фактНаладки").GetDouble());
    }

    [Fact]
    public void SetupHappened_FactKept()
    {
        var parts = Render(true, Part("Кольцо", "З1", 0.45, 86));

        Assert.Equal(86, parts[0].GetProperty("планНаладки").GetDouble());
        Assert.Equal(86, parts[0].GetProperty("фактНаладки").GetDouble());
    }

    [Fact]
    public void History_PresentOnlyWhereRelevant()
    {
        var mastering = Part("Кольцо", "З1", 0.45, 86);
        mastering.MasterSetupComment = "Освоение";
        mastering.PartsHistory = new PartsHistoryDto
        {
            RecordsFound = 1,
            Lines = { new PartsHistoryLineDto { ShiftDate = "2026-09-01", FinishedCount = 5, AnalystDecision = "ok" } },
        };
        var plain = Part("Втулка", "З2", 0.45, 86);
        plain.PartsHistory = new PartsHistoryDto
        {
            RecordsFound = 1,
            Lines = { new PartsHistoryLineDto { ShiftDate = "2026-09-01", FinishedCount = 5 } },
        };

        var parts = Render(true, mastering, plain);

        var h = parts[0].GetProperty("историяДетали");
        Assert.Equal(1, h.GetProperty("прошлыхСмен").GetInt32());
        Assert.Equal(5, h.GetProperty("строки")[0].GetProperty("готовоШт").GetInt32());
        Assert.False(parts[1].TryGetProperty("историяДетали", out _));
    }

    [Fact]
    public void History_EmptyConfirmsFirstTime_NullOmitted()
    {
        var empty = Part("Кольцо", "З1", 0.45, 86);
        empty.MasterSetupComment = "Освоение";
        empty.PartsHistory = new PartsHistoryDto { RecordsFound = 0 };
        var unknown = Part("Втулка", "З2", 0.45, 86);
        unknown.MasterSetupComment = "Освоение";

        var parts = Render(true, empty, unknown);

        var h = parts[0].GetProperty("историяДетали");
        Assert.Equal(0, h.GetProperty("прошлыхСмен").GetInt32());
        Assert.Contains("ПОДТВЕРЖДЕНО", h.GetProperty("вывод").GetString());
        Assert.False(parts[1].TryGetProperty("историяДетали", out _));
    }

    [Fact]
    public void PartialSetupMinutes_VisiblePerRow()
    {
        // Кейс 24.09.2026, SKT21 «Гайка нажимная…»: частичная 3 мин была во
        // ВТОРОЙ строке, а модель без этого числа не могла отличить строки.
        var first = Part("Гайка", "З1", null, 86);
        first.PartialSetup = 0;
        var second = Part("Гайка", "З1", null, 57);
        second.PartialSetup = 3;

        var parts = Render(true, first, second);

        Assert.Equal(0, parts[0].GetProperty("частичнаяНаладкиМин").GetDouble());
        Assert.Equal(3, parts[1].GetProperty("частичнаяНаладкиМин").GetDouble());
    }
}
