using eLog.Infrastructure;
using libeLog;
using libeLog.Base;
using remeLog.Core;
using remeLog.Models.Reports;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CoreDb = remeLog.Infrastructure.Database;

namespace eLog.ViewModels
{
    /// <summary>Строка таблицы К1 в окне оператора (значения уже отформатированы).</summary>
    internal sealed record OperatorKpiLine(
        string Machine,
        string Shifts,
        string MarkedTime,
        string IncludedShare,
        string General,
        string EfficiencyCoefficient,
        string Downtimes,
        string DowntimeCoefficient,
        string K1,
        string K1Tone,
        string Reasons);

    /// <summary>Вариант выбора периода: месяц целиком (для текущего — по сегодня) либо «произвольный» (без дат).</summary>
    internal sealed record PeriodOption(string Title, DateTime? From, DateTime? To);

    /// <summary>
    /// Предварительный К1 текущего оператора за выбранный период (по умолчанию с начала месяца по сегодня).
    /// Расчёт общий с отчётом по операторам в remeLog — <see cref="OperatorKpiService"/>.
    /// </summary>
    internal class OperatorKpiViewModel : ViewModel
    {
        private CancellationTokenSource? _cts;

        private const int MonthsInList = 12;
        private readonly PeriodOption _customPeriod = new("Произвольный период", null, null);
        private bool _syncingPeriod;

        public OperatorKpiViewModel()
        {
            var today = DateTime.Today;
            _FromDate = new DateTime(today.Year, today.Month, 1);
            _ToDate = today;
            OperatorName = AppSettings.Instance.CurrentOperator?.FullName ?? string.Empty;

            Periods.Add(_customPeriod);
            for (var i = 0; i < MonthsInList; i++)
            {
                var first = new DateTime(today.Year, today.Month, 1).AddMonths(-i);
                var title = Capitalize(first.ToString("MMMM yyyy"));
                Periods.Add(i == 0
                    ? new PeriodOption($"{title} (по сегодня)", first, today)
                    : new PeriodOption(title, first, first.AddMonths(1).AddDays(-1)));
            }
            _SelectedPeriod = Periods[1];

            RefreshCommand = new LambdaCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
        }

        private static string Capitalize(string text) =>
            text.Length == 0 ? text : char.ToUpper(text[0]) + text.Substring(1);

        public string OperatorName { get; }

        private DateTime _FromDate;
        public DateTime FromDate
        {
            get => _FromDate;
            set
            {
                if (Set(ref _FromDate, value.Date)) SyncSelectedPeriod();
            }
        }

        private DateTime _ToDate;
        public DateTime ToDate
        {
            get => _ToDate;
            set
            {
                if (Set(ref _ToDate, value.Date)) SyncSelectedPeriod();
            }
        }

        public ObservableCollection<PeriodOption> Periods { get; } = new();

        private PeriodOption _SelectedPeriod;
        /// <summary>Выбор месяца подставляет даты и сразу пересчитывает; «произвольный» выставляется сам при правке дат.</summary>
        public PeriodOption SelectedPeriod
        {
            get => _SelectedPeriod;
            set
            {
                if (value is null || !Set(ref _SelectedPeriod, value) || _syncingPeriod || value.From is null) return;
                _syncingPeriod = true;
                try
                {
                    FromDate = value.From.Value;
                    ToDate = value.To!.Value;
                }
                finally
                {
                    _syncingPeriod = false;
                }
                _ = RefreshAsync();
            }
        }

        private void SyncSelectedPeriod()
        {
            if (_syncingPeriod) return;
            var match = Periods.FirstOrDefault(p => p.From == _FromDate && p.To == _ToDate) ?? _customPeriod;
            _syncingPeriod = true;
            try { SelectedPeriod = match; }
            finally { _syncingPeriod = false; }
        }

