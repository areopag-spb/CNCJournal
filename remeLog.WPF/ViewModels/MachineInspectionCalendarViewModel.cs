using libeLog;
using libeLog.Base;
using libeLog.Extensions;
using libeLog.Models;
using remeLog.Infrastructure;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using remeLog.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using libeLog.Views;

namespace remeLog.ViewModels
{
    internal class MachineInspectionCalendarViewModel : ViewModel, IDisposable
    {
        private DateTime _FromDate;
        private DateTime _ToDate;
        private bool _isSyncing;
        private static bool lockUpdate;
        private int _SelectedMonth;
        private int _SelectedYear;
        private ObservableCollection<MachineFilter> _MachineFilters = new();
        private ObservableCollection<MachineInspectionCalendarDayRow> _Days = new();
        private List<string> _AllMachines = new();
        private string _Statistics = string.Empty;
        private readonly HashSet<DateTime> _highlightedDates = new();
        private readonly PeriodicTimer _refreshTimer = new(TimeSpan.FromSeconds(10));
        private CancellationTokenSource _cts = new();
        private int _reloadVersion;

        public MachineInspectionCalendarViewModel(DateTime fromDate, DateTime toDate)
        {
            _FromDate = fromDate;
            _ToDate = toDate;
            _SelectedMonth = fromDate.Month;
            _SelectedYear = fromDate.Year;
            AvailableMonths = Enumerable.Range(1, 12)
                .Select(m => new MonthItem(m, new DateTime(2000, m, 1).ToString("MMMM")))
                .ToList();
            AvailableYears = Enumerable.Range(2023, DateTime.Now.Year - 2023 + 1).ToList();
            ShowAllMachinesCommand = LambdaCommand.Create(_ => ExecuteShowAll());
            HideAllMachinesCommand = LambdaCommand.Create(_ => ExecuteHideAll());
            InvertMachinesCommand = LambdaCommand.Create(_ => ExecuteInvert());
            OpenPartsInfoCommand = LambdaCommand.Create(OnOpenPartsInfo, _ => true);
            _ = InitializeAsync();
        }

        public DateTime FromDate
        {
            get => _FromDate;
            set
            {
                if (Set(ref _FromDate, value))
                {
                    OnPropertyChanged(nameof(PeriodTitle));
                    if (!_isSyncing)
                    {
                        _isSyncing = true;
                        SelectedMonth = FromDate.Month;
                        SelectedYear = FromDate.Year;
                        _isSyncing = false;
                    }
                    _ = ReloadShiftsAsync();
                }
            }
        }

        public DateTime ToDate
        {
            get => _ToDate;
            set
            {
                if (Set(ref _ToDate, value))
                {
                    OnPropertyChanged(nameof(PeriodTitle));
                    _ = ReloadShiftsAsync();
                }
            }
        }

        public List<MonthItem> AvailableMonths { get; }
        public List<int> AvailableYears { get; }

        public int SelectedMonth
        {
            get => _SelectedMonth;
            set
            {
                if (Set(ref _SelectedMonth, value) && !_isSyncing)
                    UpdateFromSelectedMonthYear();
            }
        }

        public int SelectedYear
        {
            get => _SelectedYear;
            set
            {
                if (Set(ref _SelectedYear, value) && !_isSyncing)
                    UpdateFromSelectedMonthYear();
            }
        }

        public ObservableCollection<MachineFilter> MachineFilters
        {
            get => _MachineFilters;
            set => Set(ref _MachineFilters, value);
        }

        public ObservableCollection<MachineInspectionCalendarDayRow> Days
        {
            get => _Days;
            set => Set(ref _Days, value);
        }

        public List<string> AllMachines
        {
            get => _AllMachines;
            set => Set(ref _AllMachines, value);
        }

        public bool IsReloading { get; private set; }

        public string Statistics
        {
            get => _Statistics;
            set => Set(ref _Statistics, value);
        }

        public string PeriodTitle => $"Календарь ТО: {FromDate:dd.MM.yyyy} - {ToDate:dd.MM.yyyy}";

        public ICommand ShowAllMachinesCommand { get; }
        public ICommand HideAllMachinesCommand { get; }
        public ICommand InvertMachinesCommand { get; }
        public ICommand OpenPartsInfoCommand { get; }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _refreshTimer.Dispose();
        }

        private void LockUpdate() => lockUpdate = true;

        private void UnlockUpdate()
        {
            lockUpdate = false;
            _ = ReloadShiftsAsync();
        }

