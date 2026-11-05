using AiService.Services;

namespace AiService.Tests;

public class AutoClosePolicyTests
{
    [Fact]
    public void Clean_NoEscalation_Eligible()
    {
        var b = AutoClosePolicy.Blockers(false, false, false, false);
        Assert.Empty(b);
        Assert.True(AutoClosePolicy.IsEligible(false, b));
    }

    [Fact]
    public void Escalation_NeverEligible()
    {
        var b = AutoClosePolicy.Blockers(true, false, false, false);
        Assert.False(AutoClosePolicy.IsEligible(true, b));
    }

    [Fact]
    public void FilterOverrodeModel_Blocks()
    {
        var b = AutoClosePolicy.Blockers(false, true, false, false);
        Assert.Contains(AutoClosePolicy.ResetOverrodeModel, b);
        Assert.False(AutoClosePolicy.IsEligible(false, b));
    }

    [Fact]
    public void AgentFallback_Blocks()
    {
        var b = AutoClosePolicy.Blockers(false, false, true, false);
        Assert.Contains(AutoClosePolicy.AgentFellBack, b);
        Assert.False(AutoClosePolicy.IsEligible(false, b));
    }

    [Fact]
    public void ModelError_Blocks()
    {
        var b = AutoClosePolicy.Blockers(false, false, false, true);
        Assert.Contains(AutoClosePolicy.ModelError, b);
    }

    [Fact]
    public void AgentRequestedButDisabled_Blocks()
    {
        var b = AutoClosePolicy.Blockers(false, false, false, false, agentUnavailable: true);
        Assert.Contains(AutoClosePolicy.AgentUnavailable, b);
        Assert.False(AutoClosePolicy.IsEligible(false, b));
    }

    [Fact]
    public void AgentNotRequested_DoesNotBlock()
    {
        Assert.Empty(AutoClosePolicy.Blockers(false, false, false, false, agentUnavailable: false));
    }
}

public class AutoCloseUnexcludedLowKpdTests
{
    private static AiService.Models.AnalyzeRequest Req(
        double? setupRatio = 0.34, string operatorComment = "", string setupReason = "",
        string machReason = "", double? productionRatio = null, bool noSetup = false)
    {
        var r = new AiService.Models.AnalyzeRequest();
        r.Parts.Add(new AiService.Models.PartContext
        {
            PartName = "Корпус", Order = "УЧ1", Setup = 2,
            SetupRatio = setupRatio, ProductionRatio = productionRatio,
            OperatorComment = operatorComment, MasterSetupComment = setupReason,
            MasterMachiningComment = machReason, NoSetupHappened = noSetup,
            SetupTimePlan = 60,
        });
        return r;
    }

    [Fact] // SKT21 №104 11.10: «Без М/Л» с планом 0 — КПД 0% не настоящий, не блокирует
    public void BezMlOrNoSetupPlan_DoesNotBlock()
    {
        var bez = Req(setupRatio: 0, operatorComment: "освоение");
        bez.Parts[0].Order = "Без М/Л";
        Assert.Empty(AutoClosePolicy.UnexcludedExplainedLowKpd(bez, null));

        var noPlan = Req(setupRatio: 0, operatorComment: "освоение");
        noPlan.Parts[0].SetupTimePlan = 0;
        Assert.Empty(AutoClosePolicy.UnexcludedExplainedLowKpd(noPlan, null));
    }

    [Fact] // Mazak QTS200ML 26.10: оператор «освоение», мастер «Другое: ремонт»
    public void OperatorMastering_MasterOther_Blocks()
    {
        var found = AutoClosePolicy.UnexcludedExplainedLowKpd(
            Req(operatorComment: "освоение", setupReason: "Другое"), null);
        Assert.Single(found);
        var b = AutoClosePolicy.Blockers(false, false, false, false, unexcludedLowKpd: found);
        Assert.Contains(b, x => x.StartsWith(AutoClosePolicy.UnexcludedLowKpdPrefix));
        Assert.False(AutoClosePolicy.IsEligible(false, b));
    }

    [Theory]
    [InlineData("Освоение", "")]
    [InlineData("Другое", "")]
    [InlineData("", "ремонт станка")]
    public void ExplainedLowSetup_Blocks(string reason, string operatorComment)
        => Assert.Single(AutoClosePolicy.UnexcludedExplainedLowKpd(
            Req(setupReason: reason, operatorComment: operatorComment), null));

    [Fact]
    public void AlreadyExcluded_DoesNotBlock()
        => Assert.Empty(AutoClosePolicy.UnexcludedExplainedLowKpd(
            Req(setupReason: "Освоение"), ["Корпус§2§УЧ1§освоение"]));

