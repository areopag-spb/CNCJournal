using AiService.Models;
using AiService.Services;

namespace AiService.Tests;

/// <summary>
/// Какие авто-исключения из К1 разрешены (решение владельца 03.11.2026): подтверждённое «Освоение»
/// при КПД наладки &lt; 100%, причины мастера из закрытого списка, явное «исключить» мастера;
/// отмеченные простои игнорируются; свободный текст модели — вне списков (клиент уводит сутки человеку).
/// </summary>
public class AutoExcludePolicyTests
{
    private static PartContext Part(string name = "Корпус", string setupReason = "", string machReason = "",
        double? setupRatio = 0.6, double? prodRatio = 0.8, string masterDetail = "", string downtimes = "")
        => new()
        {
            PartName = name,
            Setup = 1,
            Order = "УЧ2610-0001.1.1",
            MasterSetupComment = setupReason,
            MasterMachiningComment = machReason,
            MasterSetupDetail = masterDetail,
            SetupRatio = setupRatio,
            NoSetupHappened = setupRatio is null,
            ProductionRatio = prodRatio,
            SpecifiedDowntimesList = downtimes,
        };

    private static AnalyzeRequest Req(params PartContext[] parts)
    {
        var r = new AnalyzeRequest { Machine = "Станок1", ShiftDate = "2026-10-28" };
        r.Parts.AddRange(parts);
        return r;
    }

    private static string Entry(string name = "Корпус", string reason = "основание") =>
        $"{name}§1§УЧ2610-0001.1.1§{reason}";

    [Fact]
    public void ConfirmedMastering_LowSetupKpd_Auto()
    {
        var p = Part(setupReason: "Освоение", masterDetail: "деталь с квазера, первая в работе");
        var (auto, ignored) = AutoExcludePolicy.Classify(Req(p), [Entry(reason: "Освоение новой детали")]);

        Assert.Single(auto);
        Assert.Empty(ignored);
    }

    [Fact]
    public void Mastering_SetupKpdAtOrAbove100_NotAuto()
    {
        var p = Part(setupReason: "Освоение", masterDetail: "деталь с квазера, первая в работе", setupRatio: 1.05);
        var (auto, ignored) = AutoExcludePolicy.Classify(Req(p), [Entry()]);

        Assert.Empty(auto);
        Assert.Empty(ignored);
    }

    [Theory]
    [InlineData("Разовое изменение времени из-за проблем с инструментом/оборудованием")]
    [InlineData("Несоответствующие заготовки")]
    public void ClosedListMasterReasons_Auto(string reason)
    {
        var p = Part(machReason: reason);
        var (auto, _) = AutoExcludePolicy.Classify(Req(p), [Entry()]);
        Assert.Single(auto);
    }

    [Fact]
    public void Apprentice_NeedsKpdBelow100InReasonCategory()
    {
        var low = Part(setupReason: "Работа ученика", setupRatio: 0.7);
        var high = Part(name: "Втулка", setupReason: "Работа ученика", setupRatio: 1.2);

        var (a1, _) = AutoExcludePolicy.Classify(Req(low), [Entry()]);
        var (a2, _) = AutoExcludePolicy.Classify(Req(high), [Entry(name: "Втулка")]);

        Assert.Single(a1);
        Assert.Empty(a2);
    }

    [Fact]
    public void ExplicitMasterRequest_Auto()
    {
        var p = Part(masterDetail: "Исключить из К1, деталь брак по материалу");
        var (auto, _) = AutoExcludePolicy.Classify(Req(p), [Entry()]);
        Assert.Single(auto);
    }

    [Theory]
    [InlineData("подгон размера")]
    [InlineData("выкатывание каждой детали в станке")]
    [InlineData("редактирование УП и подбор плиток")]
    public void FreeTextModelSuggestion_NeitherAutoNorIgnored(string reason)
    {
        var p = Part();
        var (auto, ignored) = AutoExcludePolicy.Classify(Req(p), [Entry(reason: reason)]);

        Assert.Empty(auto);
        Assert.Empty(ignored);
    }

    [Fact]
    public void MarkedDowntime_Ignored()
    {
        var p = Part(downtimes: "Отмеченные простои: [и] Обслуживание: 30 мин");
        var (auto, ignored) = AutoExcludePolicy.Classify(Req(p), [Entry(reason: "обслуживание станка")]);

        Assert.Empty(auto);
        Assert.Single(ignored);
    }

    [Fact]
    public void ReasonBeatsDowntime_WhenBothPresent()
    {
        var p = Part(machReason: "Несоответствующие заготовки", downtimes: "простой 10 мин");
        var (auto, ignored) = AutoExcludePolicy.Classify(Req(p), [Entry()]);

        Assert.Single(auto);
        Assert.Empty(ignored);
    }

    [Fact]
    public void UnresolvedEntry_NeitherAutoNorIgnored()
    {
        var (auto, ignored) = AutoExcludePolicy.Classify(Req(Part()), [Entry(name: "Другая деталь")]);

        Assert.Empty(auto);
        Assert.Empty(ignored);
    }

    [Fact]
    public void OrdinalTail_PicksTheNamedRow()
    {
        var first = Part(setupReason: "Освоение", masterDetail: "деталь с квазера", setupRatio: 1.3);
        var second = Part(machReason: "Несоответствующие заготовки");

        var (auto, _) = AutoExcludePolicy.Classify(Req(first, second), [Entry() + "§#2"]);
        var (auto1, _) = AutoExcludePolicy.Classify(Req(first, second), [Entry() + "§#1"]);

        Assert.Single(auto);
        Assert.Empty(auto1);
    }
}