        private void UpdateFromSelectedMonthYear()
        {
            LockUpdate();
            FromDate = new DateTime(_SelectedYear, _SelectedMonth, 1);
            ToDate = new DateTime(_SelectedYear, _SelectedMonth, DateTime.DaysInMonth(_SelectedYear, _SelectedMonth));
            UnlockUpdate();
        }

        private void ExecuteShowAll()
        {
            foreach (var mf in MachineFilters) mf.Filter = true;
            RefreshFilter();
        }

        private void ExecuteHideAll()
        {
            foreach (var mf in MachineFilters) mf.Filter = false;
            RefreshFilter();
        }

        private void ExecuteInvert()
        {
            foreach (var mf in MachineFilters) mf.Filter = !mf.Filter;
            RefreshFilter();
        }

        private void OnOpenPartsInfo(object? p)
        {
            if (p is not MachineInspectionCalendarCell cell) return;

            var partsInfo = new CombinedParts(cell.Machine, cell.Date, cell.Date);
            var window = new PartsInfoWindow(partsInfo)
            {
                Owner = Application.Current.MainWindow
            };
            window.Show();
        }

        private void RefreshFilter()
        {
            SaveFilter();
            UpdateCheckedPercents();
            UpdateStatistics();
            OnPropertyChanged(nameof(FilteredMachines));
            OnPropertyChanged(nameof(MachineFilterSummary));
            OnPropertyChanged(nameof(Days));
        }

        public List<string> FilteredMachines =>
            MachineFilters.Where(m => m.Filter).Select(m => m.Machine).ToList();

        /// <summary> Текст на кнопке фильтра по станкам. </summary>
        public string MachineFilterSummary => MachineFilters.Summary();

        private void SaveFilter()
        {
            AppSettings.Instance.MachineInspectionCalendarSelectedMachines =
                MachineFilters.Where(m => m.Filter).Select(m => m.Machine).ToList();
            AppSettings.Save();
        }

        private void UpdateStatistics()
        {
            var selectedMachines = MachineFilters.Where(m => m.Filter).ToList();
            var total = selectedMachines.Count;
            var checkedCount = 0;

            foreach (var day in Days)
            {
                foreach (var cell in day.Cells)
                {
                    if (selectedMachines.Any(m => m.Machine == cell.Machine) && cell.IsChecked)
                    {
                        checkedCount++;
                    }
                }
            }

            var totalCells = total * Days.Count;
            Statistics = totalCells > 0
                ? $"Проверено: {checkedCount:00}/{totalCells:00} ({(Convert.ToDouble(checkedCount) / totalCells):0.#%})"
                : $"Проверено: 00/00";
        }

