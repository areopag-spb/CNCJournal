using AiService.Models;
using AiService.Services;

namespace AiService.Tests;

/// <summary> P2.1: токены раундов копятся в трассе, упор в контекст блокирует автозакрытие. </summary>
public class ContextOverflowTests
{
    [Fact]
    public void RecordTokens_AccumulatesPeakAndTotal()
    {
        var trace = new AgentTrace();
        AgentLoopService.RecordTokens(trace, new ChatResult { PromptTokens = 4000, CompletionTokens = 500, DoneReason = "stop" });
        AgentLoopService.RecordTokens(trace, new ChatResult { PromptTokens = 5200, CompletionTokens = 300, DoneReason = "stop" });

        Assert.Equal(5200, trace.PeakPromptTokens);
        Assert.Equal(800, trace.TotalCompletionTokens);
        Assert.False(trace.ContextOverflow);
    }

    [Fact]
    public void RecordTokens_NearContextLimit_FlagsOverflow()
    {
        var trace = new AgentTrace();
        AgentLoopService.RecordTokens(trace, new ChatResult { PromptTokens = 15300, CompletionTokens = 400, DoneReason = "stop" });

        Assert.True(trace.ContextOverflow);
    }

    [Fact]
    public void RecordTokens_DoneReasonLength_FlagsOverflow()
    {
        var trace = new AgentTrace();
        AgentLoopService.RecordTokens(trace, new ChatResult { PromptTokens = 3000, CompletionTokens = 8192, DoneReason = "length" });

        Assert.True(trace.ContextOverflow);
    }

    [Fact]
    public void RecordTokens_NotReported_NoOverflow()
    {
        // Ollama токены не прислала (0/0) — флаг не ставим, это «нет данных», а не «всё хорошо».
        var trace = new AgentTrace();
        AgentLoopService.RecordTokens(trace, new ChatResult());

        Assert.False(trace.ContextOverflow);
        Assert.Equal(0, trace.PeakPromptTokens);
    }

    [Fact]
    public void ContextOverflow_BlocksAutoClose()
    {
        var b = AutoClosePolicy.Blockers(false, false, false, false, contextOverflow: true);

        Assert.Contains(AutoClosePolicy.ContextOverflow, b);
        Assert.False(AutoClosePolicy.IsEligible(false, b));
    }
}
