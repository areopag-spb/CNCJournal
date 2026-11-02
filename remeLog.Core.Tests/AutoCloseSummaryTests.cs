using remeLog.Core.Services;
using remeLog.Infrastructure.Types;
using remeLog.Models;

namespace remeLog.Core.Tests;

public class AutoCloseSummaryTests
{
    // 2026-10-29 — четверг; понедельник этой недели — 26.10.
    private static readonly DateTime Now = new(2026, 10, 29, 10, 0, 0);
    private static int _id;

    private static AutoCloseLogEntry E(
        string machine, int day, string outcome, bool dryRun = false, DateTime? runAt = null,
        string? control = null, DateTime? controlAt = null, params string[] reasons) => new()
    {
        Id = ++_id,
        Machine = machine,
        ShiftDate = new DateTime(2026, 10, day),
        RunAt = runAt ?? new DateTime(2026, 10, 28, 8, 0, 0),
        DryRun = dryRun,
        Outcome = outcome,
        Reasons = reasons,
        ControlVerdict = control,
        ControlAt = controlAt,
    };

    private static readonly IReadOnlyDictionary<(string, DateTime), DayReview> NoReviews =
        new Dictionary<(string, DateTime), DayReview>();

    [Fact]
    public void CountsRealAndShadowSeparately()
    {
        var s = AutoCloseSummary.Build(new[]
        {
            E("A", 1, AutoCloseLogEntry.OutcomeClosed),
            E("A", 2, AutoCloseLogEntry.OutcomeManual, reasons: "x"),
            E("A", 3, AutoCloseLogEntry.OutcomeDeferred, reasons: "y"),
            E("A", 4, AutoCloseLogEntry.OutcomeError, reasons: "z"),
            E("A", 5, AutoCloseLogEntry.OutcomeWouldClose, dryRun: true),
            E("A", 6, AutoCloseLogEntry.OutcomeManual, dryRun: true, reasons: "x"),
        }, NoReviews, Now);

        Assert.Equal(1, s.Closed);
        Assert.Equal(1, s.Manual);
        Assert.Equal(1, s.Deferred);
        Assert.Equal(1, s.Errors);
        Assert.Equal(1, s.ShadowWouldClose);
        Assert.Equal(1, s.ShadowManual);
    }

    [Fact]
    public void LatestDecisionPerDayWins_PerMode()
    {
        var s = AutoCloseSummary.Build(new[]
        {
            E("A", 1, AutoCloseLogEntry.OutcomeManual, runAt: new DateTime(2026, 10, 27), reasons: "x"),
            E("A", 1, AutoCloseLogEntry.OutcomeClosed, runAt: new DateTime(2026, 10, 28)),
            // Тень по тем же суткам — отдельная ветка, реальное решение её не вытесняет.
            E("A", 1, AutoCloseLogEntry.OutcomeWouldClose, dryRun: true, runAt: new DateTime(2026, 10, 26)),
        }, NoReviews, Now);

        Assert.Equal(1, s.Closed);
        Assert.Equal(0, s.Manual);
        Assert.Equal(1, s.ShadowWouldClose);
    }

    [Fact]
    public void ReasonsAreGroupedIgnoringNumbers_AndSortedByCount()
    {
        var s = AutoCloseSummary.Build(new[]
        {
            E("A", 1, AutoCloseLogEntry.OutcomeManual, reasons: "уверенность 79% ниже порога 80%"),
            E("B", 1, AutoCloseLogEntry.OutcomeManual, reasons: "уверенность 65% ниже порога 80%"),
            E("C", 1, AutoCloseLogEntry.OutcomeManual, reasons: "есть вопросы к отчёту мастера"),
        }, NoReviews, Now);

        Assert.Equal(2, s.TopReasons.Count);
        Assert.Equal("уверенность #% ниже порога #%", s.TopReasons[0].Reason);
        Assert.Equal(2, s.TopReasons[0].Count);
    }

    [Fact]
    public void ReopenedByHuman_WhenReviewIsNoLongerAuto()
    {
        var human = new DayReview("A", new DateTime(2026, 10, 1), "Иванов", AnalystDecision.Escalated, false);
        var auto = new DayReview("A", new DateTime(2026, 10, 2), DayReview.AutoReviewer, AnalystDecision.Ok, true)
        {
            AutoClosed = true,
        };
        var reviews = new Dictionary<(string, DateTime), DayReview>
        {
            [("A", new DateTime(2026, 10, 1))] = human,
            [("A", new DateTime(2026, 10, 2))] = auto,
        };

        var s = AutoCloseSummary.Build(new[]
        {
            E("A", 1, AutoCloseLogEntry.OutcomeClosed),
            E("A", 2, AutoCloseLogEntry.OutcomeClosed),
            E("A", 3, AutoCloseLogEntry.OutcomeClosed), // ревью нет — неизвестно, не считаем
        }, reviews, Now);

        Assert.Equal(3, s.Closed);
        Assert.Equal(1, s.ReopenedByHuman);
    }

    [Fact]
    public void ControlCounts_AndHardMissFlag()
    {
        var s = AutoCloseSummary.Build(new[]
        {
            E("A", 1, AutoCloseLogEntry.OutcomeClosed, control: AutoCloseLogEntry.ControlAgree,
                controlAt: new DateTime(2026, 10, 27)),
            E("A", 2, AutoCloseLogEntry.OutcomeClosed, control: AutoCloseLogEntry.ControlMiss,
                controlAt: new DateTime(2026, 10, 20)),
            E("A", 3, AutoCloseLogEntry.OutcomeClosed, control: AutoCloseLogEntry.ControlMissHard,
                controlAt: new DateTime(2026, 10, 28)),
            E("A", 4, AutoCloseLogEntry.OutcomeClosed),
        }, NoReviews, Now);

        Assert.Equal(1, s.ControlAgree);
        Assert.Equal(1, s.ControlMiss);
        Assert.Equal(1, s.ControlMissHard);
        Assert.Equal(3, s.ControlChecked);
        Assert.True(s.HasHardMiss);
        // С понедельника 26.10: проверки 27.10 и 28.10, а 20.10 — прошлая неделя.
        Assert.Equal(2, s.ControlledThisWeek);
    }

    [Fact]
    public void NoHardMiss_WhenOnlyAgreements()
    {
        var s = AutoCloseSummary.Build(new[]
        {
            E("A", 1, AutoCloseLogEntry.OutcomeClosed, control: AutoCloseLogEntry.ControlAgree,
                controlAt: new DateTime(2026, 10, 27)),
        }, NoReviews, Now);

        Assert.False(s.HasHardMiss);
    }

    [Fact]
    public void ClosedForControl_OnlyRealClosed_NewestFirst()
    {
        var s = AutoCloseSummary.Build(new[]
        {
            E("A", 1, AutoCloseLogEntry.OutcomeClosed),
            E("A", 5, AutoCloseLogEntry.OutcomeClosed),
            E("A", 9, AutoCloseLogEntry.OutcomeWouldClose, dryRun: true),
            E("A", 7, AutoCloseLogEntry.OutcomeManual, reasons: "x"),
        }, NoReviews, Now);

        Assert.Equal(new[] { 5, 1 }, s.ClosedForControl.Select(e => e.ShiftDate.Day).ToArray());
    }

    [Fact]
    public void Empty_AllZero()
    {
        var s = AutoCloseSummary.Build(Array.Empty<AutoCloseLogEntry>(), NoReviews, Now);

        Assert.Equal(0, s.Closed);
        Assert.Empty(s.TopReasons);
        Assert.Empty(s.ClosedForControl);
        Assert.False(s.HasHardMiss);
    }
}