        private async Task InitializeAsync()
        {
            try
            {
                var (status, machineFilters, _) = await Database.ReadMachinesAsync();
                if (status != remeLog.Core.Db.DbResult.Ok || machineFilters == null) return;

                var savedMachines = AppSettings.Instance.MachineInspectionCalendarSelectedMachines ?? new List<string>();

                foreach (var mf in machineFilters)
                {
                    mf.Filter = savedMachines.Count == 0 || savedMachines.Contains(mf.Machine);
                    mf.PropertyChanged += (_, _) =>
                    {
                        SaveFilter();
                        UpdateCheckedPercents();
                        UpdateStatistics();
                        OnPropertyChanged(nameof(FilteredMachines));
                        OnPropertyChanged(nameof(MachineFilterSummary));
                        OnPropertyChanged(nameof(Days));
                    };
                }

                _MachineFilters = machineFilters.ToObservableCollection();
                OnPropertyChanged(nameof(MachineFilters));
                OnPropertyChanged(nameof(FilteredMachines));
                OnPropertyChanged(nameof(MachineFilterSummary));

                _AllMachines = machineFilters.Select(m => m.Machine).ToList();
                OnPropertyChanged(nameof(AllMachines));

                await ReloadShiftsAsync();
                OnPropertyChanged(nameof(SelectedMonth));
                OnPropertyChanged(nameof(SelectedYear));
                StartRefreshLoop();
            }
            catch (Exception ex)
            {
                Util.WriteLog(ex);
                MessageBoxWindow.Show($"Ошибка загрузки данных: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartRefreshLoop()
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await _refreshTimer.WaitForNextTickAsync(_cts.Token);
                        await App.Current.Dispatcher.InvokeAsync(async () => await ReloadShiftsAsync());
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Util.WriteLog(ex);
                    }
                }
            }, _cts.Token);
        }

        private async Task ReloadShiftsAsync()
        {
            if (lockUpdate) return;
            var machines = _AllMachines;
            if (machines.Count == 0) return;

            var reloadToken = ++_reloadVersion;
            IsReloading = true;
            try
            {
                await ReloadShiftsCoreAsync(machines);
            }
            finally
            {
                if (reloadToken == _reloadVersion)
                    IsReloading = false;
            }
        }

        private async Task ReloadShiftsCoreAsync(List<string> machines)
        {
            var shiftsResult = await Task.Run(() =>
                Database.GetShiftsByPeriod(machines, FromDate, ToDate, new Shift(ShiftType.All)));

            if (!shiftsResult.IsOk || shiftsResult.Value == null) return;

            var shifts = shiftsResult.Value;
            var shiftLookup = shifts.ToLookup(s => (s.Machine, s.ShiftDate.Date));

            // Кто закрыл сутки: ревью «ИИ-авто» отличает зелёный «глаз» от обычной галочки.
            // Нет столбцов/таблицы или сбой — календарь остаётся прежним (галочки).
            var reviews = await Database.GetDayReviewsForPeriodAsync(machines, FromDate, ToDate);

            var days = new List<MachineInspectionCalendarDayRow>();
            for (var current = FromDate; current <= ToDate; current = current.AddDays(1))
            {
                if (AppSettings.Holidays.Contains(current.Date)) continue;
                var cells = new ObservableCollection<MachineInspectionCalendarCell>();
                foreach (var machine in machines)
                {
                    var dayShift = shiftLookup[(machine, current)]
                        .FirstOrDefault(s => s.Shift == Shifts.Day);
                    var nightShift = shiftLookup[(machine, current)]
                        .FirstOrDefault(s => s.Shift == Shifts.Night);

                    bool hasShift = dayShift != null || nightShift != null;
                    bool isChecked = (dayShift?.IsChecked == true) && (nightShift?.IsChecked == true);
                    var closedByAi = isChecked
                        && reviews.TryGetValue((machine, current.Date), out var review)
                        && review.IsAutoClosed;

                    cells.Add(new MachineInspectionCalendarCell
                    {
                        Machine = machine,
                        Date = current,
                        HasShift = hasShift,
                        IsChecked = isChecked,
                        IsClosedByAi = closedByAi
                    });
                }

                var dow = current.DayOfWeek switch
                {
                    DayOfWeek.Monday => "Понедельник",
                    DayOfWeek.Tuesday => "Вторник",
                    DayOfWeek.Wednesday => "Среда",
                    DayOfWeek.Thursday => "Четверг",
                    DayOfWeek.Friday => "Пятница",
                    DayOfWeek.Saturday => "Суббота",
                    DayOfWeek.Sunday => "Воскресенье",
                    _ => ""
                };

                var row = new MachineInspectionCalendarDayRow
                {
                    Date = current,
                    DayOfWeekShort = dow,
                    Cells = cells
                };
                days.Add(row);
            }

            _Days = days.ToObservableCollection();
            UpdateCheckedPercents();
            OnPropertyChanged(nameof(Days));
            UpdateStatistics();
        }

        public bool IsDateHighlighted(DateTime date) => _highlightedDates.Contains(date);

        public void SetDateHighlighted(DateTime date, bool highlighted)
        {
            if (highlighted) _highlightedDates.Add(date);
            else _highlightedDates.Remove(date);
        }

        private void UpdateCheckedPercents()
        {
            var visible = FilteredMachines;
            if (visible.Count == 0)
            {
                foreach (var day in Days) day.CheckedPercent = "—";
                return;
            }

            foreach (var day in Days)
            {
                var checkedCount = day.Cells.Count(c => visible.Contains(c.Machine) && c.IsChecked);
                var percent = Convert.ToDouble(checkedCount) / visible.Count;
                day.CheckedPercent = $"{checkedCount}/{visible.Count} · {percent:000.0%}";
            }
        }
    }

    internal class MachineInspectionCalendarCell
    {
        public string Machine { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public bool IsChecked { get; set; }
        /// <summary> Сутки закрыл ИИ без человека (зелёный «глаз» вместо галочки). </summary>
        public bool IsClosedByAi { get; set; }
        public bool HasShift { get; set; }
    }

    internal class MachineInspectionCalendarDayRow : INotifyPropertyChanged
    {
        private string _checkedPercent = string.Empty;

        public DateTime Date { get; set; }
        public string DayOfWeekShort { get; set; } = string.Empty;
        public string DateDisplay => $"{Date:dd.MM}";
        public ObservableCollection<MachineInspectionCalendarCell> Cells { get; set; } = new();

        public string CheckedPercent
        {
            get => _checkedPercent;
            set
            {
                if (_checkedPercent == value) return;
                _checkedPercent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckedPercent)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    internal record MonthItem(int Value, string Name);
}
