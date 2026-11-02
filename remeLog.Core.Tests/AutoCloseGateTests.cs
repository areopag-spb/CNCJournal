using remeLog.Core.Services;
using remeLog.Infrastructure;
using remeLog.Models;

namespace remeLog.Core.Tests;

public class AutoCloseGateTests
{
    private static readonly DateTime ShiftDate = new(2026, 10, 20);
    private static readonly DateTime Mature = new(2026, 10, 22, 8, 0, 0);

    private static readonly AutoCloseOptions Options = new();

    private static Part Row(string name, int setup, string order) =>
        new(Guid.NewGuid(), "Станок", "День", ShiftDate, "Оператор",
            name, order, setup, 0, 0, 0, new DateTime(2026, 10, 20, 8, 0, 0),
            new DateTime(2026, 10, 20, 8, 0, 0), 0, new DateTime(2026, 10, 20, 9, 0, 0),
            0, 0, 0, 0, TimeSpan.Zero, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "");

    private static AiAnalysisResult CleanResult() => new()
    {
        RequiresReview = false,
        AutoCloseEligible = true,
        Confidence = 0.9,
        PromptVersion = "v1",
    };

    private static AutoCloseRequest Req(
        AiAnalysisResult? result = null,
        IReadOnlyList<Part>? parts = null,
        bool day = true, bool night = true,
        DateTime? now = null,
        bool human = false, bool isChecked = false) =>
        new(result ?? CleanResult(), parts ?? Array.Empty<Part>(), day, night,
            ShiftDate, now ?? Mature, human, isChecked);

