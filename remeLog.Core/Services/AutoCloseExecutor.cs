using remeLog.Core.Db;
using remeLog.Infrastructure;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace remeLog.Core.Services
{
    /// <summary> Доступ к данным, нужный автозакрытию (для тестов подменяется). </summary>
    public interface IAutoCloseStore
    {
        /// <summary> Отчёт мастера смены: Ok — найден, NotFound — отчёта нет, иное — ошибка БД. </summary>
        Task<DbResult<ShiftInfo>> ReadShiftAsync(string machine, DateTime date, ShiftType shift);
        Task<DbResult<bool>> WriteShiftAsync(ShiftInfo shift);
        Task<IReadOnlyList<Part>> ReadPartsAsync(string machine, DateTime date, CancellationToken ct);
        Task<DbResult<string>> UpdatePartAsync(Part part);
        Task<DayReview?> GetDayReviewAsync(string machine, DateTime date);
        Task<DbResult<int>> SaveDayReviewAsync(DayReview review);
        Task ClearPartFlagsAsync(int dayReviewId);
        Task<DbResult<int>> SaveExcludeLogAsync(IReadOnlyCollection<AutoExcludeLogEntry> entries);
        Task<DbResult<bool>> RevertExcludeLogAsync(
            string machine, DateTime date, IReadOnlyCollection<Guid> partGuids, string by);
        /// <summary> Журнал решения автозакрытия. Не бросает: сбой журнала не должен ломать закрытие. </summary>
        Task SaveRunLogAsync(AutoCloseLogEntry entry);
    }

    /// <summary> Реализация поверх статического <c>Database</c>. </summary>
    public sealed class DbAutoCloseStore : IAutoCloseStore
    {
        public Task<DbResult<ShiftInfo>> ReadShiftAsync(string machine, DateTime date, ShiftType shift)
        {
            var read = Database.ReadShiftInfo(new ShiftInfo(date, shift, machine));
            if (!read.IsOk) return Task.FromResult(DbResult<ShiftInfo>.Fail(read.Status, read.Error ?? "ошибка БД"));
            return Task.FromResult(read.Value is { Count: 1 }
                ? DbResult<ShiftInfo>.Ok(read.Value[0])
                : read.Value is { Count: > 1 }
                    ? DbResult<ShiftInfo>.FailWithError("несколько отчётов мастера за одну смену")
                    : DbResult<ShiftInfo>.NotFound());
        }

        public Task<DbResult<bool>> WriteShiftAsync(ShiftInfo shift) =>
            Task.FromResult(Database.WriteShiftInfo(shift));

        public async Task<IReadOnlyList<Part>> ReadPartsAsync(string machine, DateTime date, CancellationToken ct) =>
            await Database.ReadPartsByShiftDateAndMachine(date.Date, date.Date, machine, ct);

        public Task<DbResult<string>> UpdatePartAsync(Part part) => part.UpdatePartAsync();

        public Task<DayReview?> GetDayReviewAsync(string machine, DateTime date) =>
            Database.GetDayReviewAsync(machine, date);

        public Task<DbResult<int>> SaveDayReviewAsync(DayReview review) => Database.SaveDayReviewAsync(review);

        public Task ClearPartFlagsAsync(int dayReviewId) => Database.ClearPartFlagsAsync(dayReviewId);

        public Task<DbResult<int>> SaveExcludeLogAsync(IReadOnlyCollection<AutoExcludeLogEntry> entries) =>
            Database.SaveAutoExcludeLogAsync(entries);

        public Task<DbResult<bool>> RevertExcludeLogAsync(
            string machine, DateTime date, IReadOnlyCollection<Guid> partGuids, string by) =>
            Database.RevertAutoExcludeLogAsync(machine, date, partGuids, by);

        public async Task SaveRunLogAsync(AutoCloseLogEntry entry) =>
            await Database.SaveAutoCloseLogAsync(entry);
    }

    public sealed class AutoCloseRunOptions
    {
        /// <summary> Kill switch: false — не делаем ничего. </summary>
        public bool Enabled { get; init; }

        /// <summary> Режим тени: считаем и сообщаем «закрыл бы», но в БД ничего не пишем. </summary>
        public bool DryRun { get; init; } = true;

        /// <summary> Предел закрытий (и «закрыл бы» в тени) за один экземпляр исполнителя. </summary>
        public int MaxCloses { get; init; } = 100;

        /// <summary> Кто запустил — в журнал решений. </summary>
        public string RunBy { get; init; } = Environment.UserName;
    }

    public enum AutoCloseStatus
    {
        Closed,
        /// <summary> Режим тени: сутки были бы закрыты. </summary>
        WouldClose,
        Deferred,
        Manual,
        /// <summary> Сбой записи; всё, что успели, откачено (см. Reasons). </summary>
        Error,
        Disabled,
        LimitReached,
    }

    public sealed record AutoCloseResult(
        AutoCloseStatus Status, IReadOnlyList<string> Reasons, int ExclusionsApplied = 0);

    /// <summary> Отпечаток данных суток: сравнение «что анализировали» и «что в БД сейчас». </summary>
    public static class AutoCloseFingerprint
    {
        public static string Of(IEnumerable<Part> parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts.OrderBy(x => x.Guid))
            {
                sb.Append(p.Guid).Append('|').Append(p.PartName).Append('|').Append(p.Order).Append('|')
                  .Append(p.Setup).Append('|').Append(p.FinishedCount).Append('|')
                  .Append(p.StartSetupTime.Ticks).Append('|').Append(p.StartMachiningTime.Ticks).Append('|')
                  .Append(p.EndMachiningTime.Ticks).Append('|')
                  .Append(p.OperatorComment).Append('|').Append(p.MasterSetupComment).Append('|')
                  .Append(p.MasterMachiningComment).Append('|').Append(p.MasterComment).Append('|')
                  .Append(p.ExcludeFromReports).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }

    /// <summary>
    /// Исполнитель автозакрытия суток (этап 2.3). Анализ ИИ выполняет вызывающий и передаёт
    /// результат; здесь — решение (<see cref="AutoCloseGate"/>) и запись с откатом.
    /// Порядок записи: журнал исключений → исключения строк → ревью «ИИ-авто» → IsChecked
    /// обеих смен (зелёная клетка). Сбой на любом шаге откатывает исключения и IsChecked;
    /// ревью остаётся (оно «авто» и перезапишется при повторной попытке).
    /// План: research/ai-service/auto-close-plan.md.
    /// </summary>
    public sealed class AutoCloseExecutor
    {
        private readonly IAutoCloseStore _store;
        private readonly AutoCloseOptions _gate;
        private readonly AutoCloseRunOptions _run;
        private readonly Func<DateTime> _clock;
        private int _decided;
        private int _closed;

        public AutoCloseExecutor(IAutoCloseStore store, AutoCloseOptions gate, AutoCloseRunOptions run,
            Func<DateTime>? clock = null)
        {
            _store = store;
            _gate = gate;
            _run = run;
            _clock = clock ?? (() => DateTime.Now);
        }

        /// <summary> Сколько суток закрыто (или «закрыто бы» в тени) этим экземпляром. </summary>
        public int ClosedCount => _closed;

        /// <param name="analyzedParts">Строки суток, по которым ИИ делал анализ (для сверки отпечатка).</param>
        /// <param name="aiModel">Модель — в аудиторский тег.</param>
        public async Task<AutoCloseResult> TryCloseAsync(
            string machine, DateTime date, AiAnalysisResult result, IReadOnlyList<Part> analyzedParts,
            string? aiModel, bool thinkingEnabled, CancellationToken ct = default)
        {
            var outcome = await DecideAsync(machine, date, result, analyzedParts, aiModel, thinkingEnabled, ct);
            if (outcome.Status is not (AutoCloseStatus.Disabled or AutoCloseStatus.LimitReached))
            {
                try
                {
                    await _store.SaveRunLogAsync(new AutoCloseLogEntry
                    {
                        Machine = machine,
                        ShiftDate = date.Date,
                        RunAt = _clock(),
                        RunBy = _run.RunBy,
                        DryRun = _run.DryRun,
                        Outcome = outcome.Status switch
                        {
                            AutoCloseStatus.Closed => AutoCloseLogEntry.OutcomeClosed,
                            AutoCloseStatus.WouldClose => AutoCloseLogEntry.OutcomeWouldClose,
                            AutoCloseStatus.Deferred => AutoCloseLogEntry.OutcomeDeferred,
                            AutoCloseStatus.Manual => AutoCloseLogEntry.OutcomeManual,
                            _ => AutoCloseLogEntry.OutcomeError,
                        },
                        Reasons = outcome.Reasons,
                        AiRequiresReview = result.RequiresReview,
                        AiConfidence = result.Confidence,
                        AiPromptVersion = result.PromptVersion,
                        ExclusionsApplied = outcome.ExclusionsApplied,
                    });
                }
                catch
                {
                    // Журнал вспомогательный: его сбой не должен менять исход закрытия.
                }
            }
            return outcome;
        }

        private async Task<AutoCloseResult> DecideAsync(
            string machine, DateTime date, AiAnalysisResult result, IReadOnlyList<Part> analyzedParts,
            string? aiModel, bool thinkingEnabled, CancellationToken ct)
        {
            if (!_run.Enabled) return new(AutoCloseStatus.Disabled, new[] { "автозакрытие выключено" });
            if (_decided >= _run.MaxCloses)
                return new(AutoCloseStatus.LimitReached, new[] { $"достигнут предел {_run.MaxCloses} за запуск" });

            date = date.Date;
            var parts = await _store.ReadPartsAsync(machine, date, ct);
            if (AutoCloseFingerprint.Of(parts) != AutoCloseFingerprint.Of(analyzedParts))
                return new(AutoCloseStatus.Deferred, new[] { "данные суток изменились после анализа" });

            var dayRead = await _store.ReadShiftAsync(machine, date, ShiftType.Day);
            var nightRead = await _store.ReadShiftAsync(machine, date, ShiftType.Night);
            foreach (var read in new[] { dayRead, nightRead })
                if (read.Status != DbResult.Ok && read.Status != DbResult.NotFound)
                    return new(AutoCloseStatus.Error, new[] { $"не удалось прочитать отчёт мастера: {read.Error}" });

            var existing = await _store.GetDayReviewAsync(machine, date);
            var day = dayRead.IsOk ? dayRead.Value : null;
            var night = nightRead.IsOk ? nightRead.Value : null;

            var decision = AutoCloseGate.Evaluate(new AutoCloseRequest(
                result, parts, day != null, night != null, date, _clock(),
                AlreadyReviewedByHuman: existing != null && !existing.IsAutoClosed,
                AlreadyChecked: day?.IsChecked == true && night?.IsChecked == true), _gate);

            switch (decision.Outcome)
            {
                case AutoCloseOutcome.Defer:
                    return new(AutoCloseStatus.Deferred, decision.Reasons);
                case AutoCloseOutcome.Manual:
                    return new(AutoCloseStatus.Manual, decision.Reasons);
            }

            _decided++;
            if (_run.DryRun)
            {
                _closed++;
                return new(AutoCloseStatus.WouldClose, Array.Empty<string>(), decision.Exclusions.Count);
            }

            var closed = await CloseAsync(machine, date, result, parts, decision, day!, night!, aiModel, thinkingEnabled);
            if (closed.Status == AutoCloseStatus.Closed) _closed++;
            return closed;
        }

        private async Task<AutoCloseResult> CloseAsync(
            string machine, DateTime date, AiAnalysisResult result, IReadOnlyList<Part> parts,
            AutoCloseDecision decision, ShiftInfo day, ShiftInfo night, string? aiModel, bool thinkingEnabled)
        {
            var applied = new List<Part>();

            async Task RollbackExclusionsAsync()
            {
                foreach (var part in applied)
                {
                    part.ExcludeFromReports = false;
                    await _store.UpdatePartAsync(part);
                    part.NeedUpdate = false;
                }
                if (applied.Count > 0)
                    await _store.RevertExcludeLogAsync(machine, date,
                        applied.Select(p => p.Guid).ToList(), DayReview.AutoReviewer);
            }

            AutoCloseResult Fail(string message) =>
                new(AutoCloseStatus.Error, new[] { message + " — изменения откачены" });

            if (decision.Exclusions.Count > 0)
            {
                var log = decision.Exclusions.Select(e => new AutoExcludeLogEntry
                {
                    Machine = machine,
                    ShiftDate = date,
                    PartGuid = e.Part.Guid,
                    PartName = e.Part.PartName,
                    Setup = e.Part.Setup,
                    Order = e.Part.Order,
                    Reason = e.Reason,
                    AiPromptVersion = result.PromptVersion,
                    ExcludedAt = _clock(),
                }).ToList();

                var logged = await _store.SaveExcludeLogAsync(log);
                if (!logged.IsOk) return Fail($"не записан журнал исключений: {logged.Error}");

                foreach (var (part, _) in decision.Exclusions)
                {
                    part.ExcludeFromReports = true;
                    applied.Add(part);
                    var upd = await _store.UpdatePartAsync(part);
                    if (upd.Status != DbResult.Ok)
                    {
                        await RollbackExclusionsAsync();
                        return Fail($"не сохранена строка {part.PartName} (Уст.{part.Setup}): {upd.Error}");
                    }
                    part.NeedUpdate = false;
                }
            }

            var comment = parts.Count == 0
                ? $"{DayReview.AutoCloseCommentMarker} записей нет"
                : $"{DayReview.AutoCloseCommentMarker} ИИ ок"
                  + (decision.Exclusions.Count > 0 ? $", исключено строк: {decision.Exclusions.Count}" : "");
            var agentMark = result.PromptVersion?.EndsWith("@agent") == true ? " агент" : "";
            var review = new DayReview(machine, date, DayReview.AutoReviewer, AnalystDecision.Ok, true, comment)
            {
                AiResult = result,
                AiThinkingEnabled = thinkingEnabled,
                AiModelVersion = aiModel,
                AiFeedback = $"[ИИ {aiModel}/{result.PromptVersion}{agentMark}]",
                AutoClosed = true,
            };
            var saved = await _store.SaveDayReviewAsync(review);
            if (!saved.IsOk)
            {
                await RollbackExclusionsAsync();
                return Fail($"не записано ревью: {saved.Error}");
            }
            await _store.ClearPartFlagsAsync(review.Id);

            var dayWas = day.IsChecked;
            var nightWas = night.IsChecked;
            day.IsChecked = true;
            var wroteDay = await _store.WriteShiftAsync(day);
            if (!wroteDay.IsOk)
            {
                day.IsChecked = dayWas;
                await RollbackExclusionsAsync();
                return Fail($"не отмечена дневная смена: {wroteDay.Error}");
            }

            night.IsChecked = true;
            var wroteNight = await _store.WriteShiftAsync(night);
            if (!wroteNight.IsOk)
            {
                night.IsChecked = nightWas;
                day.IsChecked = dayWas;
                await _store.WriteShiftAsync(day);
                await RollbackExclusionsAsync();
                return Fail($"не отмечена ночная смена: {wroteNight.Error}");
            }

            return new(AutoCloseStatus.Closed, Array.Empty<string>(), decision.Exclusions.Count);
        }
    }
}
