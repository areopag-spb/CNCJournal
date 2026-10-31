using remeLog.Core.Db;
using remeLog.Core.Services;
using remeLog.Infrastructure;
using remeLog.Infrastructure.Types;
using remeLog.Models;

namespace remeLog.Core.Tests;

public class AutoCloseExecutorTests
{
    private const string Machine = "Станок";
    private static readonly DateTime Date = new(2026, 10, 20);
    private static readonly DateTime Now = new(2026, 10, 22, 8, 0, 0);

    private sealed class FakeStore : IAutoCloseStore
    {
        public List<Part> Parts { get; set; } = new();
        public ShiftInfo? Day { get; set; } = new(Date, ShiftType.Day, Machine);
        public ShiftInfo? Night { get; set; } = new(Date, ShiftType.Night, Machine);
        public DayReview? Existing { get; set; }
        public List<string> Ops { get; } = new();
        public DayReview? SavedReview { get; private set; }
        public List<AutoExcludeLogEntry> Logged { get; } = new();
        public string? FailOn { get; set; }
        public bool ShiftReadError { get; set; }

        private bool Fails(string op) => FailOn == op;

        public Task<DbResult<ShiftInfo>> ReadShiftAsync(string machine, DateTime date, ShiftType shift)
        {
            if (ShiftReadError) return Task.FromResult(DbResult<ShiftInfo>.FailWithError("бд"));
            var s = shift == ShiftType.Day ? Day : Night;
            return Task.FromResult(s == null ? DbResult<ShiftInfo>.NotFound() : DbResult<ShiftInfo>.Ok(s));
        }

        public Task<DbResult<bool>> WriteShiftAsync(ShiftInfo shift)
        {
            var op = $"shift:{shift.Shift}:{shift.IsChecked}";
            Ops.Add(op);
            return Task.FromResult(Fails("shift:" + shift.Shift + ":True") && shift.IsChecked
                ? DbResult<bool>.FailWithError("сбой")
                : DbResult<bool>.Ok(true));
        }

        public Task<IReadOnlyList<Part>> ReadPartsAsync(string machine, DateTime date, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Part>>(Parts);

        public Task<DbResult<string>> UpdatePartAsync(Part part)
        {
            Ops.Add($"part:{part.PartName}:{part.ExcludeFromReports}");
            return Task.FromResult(Fails("part") && part.ExcludeFromReports
                ? DbResult<string>.FailWithError("сбой")
                : DbResult<string>.Ok("OK"));
        }

        public Task<DayReview?> GetDayReviewAsync(string machine, DateTime date) => Task.FromResult(Existing);

        public Task<DbResult<int>> SaveDayReviewAsync(DayReview review)
        {
            Ops.Add("review");
            if (Fails("review")) return Task.FromResult(DbResult<int>.FailWithError("сбой"));
            review.Id = 7;
            SavedReview = review;
            return Task.FromResult(DbResult<int>.Ok(7));
        }

        public Task ClearPartFlagsAsync(int dayReviewId)
        {
            Ops.Add("clearflags");
            return Task.CompletedTask;
        }

        public Task<DbResult<int>> SaveExcludeLogAsync(IReadOnlyCollection<AutoExcludeLogEntry> entries)
        {
            Ops.Add("log");
            if (Fails("log")) return Task.FromResult(DbResult<int>.FailWithError("сбой"));
            Logged.AddRange(entries);
            return Task.FromResult(DbResult<int>.Ok(entries.Count));
        }

        public Task<DbResult<bool>> RevertExcludeLogAsync(
            string machine, DateTime date, IReadOnlyCollection<Guid> partGuids, string by)
        {
            Ops.Add("revertlog");
            return Task.FromResult(DbResult<bool>.Ok(true));
        }
    }

    private static Part Row(string name, int setup, string order) =>
        new(Guid.NewGuid(), Machine, "День", Date, "Оператор",
            name, order, setup, 0, 0, 0, new DateTime(2026, 10, 20, 8, 0, 0),
            new DateTime(2026, 10, 20, 8, 0, 0), 0, new DateTime(2026, 10, 20, 9, 0, 0),
            0, 0, 0, 0, TimeSpan.Zero, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "");

    private static AiAnalysisResult Clean() => new()
    {
        AutoCloseEligible = true,
        Confidence = 0.9,
        PromptVersion = "v1@agent",
    };

    private static AutoCloseExecutor Executor(FakeStore store, bool enabled = true, bool dryRun = false, int max = 100) =>
        new(store, new AutoCloseOptions(), new AutoCloseRunOptions { Enabled = enabled, DryRun = dryRun, MaxCloses = max },
            () => Now);