        private bool _IsBusy;
        public bool IsBusy
        {
            get => _IsBusy;
            private set
            {
                if (Set(ref _IsBusy, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        private string _Status = string.Empty;
        public string Status
        {
            get => _Status;
            private set => Set(ref _Status, value);
        }

        public ObservableCollection<OperatorKpiLine> Lines { get; } = new();

        public ICommand RefreshCommand { get; }
        public async Task RefreshAsync()
        {
            if (IsBusy) return;
            Lines.Clear();

            if (string.IsNullOrWhiteSpace(OperatorName))
            {
                Status = "Оператор не выбран.";
                return;
            }
            if (string.IsNullOrWhiteSpace(AppSettings.Instance.ConnectionString))
            {
                Status = "Не настроено подключение к базе данных.";
                return;
            }
            if (FromDate > ToDate)
            {
                Status = "Начальная дата позже конечной.";
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            IsBusy = true;
            Status = "Загрузка данных...";
            try
            {
                DomainSettings.ConnectionString = AppSettings.Instance.ConnectionString;
                await CoreDb.LoadKpiSettingsAsync();

                var operatorsTask = CoreDb.GetOperatorsAsync();
                var qualificationsTask = CoreDb.GetQualificationsAsync();
                var machinesTask = CoreDb.GetMachineProfilesAsync();
                var partsTask = CoreDb.ReadPartsByOperator(OperatorName, FromDate, ToDate, ct);
                await Task.WhenAll(operatorsTask, qualificationsTask, machinesTask, partsTask);

                var operators = await operatorsTask;
                var rules = K1RuleSet.Current;

                var parts = await partsTask;
                if (parts.Count == 0)
                {
                    Status = "За выбранный период нет завершённых отметок.";
                    return;
                }

                var info = operators.FirstOrDefault(o => o.FullName.Trim() == OperatorName);
                if (info is null || info.Qualification == 0)
                {
                    Status = "У оператора не указан разряд — К1 не считается.";
                    return;
                }

                var rows = OperatorKpiService.Calculate(new OperatorKpiRequest(
                    parts, FromDate, ToDate, IncludeSmallBatch: false, SerialParts: null, IncludeExcludedParts: false,
                    operators, await qualificationsTask, await machinesTask, rules));

                foreach (var row in rows)
                    Lines.Add(ToLine(row));

                Status = rows.Count == 0
                    ? "Нет данных для расчёта (отметки исключены из отчётов или не найден разряд)."
                    : string.Empty;
            }
            catch (OperationCanceledException)
            {
                Status = "Отменено.";
            }
            catch (Exception ex)
            {
                eLog.Infrastructure.Extensions.Util.WriteLog(ex, "Ошибка расчёта К1 оператора");
                Status = "Не удалось рассчитать: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static OperatorKpiLine ToLine(OperatorKpiRow row)
        {
            var a = row.Aggregates;
            var r = row.Result;
            var perShift = a.WorkedShifts > 0 ? a.TotalHours / a.WorkedShifts : 0;
            var included = a.TotalHours > 0 ? a.IncludedHours / a.TotalHours : 0;

            // К1 округляем до 0.1 и по округлённому значению выбираем цвет: ниже 1 — красный, ровно 1 — обычный, выше — зелёный.
            var k1Text = "—";
            var k1Tone = "None";
            if (r.K1 is double k1)
            {
                var rounded = Math.Round((decimal)k1, 1, MidpointRounding.AwayFromZero);
                k1Text = rounded.ToString("0.0");
                k1Tone = rounded < 1m ? "Below" : rounded > 1m ? "Above" : "Equal";
            }
            return new OperatorKpiLine(
                row.Machine,
                a.WorkedShifts.ToString(),
                $"{a.TotalHours:0.#} ч ({perShift:0.#} ч/смену)",
                included.ToString("P0"),
                r.GeneralRatio.ToString("P0"),
                r.EfficiencyCoefficient?.ToString("0.##") ?? "—",
                a.SpecifiedDowntimesRatioEx.ToString("P0"),
                r.DowntimeCoefficient?.ToString("0.##") ?? "—",
                k1Text,
                k1Tone,
                string.Join("\n", r.Reasons));
        }
    }
}
