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