    private static Task<AutoCloseResult> Run(AutoCloseExecutor ex, FakeStore store, AiAnalysisResult? result = null) =>
        ex.TryCloseAsync(Machine, Date, result ?? Clean(), store.Parts.ToList(), "model", true);

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        var store = new FakeStore();

        var r = await Run(Executor(store, enabled: false), store);

        Assert.Equal(AutoCloseStatus.Disabled, r.Status);
        Assert.Empty(store.Ops);
    }

    [Fact]
    public async Task DryRun_WouldClose_WritesNothing()
    {
        var store = new FakeStore();
        var ex = Executor(store, dryRun: true);

        var r = await Run(ex, store);

        Assert.Equal(AutoCloseStatus.WouldClose, r.Status);
        Assert.Empty(store.Ops);
        Assert.Equal(1, ex.ClosedCount);
    }

    [Fact]
    public async Task Close_Empty_WritesReviewThenChecksBothShifts()
    {
        var store = new FakeStore();

        var r = await Run(Executor(store), store);

        Assert.Equal(AutoCloseStatus.Closed, r.Status);
        Assert.Equal(new[] { "review", "clearflags", "shift:День:True", "shift:Ночь:True" }, store.Ops);
        Assert.NotNull(store.SavedReview);
        Assert.Equal(DayReview.AutoReviewer, store.SavedReview!.ReviewedBy);
        Assert.True(store.SavedReview.AutoClosed);
        Assert.Equal(AnalystDecision.Ok, store.SavedReview.Decision);
        Assert.StartsWith(DayReview.AutoCloseCommentMarker, store.SavedReview.Comment);
        Assert.True(store.Day!.IsChecked);
        Assert.True(store.Night!.IsChecked);
    }

    [Fact]
    public async Task Close_WithExclusion_LogsBeforeExcludingAndReviewAfter()
    {
        var store = new FakeStore();
        var part = Row("Деталь", 1, "З1");
        store.Parts.Add(part);
        var result = Clean();
        result.SuggestExcludeFromReports = new[] { "Деталь§1§З1§разовая причина" };

        var r = await Run(Executor(store), store, result);

        Assert.Equal(AutoCloseStatus.Closed, r.Status);
        Assert.Equal(1, r.ExclusionsApplied);
        Assert.Equal(
            new[] { "log", "part:Деталь:True", "review", "clearflags", "shift:День:True", "shift:Ночь:True" },
            store.Ops);
        Assert.True(part.ExcludeFromReports);
        var entry = Assert.Single(store.Logged);
        Assert.Equal(part.Guid, entry.PartGuid);
        Assert.Equal("разовая причина", entry.Reason);
        Assert.Equal("v1@agent", entry.AiPromptVersion);
        Assert.Contains("исключено строк: 1", store.SavedReview!.Comment);
    }

    [Fact]
    public async Task LogFails_NothingExcludedNothingClosed()
    {
        var store = new FakeStore { FailOn = "log" };
        var part = Row("Деталь", 1, "З1");
        store.Parts.Add(part);
        var result = Clean();
        result.SuggestExcludeFromReports = new[] { "Деталь§1§З1§причина" };

        var r = await Run(Executor(store), store, result);

        Assert.Equal(AutoCloseStatus.Error, r.Status);
        Assert.Equal(new[] { "log" }, store.Ops);
        Assert.False(part.ExcludeFromReports);
    }

    [Fact]
    public async Task ReviewFails_RollsBackExclusions()
    {
        var store = new FakeStore { FailOn = "review" };
        var part = Row("Деталь", 1, "З1");
        store.Parts.Add(part);
        var result = Clean();
        result.SuggestExcludeFromReports = new[] { "Деталь§1§З1§причина" };

        var r = await Run(Executor(store), store, result);

        Assert.Equal(AutoCloseStatus.Error, r.Status);
        Assert.False(part.ExcludeFromReports);
        Assert.Contains("part:Деталь:False", store.Ops);
        Assert.Contains("revertlog", store.Ops);
        Assert.DoesNotContain(store.Ops, o => o.StartsWith("shift:"));
    }

    [Fact]
    public async Task NightShiftFails_RevertsDayAndExclusions()
    {
        var store = new FakeStore { FailOn = "shift:Ночь:True" };
        var part = Row("Деталь", 1, "З1");
        store.Parts.Add(part);
        var result = Clean();
        result.SuggestExcludeFromReports = new[] { "Деталь§1§З1§причина" };

        var r = await Run(Executor(store), store, result);

        Assert.Equal(AutoCloseStatus.Error, r.Status);
        Assert.False(store.Day!.IsChecked);
        Assert.False(store.Night!.IsChecked);
        Assert.False(part.ExcludeFromReports);
        Assert.Contains("shift:День:False", store.Ops);
        Assert.Contains("revertlog", store.Ops);
    }