    [Fact]
    public void KpdAtOrAbove100_NoReason_NoSetupHappened_DoNotBlock()
    {
        Assert.Empty(AutoClosePolicy.UnexcludedExplainedLowKpd(Req(setupRatio: 1.0, setupReason: "Освоение"), null));
        Assert.Empty(AutoClosePolicy.UnexcludedExplainedLowKpd(Req(setupReason: ""), null));
        Assert.Empty(AutoClosePolicy.UnexcludedExplainedLowKpd(Req(setupReason: "Освоение", noSetup: true), null));
        Assert.Empty(AutoClosePolicy.UnexcludedExplainedLowKpd(Req(setupReason: "Штучная/длительная работа"), null));
    }

    [Fact]
    public void Machining_Other_Blocks_AndEscalatedDayHasNoBlockers()
    {
        var found = AutoClosePolicy.UnexcludedExplainedLowKpd(
            Req(setupRatio: null, productionRatio: 0.5, machReason: "Другое"), null);
        Assert.Single(found);
        Assert.Empty(AutoClosePolicy.Blockers(true, false, false, false, unexcludedLowKpd: found));
    }
}

public class AutoClosePolicyProcessDeviationTests
{
    private static AiService.Models.AnalyzeRequest Req(string operatorComment = "", string masterDetail = "", string shiftComment = "")
    {
        var r = new AiService.Models.AnalyzeRequest();
        r.Parts.Add(new AiService.Models.PartContext
        {
            PartName = "Гильза",
            OperatorComment = operatorComment,
            MasterSetupDetail = masterDetail,
        });
        if (shiftComment.Length > 0)
            r.ShiftReports.Add(new AiService.Models.ShiftReportContext { Shift = "День", MasterComment = shiftComment });
        return r;
    }

    [Theory]
    [InlineData("работа не по тех.проц. ( По тех.проц все за 2 установки приведет к эллипсу )")] // QTS350 23.10
    [InlineData("Работа не по техпроцессу")]
    [InlineData("сделали не по технологии, заготовки кривые")]
    [InlineData("отступление от тех проц")]
    [InlineData("Отклонение от технологии согласовано")]
    public void ProcessDeviationAdmitted_Blocks(string text)
    {
        Assert.True(AutoClosePolicy.MentionsProcessDeviation(Req(operatorComment: text)));
        Assert.True(AutoClosePolicy.MentionsProcessDeviation(Req(masterDetail: text)));
        Assert.True(AutoClosePolicy.MentionsProcessDeviation(Req(shiftComment: text)));

        var b = AutoClosePolicy.Blockers(false, false, false, false, false, processDeviationAdmitted: true);
        Assert.Contains(AutoClosePolicy.ProcessDeviationAdmitted, b);
        Assert.False(AutoClosePolicy.IsEligible(false, b));
    }

    [Theory]
    [InlineData("")]
    [InlineData("всё по техпроцессу")]
    [InlineData("работа по тех.проц. выполнена")]
    [InlineData("ждали наладчика")]
    [InlineData("Освоение")]
    public void OrdinaryComments_DoNotBlock(string text)
    {
        Assert.False(AutoClosePolicy.MentionsProcessDeviation(Req(operatorComment: text, masterDetail: text, shiftComment: text)));
        Assert.Empty(AutoClosePolicy.Blockers(false, false, false, false, false, processDeviationAdmitted: false));
    }

    [Fact]
    public void Escalated_NoBlockersListed()
    {
        // При эскалации блокеры не нужны: сутки и так у человека.
        Assert.Empty(AutoClosePolicy.Blockers(true, false, false, false, false, processDeviationAdmitted: true));
    }
}

public class MasteringAutoExcludeGroundingTests
{
    [Fact] // Hyundai WIA SKT21 №104 10.10: освоение подтверждено, но запись терялась в фильтре предложений
    public void TemplateAutoExcludeText_IsNotGroundedInRowTexts_SoFilterMustTrustIt()
    {
        var p = new AiService.Models.PartContext
        {
            PartName = "Плунжер АР27-01-002-09", Order = "УЧ1", Setup = 1, SetupRatio = 0.61,
            MasterSetupComment = "Освоение", OperatorComment = "освоение",
        };
        var request = new AiService.Models.AnalyzeRequest { Parts = [p] };
        var outcome = AiService.Services.MasteringAutoApprover.Apply(request);

        var entry = Assert.Single(outcome.AutoExcludes);
        var reason = AiService.Services.ExcludeTriggerValidator.ReasonOf(entry);
        Assert.False(AiService.Services.ExcludeTriggerValidator.HasGroundedReason(p, reason));
    }
}
