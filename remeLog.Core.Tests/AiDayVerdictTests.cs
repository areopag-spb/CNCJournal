using remeLog.Infrastructure;
using remeLog.Models;
using System.Text.Json;

namespace remeLog.Core.Tests;

public class AiDayVerdictTests
{
    private static AiDayVerdict Stored(
        string fingerprint = "fp1", string? version = "v1", string? model = "qwen3:14b", bool thinking = true) => new()
    {
        Machine = "Станок1",
        ShiftDate = new DateTime(2026, 10, 28),
        InputFingerprint = fingerprint,
        AnalysisVersion = version,
        AiModelVersion = model,
        ThinkingEnabled = thinking,
        Result = new AiAnalysisResult { RequiresReview = true, Confidence = 0.9, Explanation = "x" },
    };

    [Fact]
    public void CanReuse_AllFourMatch_True()
        => Assert.True(AiVerdictReuse.CanReuse(Stored(), "fp1", "v1", "qwen3:14b", true));

    [Fact]
    public void CanReuse_DifferentData_False()
        => Assert.False(AiVerdictReuse.CanReuse(Stored(), "fp-changed", "v1", "qwen3:14b", true));

    [Fact]
    public void CanReuse_DifferentServiceVersion_False()
        => Assert.False(AiVerdictReuse.CanReuse(Stored(), "fp1", "v2", "qwen3:14b", true));

    [Fact]
    public void CanReuse_DifferentModelOrMode_False()
    {
        Assert.False(AiVerdictReuse.CanReuse(Stored(), "fp1", "v1", "gemma4:12b", true));
        Assert.False(AiVerdictReuse.CanReuse(Stored(), "fp1", "v1", "qwen3:14b", false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CanReuse_UnknownCurrentVersion_False(string? current)
        => Assert.False(AiVerdictReuse.CanReuse(Stored(), "fp1", current, "qwen3:14b", true));

    [Fact]
    public void CanReuse_StoredWithoutVersionOrWithError_False()
    {
        Assert.False(AiVerdictReuse.CanReuse(Stored(version: null), "fp1", "v1", "qwen3:14b", true));
        Assert.False(AiVerdictReuse.CanReuse(null, "fp1", "v1", "qwen3:14b", true));

        var withError = Stored();
        withError.Result.Error = "сбой";
        Assert.False(AiVerdictReuse.CanReuse(withError, "fp1", "v1", "qwen3:14b", true));
    }

    [Fact]
    public void Result_RoundTripsThroughJson_WithAllVerdictFields()
    {
        var original = new AiAnalysisResult
        {
            RequiresReview = false,
            Confidence = 0.95,
            Signals = new[] { "сигнал 1", "сигнал 2" },
            Explanation = "Пояснение с кириллицей",
            SuggestedReason = "причина",
            SuggestExcludeFromReports = new[] { "Деталь§1§З1§освоение" },
            AutoExcludeEntries = new[] { "Деталь§1§З1§освоение" },
            IgnoredExcludeEntries = Array.Empty<string>(),
            FlaggedParts = new[] { "Деталь§1§З1" },
            ShiftReportIssues = new[] { "Смена День: вопрос" },
            AutoCloseEligible = true,
            AutoCloseBlockers = new[] { "блокер" },
            PromptVersion = "2026-11-02-agent-v29+d4f35b82@agent",
            AnalysisVersion = "a1b2c3d4e5f6",
        };

        var json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<AiAnalysisResult>(json)!;

        Assert.Equal(original.Signals, back.Signals);
        Assert.Equal(original.Explanation, back.Explanation);
        Assert.Equal(original.AutoExcludeEntries, back.AutoExcludeEntries);
        Assert.Equal(original.FlaggedParts, back.FlaggedParts);
        Assert.Equal(original.ShiftReportIssues, back.ShiftReportIssues);
        Assert.True(back.AutoCloseEligible);
        Assert.Equal(original.AutoCloseBlockers, back.AutoCloseBlockers);
        Assert.Equal(original.PromptVersion, back.PromptVersion);
        Assert.Equal("a1b2c3d4e5f6", back.AnalysisVersion);
    }
}
