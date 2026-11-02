using remeLog.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace remeLog.Core.Services
{
    /// <summary> Строка сводки «причина — сколько раз». </summary>
    public sealed record AutoCloseReasonCount(string Reason, int Count);

    /// <summary>
    /// Сводка автозакрытия за период для окна клиента (research/ai-service/auto-close-plan.md, A7).
    /// Считается по журналу решений: на каждые сутки берётся последнее решение отдельно для
    /// реальных запусков и для теневых — повторные прогоны одних суток не раздувают цифры.
    /// </summary>
    public sealed class AutoCloseSummary
    {
        // «21.10 12:00», «79%» и т.п. — в причинах склеиваем по тексту без чисел.
        private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

        /// <summary> Реальные запуски (в БД писалось). </summary>
        public int Closed { get; private set; }
        public int Manual { get; private set; }
        public int Deferred { get; private set; }
        public int Errors { get; private set; }

        /// <summary> Теневые запуски (в БД не писалось). </summary>
        public int ShadowWouldClose { get; private set; }
        public int ShadowManual { get; private set; }
        public int ShadowDeferred { get; private set; }
        public int ShadowErrors { get; private set; }

        /// <summary> Закрытые ИИ сутки, по которым человек потом изменил решение. </summary>
        public int ReopenedByHuman { get; private set; }

        public int ControlAgree { get; private set; }
        public int ControlMiss { get; private set; }
        public int ControlMissHard { get; private set; }

        /// <summary> Проверок выборочного контроля с понедельника текущей недели. </summary>
        public int ControlledThisWeek { get; private set; }

        /// <summary> Закрытые ИИ сутки (реальные), новые сверху — для выборочного контроля. </summary>
        public IReadOnlyList<AutoCloseLogEntry> ClosedForControl { get; private set; } = Array.Empty<AutoCloseLogEntry>();

        /// <summary> Почему сутки остались человеку / отложены / упали (реальные и теневые вместе). </summary>
        public IReadOnlyList<AutoCloseReasonCount> TopReasons { get; private set; } = Array.Empty<AutoCloseReasonCount>();

        /// <summary> Критерий остановки (P1.4): найден пропуск hard-класса. </summary>
        public bool HasHardMiss => ControlMissHard > 0;

        public int ControlChecked => ControlAgree + ControlMiss + ControlMissHard;

        public static AutoCloseSummary Build(
            IEnumerable<AutoCloseLogEntry> entries,
            IReadOnlyDictionary<(string Machine, DateTime Date), DayReview> reviews,
            DateTime now)
        {
            var latest = entries
                .GroupBy(e => (e.Machine, Date: e.ShiftDate.Date, e.DryRun))
                .Select(g => g.OrderBy(e => e.RunAt).ThenBy(e => e.Id).Last())
                .ToList();

            var summary = new AutoCloseSummary();
            var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            var closed = new List<AutoCloseLogEntry>();

            foreach (var e in latest)
            {
                if (e.DryRun)
                {
                    switch (e.Outcome)
                    {
                        case AutoCloseLogEntry.OutcomeWouldClose: summary.ShadowWouldClose++; break;
                        case AutoCloseLogEntry.OutcomeManual: summary.ShadowManual++; break;
                        case AutoCloseLogEntry.OutcomeDeferred: summary.ShadowDeferred++; break;
                        case AutoCloseLogEntry.OutcomeError: summary.ShadowErrors++; break;
                    }
                    continue;
                }

                switch (e.Outcome)
                {
                    case AutoCloseLogEntry.OutcomeClosed:
                        summary.Closed++;
                        closed.Add(e);
                        if (reviews.TryGetValue((e.Machine, e.ShiftDate.Date), out var review) && !review.IsAutoClosed)
                            summary.ReopenedByHuman++;
                        switch (e.ControlVerdict)
                        {
                            case AutoCloseLogEntry.ControlAgree: summary.ControlAgree++; break;
                            case AutoCloseLogEntry.ControlMiss: summary.ControlMiss++; break;
                            case AutoCloseLogEntry.ControlMissHard: summary.ControlMissHard++; break;
                        }
                        if (e.ControlAt is { } at && at >= weekStart) summary.ControlledThisWeek++;
                        break;
                    case AutoCloseLogEntry.OutcomeManual: summary.Manual++; break;
                    case AutoCloseLogEntry.OutcomeDeferred: summary.Deferred++; break;
                    case AutoCloseLogEntry.OutcomeError: summary.Errors++; break;
                }
            }

            summary.ClosedForControl = closed
                .OrderByDescending(e => e.ShiftDate).ThenBy(e => e.Machine).ToList();

            summary.TopReasons = latest
                .Where(e => e.Outcome is AutoCloseLogEntry.OutcomeManual
                    or AutoCloseLogEntry.OutcomeDeferred or AutoCloseLogEntry.OutcomeError)
                .SelectMany(e => e.Reasons)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .GroupBy(r => Digits.Replace(r.Trim(), "#"))
                .Select(g => new AutoCloseReasonCount(g.Key, g.Count()))
                .OrderByDescending(x => x.Count).ThenBy(x => x.Reason)
                .ToList();

            return summary;
        }
    }
}