    [Fact]
    public async Task PartSaveFails_RollsBackAndNoReview()
    {
        var store = new FakeStore { FailOn = "part" };
        var part = Row("Деталь", 1, "З1");
        store.Parts.Add(part);
        var result = Clean();
        result.SuggestExcludeFromReports = new[] { "Деталь§1§З1§причина" };

        var r = await Run(Executor(store), store, result);

        Assert.Equal(AutoCloseStatus.Error, r.Status);
        Assert.False(part.ExcludeFromReports);
        Assert.DoesNotContain("review", store.Ops);
    }

    [Fact]
    public async Task DataChangedAfterAnalysis_Defers()
    {
        var store = new FakeStore();
        var analyzed = new List<Part> { Row("Деталь", 1, "З1") };
        store.Parts.Add(Row("Деталь", 1, "З1")); // другая строка (другой Guid) → отпечаток иной

        var r = await Executor(store).TryCloseAsync(Machine, Date, Clean(), analyzed, "m", true);

        Assert.Equal(AutoCloseStatus.Deferred, r.Status);
        Assert.Empty(store.Ops);
    }

    [Fact]
    public async Task NoNightReport_Defers()
    {
        var store = new FakeStore { Night = null };

        var r = await Run(Executor(store), store);

        Assert.Equal(AutoCloseStatus.Deferred, r.Status);
        Assert.Empty(store.Ops);
    }

    [Fact]
    public async Task ShiftReadError_Error()
    {
        var store = new FakeStore { ShiftReadError = true };

        var r = await Run(Executor(store), store);

        Assert.Equal(AutoCloseStatus.Error, r.Status);
    }

    [Fact]
    public async Task HumanReview_Manual()
    {
        var store = new FakeStore
        {
            Existing = new DayReview(Machine, Date, "Иванов", AnalystDecision.Escalated, false),
        };

        var r = await Run(Executor(store), store);

        Assert.Equal(AutoCloseStatus.Manual, r.Status);
        Assert.Empty(store.Ops);
    }

    [Fact]
    public async Task EarlierAutoReview_AllowsRetry()
    {
        var store = new FakeStore
        {
            Existing = new DayReview(Machine, Date, DayReview.AutoReviewer, AnalystDecision.Ok, true)
            {
                AutoClosed = true,
            },
        };

        var r = await Run(Executor(store), store);

        Assert.Equal(AutoCloseStatus.Closed, r.Status);
    }

    [Fact]
    public async Task AlreadyChecked_Manual()
    {
        var store = new FakeStore();
        store.Day!.IsChecked = true;
        store.Night!.IsChecked = true;

        var r = await Run(Executor(store), store);

        Assert.Equal(AutoCloseStatus.Manual, r.Status);
        Assert.Empty(store.Ops);
    }

    [Fact]
    public async Task ServerNotEligible_Manual_WithBlockers()
    {
        var store = new FakeStore();
        var result = Clean();
        result.AutoCloseEligible = false;
        result.AutoCloseBlockers = new[] { "агентный контур не завершился" };

        var r = await Run(Executor(store), store, result);

        Assert.Equal(AutoCloseStatus.Manual, r.Status);
        Assert.Contains("агентный контур не завершился", r.Reasons);
        Assert.Empty(store.Ops);
    }

    [Fact]
    public async Task Limit_StopsAfterMaxCloses()
    {
        var store = new FakeStore();
        var ex = Executor(store, max: 1);

        var first = await Run(ex, store);
        // Для второго раза вернём сутки в исходное состояние.
        store.Day!.IsChecked = false;
        store.Night!.IsChecked = false;
        store.Existing = null;
        var second = await Run(ex, store);

        Assert.Equal(AutoCloseStatus.Closed, first.Status);
        Assert.Equal(AutoCloseStatus.LimitReached, second.Status);
    }

    [Fact]
    public void Fingerprint_StableAndSensitive()
    {
        var a = Row("Деталь", 1, "З1");
        var same = AutoCloseFingerprint.Of(new[] { a });
        Assert.Equal(same, AutoCloseFingerprint.Of(new[] { a }));

        a.OperatorComment = "правка";
        Assert.NotEqual(same, AutoCloseFingerprint.Of(new[] { a }));
    }
}
