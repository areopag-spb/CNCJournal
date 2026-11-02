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