    [Fact]
    public void Clean_Closes()
    {
        var d = AutoCloseGate.Evaluate(Req(), Options);

        Assert.Equal(AutoCloseOutcome.Close, d.Outcome);
        Assert.True(d.CanClose);
        Assert.Empty(d.Reasons);
        Assert.Empty(d.Exclusions);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void NoMasterReport_Defers(bool day, bool night)
    {
        var d = AutoCloseGate.Evaluate(Req(day: day, night: night), Options);

        Assert.Equal(AutoCloseOutcome.Defer, d.Outcome);
        Assert.NotEmpty(d.Reasons);
    }

    [Fact]
    public void NotMatureYet_Defers()
    {
        // Конец суток 21.10 00:00 + 12 ч = 21.10 12:00.
        var d = AutoCloseGate.Evaluate(Req(now: new DateTime(2026, 10, 21, 11, 59, 0)), Options);
        Assert.Equal(AutoCloseOutcome.Defer, d.Outcome);

        d = AutoCloseGate.Evaluate(Req(now: new DateTime(2026, 10, 21, 12, 0, 0)), Options);
        Assert.Equal(AutoCloseOutcome.Close, d.Outcome);
    }

    [Fact]
    public void DeferWinsOverOtherReasons()
    {
        var r = CleanResult();
        r.RequiresReview = true;

        var d = AutoCloseGate.Evaluate(Req(r, day: false), Options);

        Assert.Equal(AutoCloseOutcome.Defer, d.Outcome);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AlreadyReviewedOrChecked_Manual(bool human, bool isChecked)
    {
        var d = AutoCloseGate.Evaluate(Req(human: human, isChecked: isChecked), Options);

        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
    }

    [Fact]
    public void RequiresReview_Manual()
    {
        var r = CleanResult();
        r.RequiresReview = true;
        r.AutoCloseEligible = false;

        var d = AutoCloseGate.Evaluate(Req(r), Options);

        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
    }

    [Fact]
    public void Error_Manual()
    {
        var r = CleanResult();
        r.Error = "сбой";

        var d = AutoCloseGate.Evaluate(Req(r), Options);

        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
        Assert.Contains(d.Reasons, s => s.Contains("сбой"));
    }

    [Fact]
    public void NotEligible_ServerBlockersAreReturned()
    {
        var r = CleanResult();
        r.AutoCloseEligible = false;
        r.AutoCloseBlockers = new[] { "агентный контур не завершился" };

        var d = AutoCloseGate.Evaluate(Req(r), Options);

        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
        Assert.Contains("агентный контур не завершился", d.Reasons);
    }

    [Fact]
    public void NotEligible_NoBlockers_OldServerFallsToManual()
    {
        var r = CleanResult();
        r.AutoCloseEligible = false;

        var d = AutoCloseGate.Evaluate(Req(r), Options);

        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
        Assert.Single(d.Reasons);
    }

    [Fact]
    public void FlaggedParts_Manual()
    {
        var r = CleanResult();
        r.FlaggedParts = new[] { "Деталь§1§З1§причина" };

        Assert.Equal(AutoCloseOutcome.Manual, AutoCloseGate.Evaluate(Req(r), Options).Outcome);
    }

    [Fact]
    public void LowConfidence_Manual()
    {
        var r = CleanResult();
        r.Confidence = 0.79;

        Assert.Equal(AutoCloseOutcome.Manual, AutoCloseGate.Evaluate(Req(r), Options).Outcome);
    }

    [Fact]
    public void ConfidenceAtThreshold_Closes()
    {
        var r = CleanResult();
        r.Confidence = 0.8;

        Assert.Equal(AutoCloseOutcome.Close, AutoCloseGate.Evaluate(Req(r), Options).Outcome);
    }

    [Fact]
    public void PromptVersion_NotAllowed_Manual()
    {
        var options = new AutoCloseOptions { AllowedPromptVersions = new[] { "v2" } };

        var d = AutoCloseGate.Evaluate(Req(), options);

        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
    }

    [Fact]
    public void PromptVersion_Allowed_Closes()
    {
        var options = new AutoCloseOptions { AllowedPromptVersions = new[] { "v1" } };

        Assert.Equal(AutoCloseOutcome.Close, AutoCloseGate.Evaluate(Req(), options).Outcome);
    }

    [Fact]
    public void PromptVersion_AllowlistNull_NotChecked()
    {
        var r = CleanResult();
        r.PromptVersion = null;

        Assert.Equal(AutoCloseOutcome.Close, AutoCloseGate.Evaluate(Req(r), Options).Outcome);
    }

    [Fact]
    public void PromptVersion_MissingWithAllowlist_Manual()
    {
        var r = CleanResult();
        r.PromptVersion = null;
        var options = new AutoCloseOptions { AllowedPromptVersions = new[] { "v1" } };

        Assert.Equal(AutoCloseOutcome.Manual, AutoCloseGate.Evaluate(Req(r), options).Outcome);
    }

    [Fact]
    public void UnsavedEdits_Manual()
    {
        var part = Row("Деталь", 1, "З1");
        part.ExcludeFromReports = true; // правка → NeedUpdate

        var d = AutoCloseGate.Evaluate(Req(parts: new[] { part }), Options);

        Assert.True(part.NeedUpdate);
        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
    }

    [Fact]
    public void Exclusion_Matched_ClosesAndReturnsRow()
    {
        var part = Row("Деталь", 1, "З1");
        var r = CleanResult();
        r.SuggestExcludeFromReports = new[] { "Деталь§1§З1§разовая проблема с инструментом" };

        var d = AutoCloseGate.Evaluate(Req(r, new[] { part }), Options);

        Assert.Equal(AutoCloseOutcome.Close, d.Outcome);
        var ex = Assert.Single(d.Exclusions);
        Assert.Same(part, ex.Part);
        Assert.Equal("разовая проблема с инструментом", ex.Reason);
    }

    [Fact]
    public void Exclusion_NotMatched_Manual_NothingApplied()
    {
        var part = Row("Деталь", 1, "З1");
        var r = CleanResult();
        r.SuggestExcludeFromReports = new[]
        {
            "Деталь§1§З1§причина",
            "Другая§2§З9§причина",
        };

        var d = AutoCloseGate.Evaluate(Req(r, new[] { part }), Options);

        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
        Assert.Empty(d.Exclusions);
        Assert.Contains(d.Reasons, s => s.Contains("не сопоставлено"));
    }

    [Fact]
    public void Exclusion_Ambiguous_Manual()
    {
        // Две строки с одним ключом и без «#N» — однозначно не выбрать.
        var rows = new[] { Row("Деталь", 1, "З1"), Row("Деталь", 1, "З1") };
        var r = CleanResult();
        r.SuggestExcludeFromReports = new[] { "Деталь§1§З1§причина" };

        Assert.Equal(AutoCloseOutcome.Manual, AutoCloseGate.Evaluate(Req(r, rows), Options).Outcome);
    }

    [Fact]
    public void Exclusion_Unparseable_Manual()
    {
        var r = CleanResult();
        r.SuggestExcludeFromReports = new[] { "мусор" };

        var d = AutoCloseGate.Evaluate(Req(r), Options);

        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
        Assert.Contains(d.Reasons, s => s.Contains("не удалось разобрать"));
    }

    [Fact]
    public void Exclusion_AlreadyExcluded_Skipped()
    {
        var part = Row("Деталь", 1, "З1");
        part.ExcludeFromReports = true;
        part.NeedUpdate = false;
        var r = CleanResult();
        r.SuggestExcludeFromReports = new[] { "Деталь§1§З1§причина" };

        var d = AutoCloseGate.Evaluate(Req(r, new[] { part }), Options);

        Assert.Equal(AutoCloseOutcome.Close, d.Outcome);
        Assert.Empty(d.Exclusions);
    }

    [Fact]
    public void Exclusion_DuplicateSuggestions_AppliedOnce()
    {
        var part = Row("Деталь", 1, "З1");
        var r = CleanResult();
        r.SuggestExcludeFromReports = new[] { "Деталь§1§З1§причина", "Деталь§1§З1§причина" };

        var d = AutoCloseGate.Evaluate(Req(r, new[] { part }), Options);

        Assert.Single(d.Exclusions);
    }

    [Fact]
    public void AdvisoryShiftReportIssues_DoNotBlock_WhenServerDidNotEscalate()
    {
        // Рекомендательные замечания (S2 «частичная наладка > порога») не эскалируют
        // на сервере и не блокируют закрытие.
        var r = CleanResult();
        r.ShiftReportIssues = new[] { "Смена День: Частичная наладка 48% > 30% — рекомендуется отразить в комментарии мастера" };

        Assert.Equal(AutoCloseOutcome.Close, AutoCloseGate.Evaluate(Req(r), Options).Outcome);
    }

    [Fact]
    public void ShiftReportEscalation_Blocks()
    {
        var r = CleanResult();
        r.ShiftReportIssues = new[] { "Смена День: нет причины простоя" };
        r.EscalatedByShiftReport = true;

        var d = AutoCloseGate.Evaluate(Req(r), Options);

        Assert.Equal(AutoCloseOutcome.Manual, d.Outcome);
        Assert.Contains(d.Reasons, s => s.Contains("вопросы к отчёту"));
    }
}
