using libeLog;
using libeLog.Base;
using remeLog.Core;
using remeLog.Core.Db;
using remeLog.Core.Services;
using remeLog.Infrastructure;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using remeLog.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace remeLog.ViewModels
{
    internal class BatchAiAnalysisViewModel : ViewModel
    {
        private readonly AiServiceClient _aiClient = new();
        private CancellationTokenSource? _cts;
        private int _refreshVersion;

        public ObservableCollection<BatchItem> Items { get; } = new();

        private bool _IsRunning;
        public bool IsRunning
        {
            get => _IsRunning;
            set
            {
                if (Set(ref _IsRunning, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private double _OverallProgress;
        public double OverallProgress
        {
            get => _OverallProgress;
            set => Set(ref _OverallProgress, value);
        }

        private string _StatusText = "Готов к запуску";
        public string StatusText
        {
            get => _StatusText;
            set => Set(ref _StatusText, value);
        }

        private int _TotalCount;
        public int TotalCount
        {
            get => _TotalCount;
            set => Set(ref _TotalCount, value);
        }

        private int _ProcessedCount;
        public int ProcessedCount
        {
            get => _ProcessedCount;
            set => Set(ref _ProcessedCount, value);
        }

        private bool _OnlyPending = true;
        public bool OnlyPending
        {
            get => _OnlyPending;
            set
            {
                if (Set(ref _OnlyPending, value))
                {
                    _ = RefreshListAsync();
                }
            }
        }

        private bool _AutoCloseEmpty;
        /// <summary> Отдельный режим: автозакрытие суток без записей при наличии
        /// обоих отчётов мастера. ИИ не используется. </summary>
        public bool AutoCloseEmpty
        {
            get => _AutoCloseEmpty;
            set
            {
                if (Set(ref _AutoCloseEmpty, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                    _ = RefreshListAsync();
                }
            }
        }

        /// <summary> Режим тени автозакрытия: ничего не писать в БД (настройка клиента). </summary>
        public bool AutoCloseDryRun
        {
            get => AppSettings.Instance.AutoCloseDryRun;
            set
            {
                if (AppSettings.Instance.AutoCloseDryRun == value) return;
                AppSettings.Instance.AutoCloseDryRun = value;
                AppSettings.Save();
                OnPropertyChanged();
            }
        }

        private DateTime _FromDate = DateTime.Today.AddDays(-30);
        /// <summary> Начало периода поиска пустых суток (только AutoCloseEmpty). </summary>
        public DateTime FromDate
        {
            get => _FromDate;
            set
            {
                if (Set(ref _FromDate, value) && AutoCloseEmpty && !IsRunning)
                    _ = RefreshListAsync();
            }
        }

        private DateTime _ToDate = DateTime.Today;
        /// <summary> Конец периода поиска пустых суток (только AutoCloseEmpty). </summary>
        public DateTime ToDate
        {
            get => _ToDate;
            set
            {
                if (Set(ref _ToDate, value) && AutoCloseEmpty && !IsRunning)
                    _ = RefreshListAsync();
            }
        }

        private bool _ThinkingEnabled;
        /// <summary> Режим ИИ-анализа: вкл — агентский контур, выкл — обычный thinking-режим.
        /// Имя сохранено ради привязки и настройки AppSettings.AiThinkingEnabled. </summary>
        public bool ThinkingEnabled
        {
            get => _ThinkingEnabled;
            set
            {
                if (Set(ref _ThinkingEnabled, value))
                {
                    AppSettings.Instance.AiThinkingEnabled = value;
                    AppSettings.Save();
                }
            }
        }

        public ICommand StartCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ShowAllMachinesCommand { get; }
        public ICommand HideAllMachinesCommand { get; }
        public ICommand InvertMachinesCommand { get; }
        public ICommand ShowAutoCloseHistoryCommand { get; }
        public ICommand ShowAutoCloseSummaryCommand { get; }

        /// <summary> Фильтр по станкам для режима автозакрытия (общий контрол,
        /// как в календаре проверки; выбор запоминается). </summary>
        public ObservableCollection<MachineFilter> MachineFilters { get; } = new();

        public List<string> FilteredMachines =>
            MachineFilters.Where(m => m.Filter).Select(m => m.Machine).ToList();

        /// <summary> Текст на кнопке фильтра по станкам. </summary>
        public string MachineFilterSummary => MachineFilters.Summary();

        public BatchAiAnalysisViewModel()
        {
            _ThinkingEnabled = AppSettings.Instance.AiThinkingEnabled;
            // Автозакрытие тоже идёт через ИИ-анализ суток — без ИИ Старт недоступен.
            StartCommand = new LambdaCommand(OnStartExecuted,
                _ => !IsRunning && AiHealthMonitor.Instance.IsAiAvailable);
            CancelCommand = new LambdaCommand(OnCancelExecuted, _ => IsRunning);
            RefreshCommand = new LambdaCommand(OnRefreshExecuted, _ => !IsRunning);
            ShowAllMachinesCommand = new LambdaCommand(_ => SetAllMachineFilters(true), _ => !IsRunning);
            HideAllMachinesCommand = new LambdaCommand(_ => SetAllMachineFilters(false), _ => !IsRunning);
            InvertMachinesCommand = new LambdaCommand(_ =>
            {
                foreach (var mf in MachineFilters) mf.Filter = !mf.Filter;
                AfterMachineFilterChanged();
            }, _ => !IsRunning);
            ShowAutoCloseHistoryCommand = new LambdaCommand(_ =>
            {
                var window = new AutoCloseHistoryWindow { Owner = Application.Current.MainWindow };
                window.Show();
            }, _ => !IsRunning);
            ShowAutoCloseSummaryCommand = new LambdaCommand(_ =>
            {
                var window = new AutoCloseSummaryWindow { Owner = Application.Current.MainWindow };
                window.Show();
            });
            // ИИ может упасть/подняться прямо при открытом окне — пересчитать доступность Старта.
            AiHealthMonitor.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AiHealthMonitor.IsAiAvailable))
                    CommandManager.InvalidateRequerySuggested();
            };
            _ = RefreshListAsync();
        }

        private async Task RefreshListAsync()
        {
            if (AutoCloseEmpty)
            {
                await RefreshEmptyCandidatesAsync();
                return;
            }
            StatusText = "Загрузка списка суток...";
            var all = await Database.GetAllDayReviewsAsync();
            var list = OnlyPending
                ? all.Where(r => !r.HasAiResult).OrderBy(r => r.ShiftDate).ThenBy(r => r.Machine).ToList()
                : all.OrderBy(r => r.ShiftDate).ThenBy(r => r.Machine).ToList();

            Items.Clear();
            foreach (var r in list)
            {
                Items.Add(new BatchItem
                {
                    DayReviewId = r.Id,
                    Date = r.ShiftDate,
                    Machine = r.Machine,
                    State = r.HasAiResult ? BatchItemState.Skipped : BatchItemState.Pending,
                    Summary = r.HasAiResult
                        ? BuildExistingSummary(r)
                        : "ожидание",
                });
            }
            TotalCount = Items.Count;
            ProcessedCount = 0;
            OverallProgress = 0;
            var toAnalyze = OnlyPending
                ? list.Count(r => !r.HasAiResult)
                : list.Count;
            StatusText = list.Count == 0
                ? "Нет записей для анализа"
                : $"К анализу: {toAnalyze} (всего в БД: {all.Count})";
        }

        private void SetAllMachineFilters(bool value)
        {
            foreach (var mf in MachineFilters) mf.Filter = value;
            AfterMachineFilterChanged();
        }

        private void AfterMachineFilterChanged()
        {
            SaveMachineFilter();
            OnPropertyChanged(nameof(FilteredMachines));
            OnPropertyChanged(nameof(MachineFilterSummary));
            if (AutoCloseEmpty && !IsRunning)
                _ = RefreshListAsync();
        }

        private void SaveMachineFilter()
        {
            AppSettings.Instance.BatchAutoCloseSelectedMachines =
                MachineFilters.Where(m => m.Filter).Select(m => m.Machine).ToList();
            AppSettings.Save();
        }

        /// <summary>
        /// Станки для фильтра: грузятся один раз, выбор восстанавливается
        /// из настроек. Пустой сохранённый выбор = выбраны все.
        /// </summary>
        /// <returns> false — станки прочитать не удалось. </returns>
        private async Task<bool> EnsureMachinesAsync()
        {
            if (_machinesLoaded) return MachineFilters.Count > 0;
            var machinesResult = await Database.ReadMachinesAsync();
            if (!machinesResult.IsOk)
            {
                StatusText = $"Не удалось прочитать станки: {machinesResult.Error}";
                return false;
            }
            var saved = AppSettings.Instance.BatchAutoCloseSelectedMachines ?? new List<string>();
            foreach (var mf in machinesResult.Value ?? Enumerable.Empty<MachineFilter>())
            {
                mf.Filter = saved.Count == 0 || saved.Contains(mf.Machine);
                mf.PropertyChanged += (_, _) => AfterMachineFilterChanged();
                MachineFilters.Add(mf);
            }
            _machinesLoaded = true;
            OnPropertyChanged(nameof(FilteredMachines));
            OnPropertyChanged(nameof(MachineFilterSummary));
            return MachineFilters.Count > 0;
        }

        private bool _machinesLoaded;

        /// <summary>
        /// Кандидаты на автозакрытие: пустые суткостанки за выбранный период, по
        /// которым есть оба отчёта мастера, но день ещё не проверен целиком.
        /// Праздники пропускаются, как в календаре проверки.
        /// </summary>
        private async Task RefreshEmptyCandidatesAsync()
        {
            var version = ++_refreshVersion;
            StatusText = "Поиск пустых суток с отчётами мастера...";
            Items.Clear();
            TotalCount = 0;
            ProcessedCount = 0;
            OverallProgress = 0;

            var to = ToDate.Date;
            var from = FromDate.Date;
            if (from > to)
            {
                StatusText = "Начало периода позже конца — поправьте даты";
                return;
            }

            if (!await EnsureMachinesAsync()) return;
            if (version != _refreshVersion) return;
            var machines = FilteredMachines;
            if (machines.Count == 0)
            {
                StatusText = "Не выбран ни один станок — отметьте станки в фильтре";
                return;
            }

            var shiftsResult = Database.GetShiftsByPeriod(machines, from, to, new Shift(ShiftType.All));
            if (!shiftsResult.IsOk)
            {
                StatusText = $"Не удалось прочитать смены: {shiftsResult.Error}";
                return;
            }

            // Сутки, по которым человек уже принял решение, ИИ не трогает (до анализа —
            // он дорогой). Авто-ревью («ИИ-авто») человеческим не считается.
            var humanReviewed = new HashSet<(string Machine, DateTime Date)>();
            var reviews = await Database.GetDayReviewsForPeriodAsync(machines, from, to);
            foreach (var (key, review) in reviews)
                if (!review.IsAutoClosed) humanReviewed.Add(key);
            if (version != _refreshVersion) return;

            var holidays = new HashSet<DateTime>(DomainSettings.Holidays.Select(d => d.Date));
            var maturity = AppSettings.Instance.AutoCloseMaturityHours;
            var count = 0;
            var closed = 0;
            var noReports = 0;
            var reviewed = 0;
            var notMature = 0;
            foreach (var g in (shiftsResult.Value ?? Enumerable.Empty<ShiftInfo>())
                         .GroupBy(s => new { s.Machine, Date = s.ShiftDate.Date })
                         .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Machine))
            {
                if (g.Key.Date < from || g.Key.Date > to) continue;
                if (holidays.Contains(g.Key.Date)) continue;
                var day = g.FirstOrDefault(s => s.Shift == new Shift(ShiftType.Day).Name);
                var night = g.FirstOrDefault(s => s.Shift == new Shift(ShiftType.Night).Name);
                // Закрывать можно только при обоих отчётах; без них — не кандидат.
                if (day is null || night is null) { noReports++; continue; }
                // Уже зелёные в календаре проверки — пропускаем.
                if (day.IsChecked && night.IsChecked) { closed++; continue; }
                // Решение человека уже есть — не наше дело.
                if (humanReviewed.Contains((g.Key.Machine, g.Key.Date))) { reviewed++; continue; }
                // Рано: сутки ещё не созрели (то же правило, что у AutoCloseGate).
                if (DateTime.Now < g.Key.Date.AddDays(1).AddHours(maturity)) { notMature++; continue; }
                Items.Add(new BatchItem
                {
                    DayReviewId = 0,
                    Date = g.Key.Date,
                    Machine = g.Key.Machine,
                    State = BatchItemState.Pending,
                    Summary = "отчёты есть — к проверке",
                });
                count++;
            }

            TotalCount = count;
            var scope = $"{from:dd.MM.yyyy}–{to:dd.MM.yyyy}";
            var rest = $"уже закрыто: {closed}, решение человека: {reviewed}, " +
                       $"не созрели: {notMature}, без обоих отчётов: {noReports}";
            StatusText = count == 0
                ? $"Нет кандидатов ({scope}): {rest}"
                : $"К автозакрытию: {count} ({scope}); {rest}";
            await Task.CompletedTask;
        }

        private static string BuildExistingSummary(DayReview r)
        {
            var verdict = r.AiRequiresReview == true ? "требует проверки"
                        : r.AiRequiresReview == false ? "ок"
                        : "—";
            var conf = r.AiConfidence.HasValue ? $"{r.AiConfidence.Value * 100:F0}%" : "—";
            return $"готово: {verdict} ({conf})";
        }

        private void OnRefreshExecuted(object _)
        {
            _ = RefreshListAsync();
        }

        private void OnCancelExecuted(object _)
        {
            _cts?.Cancel();
            StatusText = "Останавливаю...";
        }

        private async void OnStartExecuted(object _)
        {
            if (Items.Count == 0)
            {
                StatusText = "Нечего анализировать";
                return;
            }

            IsRunning = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var pending = OnlyPending
                ? Items.Where(i => i.State == BatchItemState.Pending).ToList()
                : Items.Where(i => i.State == BatchItemState.Pending || i.State == BatchItemState.Skipped).ToList();
            var total = pending.Count;
            var done = 0;
            ProcessedCount = 0;
            TotalCount = total;
            OverallProgress = 0;

            var executor = AutoCloseEmpty ? CreateAutoCloseExecutor() : null;
            var limitHit = false;

            try
            {
                foreach (var item in pending)
                {
                    if (ct.IsCancellationRequested)
                    {
                        item.State = BatchItemState.Cancelled;
                        item.Summary = "отменено";
                        break;
                    }

                    item.State = BatchItemState.Running;
                    item.Summary = AutoCloseEmpty ? "проверка..." : "анализ...";
                    item.ThinkingThoughts = "";
                    StatusText = AutoCloseEmpty
                        ? $"Закрытие {done + 1}/{total}: {item.Machine} {item.Date:dd.MM.yyyy}"
                        : $"Анализ {done + 1}/{total}: {item.Machine} {item.Date:dd.MM.yyyy}";

                    try
                    {
                        var parts = await Database.ReadPartsByShiftDateAndMachine(
                            item.Date, item.Date, item.Machine, ct);

                        if (executor != null)
                        {
                            var limitReached = await AutoCloseItemAsync(executor, item, parts, ct);
                            done++;
                            ProcessedCount = done;
                            OverallProgress = (double)done / total;
                            if (limitReached)
                            {
                                limitHit = true;
                                break;
                            }
                            continue;
                        }

                        var progress = ItemProgress(item);

                        var result = await _aiClient.AnalyzeAsync(
                            item.Machine, item.Date, parts,
                            thinkingProgress: progress,
                            ct: ct);

                        if (ct.IsCancellationRequested)
                        {
                            item.State = BatchItemState.Cancelled;
                            item.Summary = "отменено";
                            break;
                        }

                        if (result.HasError)
                        {
                            item.State = BatchItemState.Error;
                            item.Summary = $"ошибка: {result.Error}";
                            item.Explanation = result.Error;
                        }
                        else
                        {
                            // Живой вердикт суток неизменяем: у суток с вердиктом ИИ повторная
                            // оценка пишется отдельно (ai_day_reeval), а не поверх него (P3.4).
                            var saved = await Database.SaveAiAnalysisAsync(
                                item.DayReviewId, result, AppSettings.AiModel, ThinkingEnabled);

                            item.State = BatchItemState.Done;
                            item.Summary = BuildResultSummary(result) + (ThinkingEnabled ? " (агент)" : "")
                                + (!saved.IsOk ? $" — НЕ записано: {saved.Error}"
                                    : saved.Value == Database.AiAnalysisSaveKind.ReEval
                                        ? " — повторная оценка, живой вердикт не тронут" : "");
                            FillItemVerdict(item, result);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        item.State = BatchItemState.Cancelled;
                        item.Summary = "отменено";
                        break;
                    }
                    catch (Exception ex)
                    {
                        Util.WriteLog(ex, $"BatchAiAnalysis: {item.Machine} {item.Date:dd.MM.yyyy}");
                        item.State = BatchItemState.Error;
                        item.Summary = $"ошибка: {ex.Message}";
                        item.Explanation = ex.ToString();
                    }

                    done++;
                    ProcessedCount = done;
                    OverallProgress = (double)done / total;
                }

                StatusText = ct.IsCancellationRequested
                    ? $"Остановлено. Обработано {done}/{total}"
                    : limitHit
                        ? $"Предел автозакрытия за запуск ({AppSettings.Instance.AutoCloseMaxPerRun}) достигнут. Обработано {done}/{total}"
                        : $"Готово. Обработано {done}/{total}"
                          + (executor is { } closer ? $"; {(AppSettings.Instance.AutoCloseDryRun ? "в тени закрыто бы" : "закрыто")}: {closer.ClosedCount}" : "");
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private static string BuildResultSummary(AiAnalysisResult r)
        {
            var verdict = r.RequiresReview ? "требует проверки" : "ок";
            return $"{verdict} ({r.Confidence * 100:F0}%)";
        }

        /// <summary> Прогресс ИИ-анализа в строку пункта (мысли/очередь/инструменты). </summary>
        private static Progress<AiProgress> ItemProgress(BatchItem item)
        {
            var thinkingStarted = false;
            return new Progress<AiProgress>(update =>
            {
                if (update.Kind == AiProgressKind.Queue)
                {
                    item.ThinkingThoughts = update.Text;
                    thinkingStarted = false;
                }
                else if (update.Kind == AiProgressKind.Tool)
                {
                    item.ThinkingThoughts += (item.ThinkingThoughts.Length > 0 ? "\n" : "")
                        + "⚙ " + update.Text + "\n";
                    thinkingStarted = true;
                }
                else
                {
                    item.ThinkingThoughts = thinkingStarted
                        ? item.ThinkingThoughts + update.Text
                        : update.Text;
                    thinkingStarted = true;
                }
            });
        }

        private static void FillItemVerdict(BatchItem item, AiAnalysisResult result)
        {
            item.Explanation = result.Explanation;
            item.ShiftReportNote = result.ShiftReportIssues is { Length: > 0 } issues
                ? "Отчёт мастера: " + string.Join("; ", issues)
                : "";
            item.ShiftCausedEscalation = result.EscalatedByShiftReport;
            item.Confidence = result.Confidence;
            item.RequiresReview = result.RequiresReview;
        }

        private string? _serviceVersion;
        private DateTime _serviceVersionAt = DateTime.MinValue;

        /// <summary> Версия анализа службы из /health (кэшируется на минуту: сверка вердиктов не должна стоить запроса на каждые сутки). </summary>
        private async Task<string?> GetServiceAnalysisVersionAsync(CancellationToken ct)
        {
            if (DateTime.UtcNow - _serviceVersionAt < TimeSpan.FromMinutes(1)) return _serviceVersion;
            var health = await _aiClient.CheckHealthAsync(ct);
            _serviceVersion = health.Server ? health.AnalysisVersion : null;
            _serviceVersionAt = DateTime.UtcNow;
            return _serviceVersion;
        }

        private AutoCloseExecutor CreateAutoCloseExecutor()
        {
            var cfg = AppSettings.Instance;
            return new AutoCloseExecutor(
                new DbAutoCloseStore(),
                new AutoCloseOptions
                {
                    MinConfidence = cfg.AutoCloseMinConfidence,
                    MaturityHours = cfg.AutoCloseMaturityHours,
                    AllowedPromptVersions = cfg.AutoCloseAllowedPromptVersions,
                },
                new AutoCloseRunOptions
                {
                    Enabled = cfg.AutoCloseEnabled,
                    DryRun = cfg.AutoCloseDryRun,
                    MaxCloses = cfg.AutoCloseMaxPerRun,
                });
        }

        /// <summary>
        /// Автозакрытие одного суткостанка: ИИ-анализ суток (как «Призвать ИИ к ответу»),
        /// затем решение и запись в <see cref="AutoCloseExecutor"/> (допуск, автоисключения,
        /// ревью «ИИ-авто», зелёная клетка календаря). Сутки с записями и без.
        /// </summary>
        /// <returns> true — достигнут предел закрытий за запуск, цикл нужно остановить. </returns>
        private async Task<bool> AutoCloseItemAsync(
            AutoCloseExecutor executor, BatchItem item, IReadOnlyList<Part> parts, CancellationToken ct)
        {
            // Сохранённый вердикт (ai_day_verdicts) переживает перезапуск службы: если данные суток, версия
            // анализа службы, модель и режим не менялись, повторно ИИ не вызываем.
            var prepared = await _aiClient.PrepareAnalysisAsync(item.Machine, item.Date, parts, ct);
            var stored = await Database.GetAiDayVerdictAsync(item.Machine, item.Date);
            var serviceVersion = stored is null ? null : await GetServiceAnalysisVersionAsync(ct);
            var reused = AiVerdictReuse.CanReuse(stored, prepared.Fingerprint, serviceVersion, AppSettings.AiModel, ThinkingEnabled);

            AiAnalysisResult result;
            if (reused)
            {
                result = stored!.Result;
            }
            else
            {
                result = await _aiClient.AnalyzeAsync(prepared, ItemProgress(item), ct);

                if (ct.IsCancellationRequested)
                {
                    item.State = BatchItemState.Cancelled;
                    item.Summary = "отменено";
                    return false;
                }
                if (result.HasError)
                {
                    item.State = BatchItemState.Error;
                    item.Summary = $"ошибка: {result.Error}";
                    item.Explanation = result.Error;
                    return false;
                }

                // Вспомогательная запись: сбой (нет таблицы, БД не обновлена) закрытие не прерывает.
                await Database.SaveAiDayVerdictAsync(new AiDayVerdict
                {
                    Machine = item.Machine,
                    ShiftDate = item.Date,
                    AnalyzedBy = Environment.UserName,
                    Source = AiDayVerdict.SourceBatch,
                    InputFingerprint = prepared.Fingerprint,
                    AnalysisVersion = result.AnalysisVersion,
                    AiModelVersion = AppSettings.AiModel,
                    ThinkingEnabled = ThinkingEnabled,
                    Result = result,
                });
            }

            FillItemVerdict(item, result);
            var outcome = await executor.TryCloseAsync(
                item.Machine, item.Date, result, parts, AppSettings.AiModel, ThinkingEnabled, ct);
            var reasons = string.Join("; ", outcome.Reasons);
            var excluded = (outcome.ExclusionsApplied > 0 ? $", исключено строк: {outcome.ExclusionsApplied}" : "")
                + (reused ? " (вердикт из сохранённого, ИИ не вызывался)" : "");

            switch (outcome.Status)
            {
                case AutoCloseStatus.Closed:
                    item.State = BatchItemState.Done;
                    item.Summary = $"закрыт ИИ{excluded}";
                    break;
                case AutoCloseStatus.WouldClose:
                    item.State = BatchItemState.Done;
                    item.Summary = $"тень: был бы закрыт{excluded}";
                    break;
                case AutoCloseStatus.Deferred:
                    item.State = BatchItemState.Skipped;
                    item.Summary = $"отложено: {reasons}";
                    break;
                case AutoCloseStatus.Manual:
                    item.State = BatchItemState.Done;
                    item.Summary = $"{BuildResultSummary(result)} — вручную: {reasons}";
                    break;
                case AutoCloseStatus.Error:
                    item.State = BatchItemState.Error;
                    item.Summary = $"ошибка закрытия: {reasons}";
                    item.Explanation = reasons;
                    break;
                case AutoCloseStatus.Disabled:
                    item.State = BatchItemState.Skipped;
                    item.Summary = reasons;
                    break;
                case AutoCloseStatus.LimitReached:
                    item.State = BatchItemState.Skipped;
                    item.Summary = reasons;
                    return true;
            }
            return false;
        }
    }

    internal enum BatchItemState
    {
        Pending,
        Running,
        Done,
        Error,
        Skipped,
        Cancelled,
    }

    internal class BatchItem : ViewModel
    {
        public int DayReviewId { get; set; }

        public DateTime Date { get; set; }

        public string Machine { get; set; } = string.Empty;

        private BatchItemState _State;
        public BatchItemState State
        {
            get => _State;
            set
            {
                if (Set(ref _State, value))
                {
                    OnPropertyChanged(nameof(StateBrush));
                    OnPropertyChanged(nameof(Header));
                    if (value == BatchItemState.Running)
                    {
                        IsExpanded = true;
                    }
                }
            }
        }

        private bool _IsExpanded;
        public bool IsExpanded
        {
            get => _IsExpanded;
            set => Set(ref _IsExpanded, value);
        }

        private string _Summary = "ожидание";
        public string Summary
        {
            get => _Summary;
            set
            {
                if (Set(ref _Summary, value))
                {
                    OnPropertyChanged(nameof(Header));
                }
            }
        }

        private string _ThinkingThoughts = "";
        public string ThinkingThoughts
        {
            get => _ThinkingThoughts;
            set => Set(ref _ThinkingThoughts, value);
        }

        private string _Explanation = "";
        public string Explanation
        {
            get => _Explanation;
            set => Set(ref _Explanation, value);
        }

        private string _ShiftReportNote = "";
        /// <summary> Вопросы к суточному отчёту мастера — строкой под объяснением. </summary>
        public string ShiftReportNote
        {
            get => _ShiftReportNote;
            set
            {
                if (Set(ref _ShiftReportNote, value))
                    OnPropertyChanged(nameof(HasShiftReportNote));
            }
        }

        public bool HasShiftReportNote => !string.IsNullOrWhiteSpace(ShiftReportNote);

        private bool _ShiftCausedEscalation;
        /// <summary> Строка отчёта подсвечивается: эскалация вызвана суточным отчётом. </summary>
        public bool ShiftCausedEscalation
        {
            get => _ShiftCausedEscalation;
            set => Set(ref _ShiftCausedEscalation, value);
        }

        public double Confidence { get; set; }

        public bool RequiresReview { get; set; }

        public string Header => $"{Date:dd.MM.yyyy}  ·  {Machine}  ·  {Summary}";

        public System.Windows.Media.Brush StateBrush =>
            State switch
            {
                BatchItemState.Pending => Infrastructure.ThemeBrushes.Text3,
                BatchItemState.Running => Infrastructure.ThemeBrushes.Accent,
                BatchItemState.Done => Infrastructure.ThemeBrushes.Ok,
                BatchItemState.Error => Infrastructure.ThemeBrushes.Alert,
                BatchItemState.Skipped => Infrastructure.ThemeBrushes.Disabled,
                BatchItemState.Cancelled => Infrastructure.ThemeBrushes.Warn,
                _ => Infrastructure.ThemeBrushes.Text,
            };
    }
}
