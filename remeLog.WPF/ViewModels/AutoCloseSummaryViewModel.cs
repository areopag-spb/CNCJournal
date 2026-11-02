using libeLog;
using libeLog.Base;
using libeLog.Views;
using remeLog.Core.Db;
using remeLog.Core.Services;
using remeLog.Infrastructure;
using remeLog.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace remeLog.ViewModels
{
    internal sealed class AutoCloseSummaryRow
    {
        public AutoCloseSummaryRow(string label, string value, bool isAlert = false)
        {
            Label = label;
            Value = value;
            IsAlert = isAlert;
        }

        public string Label { get; }
        public string Value { get; }
        /// <summary> Подсветка критичной строки (пропуск hard-класса). </summary>
        public bool IsAlert { get; }
    }

    /// <summary> Закрытые ИИ сутки в списке выборочного контроля. </summary>
    internal sealed class AutoCloseControlRow : ViewModel
    {
        public AutoCloseControlRow(AutoCloseLogEntry entry) => Entry = entry;

        public AutoCloseLogEntry Entry { get; }

        public DateTime Date => Entry.ShiftDate;
        public string Machine => Entry.Machine;
        public string Excluded => Entry.ExclusionsApplied > 0 ? Entry.ExclusionsApplied.ToString() : "";
        public string Confidence => Entry.AiConfidence is { } c ? $"{c:P0}" : "";

        public string ControlText => Entry.ControlVerdict switch
        {
            AutoCloseLogEntry.ControlAgree => $"согласен ({Entry.ControlBy}, {Entry.ControlAt:dd.MM HH:mm})",
            AutoCloseLogEntry.ControlMiss => $"пропуск ({Entry.ControlBy}, {Entry.ControlAt:dd.MM HH:mm})",
            AutoCloseLogEntry.ControlMissHard => $"ПРОПУСК HARD ({Entry.ControlBy}, {Entry.ControlAt:dd.MM HH:mm})",
            _ => "не проверено",
        };

        public void Refresh() => OnPropertyChanged(nameof(ControlText));
    }

    /// <summary>
    /// Окно «Сводка автозакрытия»: что ИИ закрыл, что оставил человеку и почему, как идёт
    /// выборочный контроль закрытых суток (research/ai-service/auto-close-plan.md, A7/P1.4).
    /// </summary>
    internal sealed class AutoCloseSummaryViewModel : ViewModel
    {
        private bool _loading;

        public ObservableCollection<AutoCloseSummaryRow> SummaryRows { get; } = new();
        public ObservableCollection<AutoCloseReasonCount> Reasons { get; } = new();
        public ObservableCollection<AutoCloseControlRow> ClosedDays { get; } = new();

        private DateTime _FromDate = DateTime.Today.AddDays(-30);
        public DateTime FromDate
        {
            get => _FromDate;
            set
            {
                if (Set(ref _FromDate, value) && !_loading) _ = LoadAsync();
            }
        }

        private DateTime _ToDate = DateTime.Today;
        public DateTime ToDate
        {
            get => _ToDate;
            set
            {
                if (Set(ref _ToDate, value) && !_loading) _ = LoadAsync();
            }
        }

        private string _StatusText = "Загрузка...";
        public string StatusText
        {
            get => _StatusText;
            set => Set(ref _StatusText, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand AgreeCommand { get; }
        public ICommand MissCommand { get; }
        public ICommand MissHardCommand { get; }

        public AutoCloseSummaryViewModel()
        {
            RefreshCommand = new LambdaCommand(_ => _ = LoadAsync(), _ => !_loading);
            AgreeCommand = new LambdaCommand(
                async p => await ControlAsync(p, AutoCloseLogEntry.ControlAgree), p => p is AutoCloseControlRow);
            MissCommand = new LambdaCommand(
                async p => await ControlAsync(p, AutoCloseLogEntry.ControlMiss), p => p is AutoCloseControlRow);
            MissHardCommand = new LambdaCommand(
                async p => await ControlAsync(p, AutoCloseLogEntry.ControlMissHard), p => p is AutoCloseControlRow);
            _ = LoadAsync();
        }

        public async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                if (FromDate > ToDate)
                {
                    StatusText = "Начало периода позже конца — поправьте даты";
                    return;
                }

                StatusText = "Загрузка журнала автозакрытия...";
                var log = await Database.GetAutoCloseLogAsync(FromDate, ToDate);

                var machines = log.Select(e => e.Machine).Distinct().ToList();
                var reviews = machines.Count == 0
                    ? new Dictionary<(string Machine, DateTime Date), DayReview>()
                    : await Database.GetDayReviewsForPeriodAsync(machines, FromDate, ToDate);

                var summary = AutoCloseSummary.Build(log, reviews, DateTime.Now);
                Fill(summary, log.Count);
            }
            catch (Exception ex)
            {
                Util.WriteLog(ex, "AutoCloseSummary");
                StatusText = $"Ошибка загрузки: {ex.Message}";
            }
            finally
            {
                _loading = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void Fill(AutoCloseSummary s, int logCount)
        {
            SummaryRows.Clear();
            SummaryRows.Add(new("Закрыто ИИ", s.Closed.ToString()));
            SummaryRows.Add(new("Оставлено человеку", s.Manual.ToString()));
            SummaryRows.Add(new("Отложено", s.Deferred.ToString()));
            SummaryRows.Add(new("Ошибки записи", s.Errors.ToString(), s.Errors > 0));
            SummaryRows.Add(new("Закрыто ИИ, потом изменено человеком", s.ReopenedByHuman.ToString()));
            SummaryRows.Add(new("Тень: был бы закрыт", s.ShadowWouldClose.ToString()));
            SummaryRows.Add(new("Тень: оставлено бы человеку", s.ShadowManual.ToString()));
            SummaryRows.Add(new("Контроль: согласен / пропуск / пропуск hard",
                $"{s.ControlAgree} / {s.ControlMiss} / {s.ControlMissHard}", s.HasHardMiss));
            SummaryRows.Add(new("Проверено на этой неделе (цель 10–20)", s.ControlledThisWeek.ToString()));
            if (s.HasHardMiss)
                SummaryRows.Add(new("ВНИМАНИЕ", "найден пропуск hard-класса — автозакрытие остановить и разобрать", true));

            Reasons.Clear();
            foreach (var r in s.TopReasons) Reasons.Add(r);

            ClosedDays.Clear();
            foreach (var e in s.ClosedForControl) ClosedDays.Add(new AutoCloseControlRow(e));

            StatusText = logCount == 0
                ? "За период решений автозакрытия нет (или БД ещё не обновлена)"
                : $"{FromDate:dd.MM.yyyy}–{ToDate:dd.MM.yyyy}: решений в журнале {logCount}, закрыто ИИ {s.Closed}";
        }

        private async Task ControlAsync(object? parameter, string verdict)
        {
            if (parameter is not AutoCloseControlRow row) return;

            if (verdict == AutoCloseLogEntry.ControlMissHard)
            {
                var answer = MessageBoxWindow.Show(
                    "Пропуск hard-класса — это критерий остановки автозакрытия. Отметить?",
                    "Пропуск hard-класса", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxDefaultButton.No);
                if (answer != MessageBoxResult.Yes) return;
            }

            var saved = await Database.SetAutoCloseControlAsync(
                row.Entry.Id, verdict, Environment.UserName, null);
            if (saved.Status != DbResult.Ok)
            {
                MessageBoxWindow.Show($"Не удалось сохранить отметку: {saved.Error}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Перечитываем целиком: счётчики и подсветка пересчитываются из журнала.
            await LoadAsync();
        }
    }
}
