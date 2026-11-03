using AiService.Models;
using AiService.Services;

namespace AiService.Tests;

/// <summary>
/// verify-part: «МЦ &lt;операция&gt;» (механический цех, универсальные/ручные операции) — допустимое объяснение
/// «Изготовление не по техпроцессу»; пустой обязательный ввод получает статическое замечание без модели.
/// </summary>
public class MasterCheckMcAndEmptyTests
{
    private static PartContext Part(string setupReason = "", string setupDetail = "", string machReason = "",
        string machDetail = "", string downtimes = "") => new()
    {
        PartName = "Фланец",
        MasterSetupComment = setupReason,
        MasterSetupDetail = setupDetail,
        MasterMachiningComment = machReason,
        MasterMachiningDetail = machDetail,
        SpecifiedDowntimesComment = downtimes,
    };

    [Theory]
    [InlineData("МЦ Сверлильная операция")]
    [InlineData("МЦ Фрезерная операция")]
    [InlineData("мц слесарная")]
    [InlineData("Выполнялось в МЦ, сверлильная операция")]
    public void McOperation_IsSufficientForNotByProcess(string detail)
    {
        var p = Part(machReason: "Изготовление не по техпроцессу", machDetail: detail,
            setupReason: "Изготовление не по техпроцессу", setupDetail: detail);

        Assert.True(MasteringAutoApprover.IsMachiningReasonSelfSufficient(p));
        Assert.True(MasteringAutoApprover.IsSetupReasonSelfSufficient(p));
    }

    [Theory]
    [InlineData("МЦ")]
    [InlineData("МЦ.")]
    [InlineData("ЗАМЦ работа")]
    [InlineData("просто что-то случилось")]
    public void McWithoutOperation_OrOtherWords_NotSufficient(string detail)
    {
        var p = Part(machReason: "Изготовление не по техпроцессу", machDetail: detail);

        Assert.False(MasteringAutoApprover.IsMachiningReasonSelfSufficient(p));
    }

    [Fact]
    public void McOperation_DoesNotAutoApproveOtherReasons()
    {
        var p = Part(machReason: "Другое", machDetail: "МЦ Сверлильная операция");

        Assert.False(MasteringAutoApprover.IsMachiningReasonSelfSufficient(p));
    }

    [Fact]
    public void EmptySetup_GetsStaticRemark_WithoutModel()
    {
        var anomalies = new List<VerifyAnomaly> { new() { Field = nameof(PartContext.MasterSetupDetail), Description = "КПД наладки 0%" } };

        Assert.Equal(MasterCheckGuards.EmptySetupRemark, MasterCheckGuards.CheckKnownFalsePattern(Part(), anomalies));
    }

    [Fact]
    public void EmptyMachining_AndDowntime_GetStaticRemarks()
    {
        var machining = new List<VerifyAnomaly> { new() { Field = nameof(PartContext.MasterMachiningDetail), Description = "x" } };
        var downtime = new List<VerifyAnomaly> { new() { Field = nameof(PartContext.SpecifiedDowntimesComment), Description = "Простои 55%" } };

        Assert.Equal(MasterCheckGuards.EmptyMachiningRemark, MasterCheckGuards.CheckKnownFalsePattern(Part(), machining));
        Assert.Equal(MasterCheckGuards.EmptyDowntimeRemark, MasterCheckGuards.CheckKnownFalsePattern(Part(), downtime));
    }

    [Fact]
    public void PartiallyFilled_GoesToModel()
    {
        // Причина выбрана, текста нет — «пустым вводом» не считается, решает модель.
        var anomalies = new List<VerifyAnomaly> { new() { Field = nameof(PartContext.MasterSetupDetail), Description = "x" } };

        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(Part(setupReason: "Другое"), anomalies));
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(Part(setupDetail: "что-то"), anomalies));
    }

    [Fact]
    public void FilledDowntimeComment_GoesToModel()
    {
        var anomalies = new List<VerifyAnomaly> { new() { Field = nameof(PartContext.SpecifiedDowntimesComment), Description = "Простои 55%" } };

        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(Part(downtimes: "ждали заготовку"), anomalies));
    }
}
