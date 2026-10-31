using libeLog;
using libeLog.Base;
using libeLog.Extensions;
using libeLog.Infrastructure;
using libeLog.Models;
using remeLog.Core.Services;
using remeLog.Core.Services.Demo;
using remeLog.Infrastructure;
using remeLog.Infrastructure.Extensions;
using remeLog.Infrastructure.Types;
using remeLog.Infrastructure.Winnum;
using remeLog.Infrastructure.Winnum.Data;
using remeLog.Models;
using remeLog.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using libeLog.Views;
using static remeLog.Infrastructure.Winnum.Types;
using Database = remeLog.Infrastructure.Database;
using Part = remeLog.Models.Part;

namespace remeLog.ViewModels
{

    public class PartsInfoWindowViewModel : ViewModel, IDataErrorInfo
    {
        private static readonly SemaphoreSlim semaphoreSlim = new(1, 1);
        private CancellationTokenSource _cancellationTokenSource = new();
        private CancellationTokenSource _wncCancellationTokenSource = new();
        private CancellationTokenSource? _aiCancellationTokenSource;
        private Dictionary<string, MultiValueEditorWindow> _editors = new();
        private static readonly AiServiceClient _aiClient = new();
        private static bool lockUpdate;

        // Фоновая ИИ-проверка комментариев мастера (фича AiMasterCheck): дебаунс и
        // версия последнего редактирования на строку. Версия отбрасывает устаревшие
        // результаты, если строку переправили, пока запрос был в полёте.
        private readonly Dictionary<Part, CancellationTokenSource> _aiCheckDebounce = new();
        private readonly Dictionary<Part, int> _aiCheckVersion = new();
        // Клиентская сериализация проверок: сервер и так обрабатывает по одному
        // (семафор Ollama), но очередь на клиенте дешевле оборванных http-запросов.
        private static readonly SemaphoreSlim _aiCheckGate = new(1, 1);

        public PartsInfoWindowViewModel(CombinedParts parts)
        {
            lockUpdate = true;
            if (Util.HasFeature(RemeLogFeature.Ai))
            {
                AiHealthMonitor.Instance.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AiHealthMonitor.IsServerAvailable))
                        OnPropertyChanged(nameof(IsServerAvailable));
                    if (e.PropertyName == nameof(AiHealthMonitor.IsOllamaAvailable))
                        OnPropertyChanged(nameof(IsOllamaAvailable));
                    if (e.PropertyName == nameof(AiHealthMonitor.IsAiAvailable))
                        OnPropertyChanged(nameof(IsAiAvailable));
                    if (e.PropertyName == nameof(AiHealthMonitor.HealthError))
                        OnPropertyChanged(nameof(HealthError));
                    if (e.PropertyName == nameof(AiHealthMonitor.HealthTooltip))
                        OnPropertyChanged(nameof(HealthTooltip));
                    // Кнопка «Анализ дня» зависит от доступности ИИ — пересчитать CanExecute.
                    CommandManager.InvalidateRequerySuggested();
                };
            }
            ChangeCalcFixedCommand = new LambdaCommand(OnChangeCalcFixedCommandExecuted, CanChangeCalcFixedCommandExecute);
            ChangeCompactViewCommand = new LambdaCommand(OnChangeCompactViewCommandExecuted, CanChangeCompactViewCommandExecute);
            ChangeRoleCommand = new LambdaCommand(OnChangeRoleCommandExecuted, CanChangeRoleCommandExecute);
            SelectColumnProfileCommand = new LambdaCommand(OnSelectColumnProfileCommandExecuted, CanSelectColumnProfileCommandExecute);
            ManageColumnProfilesCommand = new LambdaCommand(OnManageColumnProfilesCommandExecuted, CanManageColumnProfilesCommandExecute);
            ChangeShowUncheckedCommand = new LambdaCommand(OnChangeShowUncheckedCommandExecuted, CanChangeShowUncheckedCommandExecute);
            CheckAssignmentWithFactCommand = new LambdaCommand(OnCheckAssignmentWithFactCommandExecuted, CanCheckAssignmentWithFactCommandExecute);
            ClearContentCommand = new LambdaCommand(OnClearContentCommandExecuted, CanClearContentCommandExecute);
            DecreaseDateCommand = new LambdaCommand(OnDecreaseDateCommandExecuted, CanDecreaseDateCommandExecute);
            DecreaseSetupCommand = new LambdaCommand(OnDecreaseSetupCommandExecuted, CanDecreaseSetupCommandExecute);
            DeleteFilterCommand = new LambdaCommand(OnDeleteFilterCommandExecuted, CanDeleteFilterCommandExecute);
            DeletePartCommand = new LambdaCommand(OnDeletePartCommandExecuted, CanDeletePartCommandExecute);
            ChangeSerialPartNormativesCommand = new LambdaCommand(OnChangeSerialPartNormativesCommandExecuted, CanChangeSerialPartNormativesCommandExecute);
            ExportHistoryToExcelCommand = new LambdaCommand(OnExportHistoryToExcelCommandExecuted, CanExportHistoryToExcelCommandExecute);
            ExportLongSetupsCommand = new LambdaCommand(OnExportLongSetupsCommandExecuted, CanExportLongSetupsCommandExecute);
            ExportPartsReportToExcelCommand = new LambdaCommand(OnExportPartsReportToExcelCommandExecuted, CanExportPartsReportToExcelCommandExecute);
            ExportReportForPeriodToExcelCommand = new LambdaCommand(OnExportReportForPeriodToExcelCommandExecuted, CanExportReportForPeriodToExcelCommandExecute);
            ExportNewReportForPeriodToExcelCommand = new LambdaCommand(OnExportNewReportForPeriodToExcelCommandExecuted, CanExportNewReportForPeriodToExcelCommandExecute);
            ExportPeriodsComparisonCommand = new LambdaCommand(OnExportPeriodsComparisonCommandExecuted, CanExportPeriodsComparisonCommandExecute);
            ExportShiftsInfoReportCommand = new LambdaCommand(OnExportShiftsInfoReportCommandExecuted, CanExportShiftsInfoReportCommandExecute);
            ExportToExcelCommand = new LambdaCommand(OnExportToExcelCommandExecuted, CanExportToExcelCommandExecute);
            ExportToolSearchCasesToExcelCommand = new LambdaCommand(OnExportToolSearchCasesToExcelCommandExecuted, CanExportToolSearchCasesToExcelCommandExecute);
            ExportValidationErrorsCommand = new LambdaCommand(OnExportValidationErrorsCommandExecuted, CanExportValidationErrorsCommandExecute);
            ExportIncorrectFillsCommand = new LambdaCommand(OnExportIncorrectFillsCommandExecuted, CanExportIncorrectFillsCommandExecute);
            ExportVerevkinReportCommand = new LambdaCommand(OnExportVerevkinReportCommandExecuted, CanExportVerevkinReportCommandExecute);
            HideAllMachinesCommand = new LambdaCommand(OnHideAllMachinesCommandExecuted, CanHideAllMachinesCommandExecute);
            IncreaseDateCommand = new LambdaCommand(OnIncreaseDateCommandExecuted, CanIncreaseDateCommandExecute);
            IncreaseSetupCommand = new LambdaCommand(OnIncreaseSetupCommandExecuted, CanIncreaseSetupCommandExecute);
            InvertMachinesCommand = new LambdaCommand(OnInvertMachinesCommandExecuted, CanInvertMachinesCommandExecute);
            NormsAndWorkloadAnalysisCommand = new LambdaCommand(OnNormsAndWorkloadAnalysisCommandExecuted, CanNormsAndWorkloadAnalysisCommandExecute);
            OpenDailyReportWindowCommand = new LambdaCommand(OnOpenDailyReportWindowCommandExecuted, CanOpenDailyReportWindowCommandExecute);
            FillProcessComplianceAuditCommand = new LambdaCommand(OnFillProcessComplianceAuditCommandExecuted, CanFillProcessComplianceAuditCommandExecute);
            OperatorReportToExcelCommand = new LambdaCommand(OnOperatorReportToExcelCommandExecuted, CanOperatorReportToExcelCommandExecute);
            OperatorsShiftsReportToExcelCommand = new LambdaCommand(OnOperatorsShiftsReportToExcelCommandExecuted, CanOperatorsShiftsReportToExcelCommandExecute);
            RefreshPartsCommand = new LambdaCommand(OnRefreshPartsCommandExecuted, CanRefreshPartsCommandExecute);
            SearchInWindchillCommand = new LambdaCommand(OnSearchInWindchillCommandExecuted, CanSearchInWindchillCommandExecute);
            SearchInWinnumCommand = new LambdaCommand(OnSearchInWinnumCommandExecuted, CanSearchInWinnumCommandExecute);
            SetAllDateCommand = new LambdaCommand(OnSetAllDateCommandExecuted, CanSetAllDateCommandExecute);
            SetHorMillMachinesCommand = new LambdaCommand(OnSetHorMillMachinesCommandExecuted, CanSetHorMillMachinesCommandExecute);
            SetLatheMachinesCommand = new LambdaCommand(OnSetLatheMachinesCommandExecuted, CanSetLatheMachinesCommandExecute);
            SetMillMachinesCommand = new LambdaCommand(OnSetMillMachinesCommandExecuted, CanSetMillMachinesCommandExecute);
            SetMonthDateCommand = new LambdaCommand(OnSetMonthDateCommandExecuted, CanSetMonthDateCommandExecute);
            SetWeekDateCommand = new LambdaCommand(OnSetWeekDateCommandExecuted, CanSetWeekDateCommandExecute);
            SetVerMillMachinesCommand = new LambdaCommand(OnSetVerMillMachinesCommandExecuted, CanSetVerMillMachinesCommandExecute);
            SetYearDateCommand = new LambdaCommand(OnSetYearDateCommandExecuted, CanSetYearDateCommandExecute);
            SetYesterdayDateCommand = new LambdaCommand(OnSetYesterdayDateCommandExecuted, CanSetYesterdayDateCommandExecute);
            SetSpecificDateCommand = new LambdaCommand(OnSetSpecificDateCommandExecuted, CanSetSpecificDateCommandExecute);
            SetSpecificMonthCommand = new LambdaCommand(OnSetSpecificMonthCommandExecuted, CanSetSpecificMonthCommandExecute);
            SetSpecificYearCommand = new LambdaCommand(OnSetSpecificYearCommandExecuted, CanSetSpecificYearCommandExecute);
            ShowAllMachinesCommand = new LambdaCommand(OnShowAllMachinesCommandExecuted, CanShowAllMachinesCommandExecute);
            ShowArchiveTableCommand = new LambdaCommand(OnShowArchiveTableCommandExecuted, CanShowArchiveTableCommandExecute);
            ShowInfoCommand = new LambdaCommand(OnShowInfoCommandExecuted, CanShowInfoCommandExecute);
            UnlockSerialPartNormativesCommand = new LambdaCommand(OnUnlockSerialPartNormativesCommandExecuted, CanUnlockSerialPartNormativesCommandExecute);
            OpenMultiValueEditorCommand = new LambdaCommand(OnOpenMultiValueEditorCommandExecuted, CanOpenMultiValueEditorCommandExecute);
            UpdatePartsCommand = new LambdaCommand(OnUpdatePartsCommandExecutedAsync, CanUpdatePartsCommandExecute);
            RemoveChipFilterCommand = new LambdaCommand(OnRemoveChipFilterExecuted);
            EditChipFilterCommand = new LambdaCommand(p => EditChipFilter((FilterChip)p!));
            ClearChipFiltersCommand = new LambdaCommand(_ => ClearChipFilters());
            MarkDayOkCommand = new LambdaCommand(OnMarkDayOkExecuted, CanMarkDayOkExecute);
            MarkDayEscalatedCommand = new LambdaCommand(OnMarkDayEscalatedExecuted, CanMarkDayEscalatedExecute);
            TogglePartFlagCommand = new LambdaCommand(OnTogglePartFlagExecuted);
            AnalyzeDayCommand = new LambdaCommand(OnAnalyzeDayExecuted, CanAnalyzeDayExecute);
            SnapPartsCommand = new LambdaCommand(OnSnapPartsExecuted, CanSnapPartsExecute);
            ShowAiExplanationCommand = new LambdaCommand(OnShowAiExplanationExecuted, CanShowAiExplanationExecute);
            OpenCommentEditorCommand = new LambdaCommand(OnOpenCommentEditorExecuted);
            CloseCommentEditorCommand = new LambdaCommand(OnCloseCommentEditorExecuted);
            CancelCommentEditorCommand = new LambdaCommand(OnCancelCommentEditorExecuted);
            CloseAiExplanationPopupCommand = new LambdaCommand(_ => IsAiExplanationPopupOpen = false);

            CalcFixed = Part.CalcFixed;
            PartsInfo = parts;
            ShiftFilterItems = new Shift[3] { new(ShiftType.All), new(ShiftType.Day), new(ShiftType.Night) };
            _ShiftFilter = ShiftFilterItems.FirstOrDefault();
            SerialPartsFilterItems = new PartsFilter[3]
                { new(PartsFilterType.All), new(PartsFilterType.Serial), new(PartsFilterType.NonSerial) };
            _SerialPartsFilter = SerialPartsFilterItems.First();
            _OperatorFilter = "";
            _FinishedCountFilter = "";
            _Parts = new();
            _OrderFilter = "";
            _PartNameFilter = "";
            _EngineerConclusionFilter = "";
            _EngineerCommentFilter = "";
            _TotalCountFilter = "";
            _FromDate = PartsInfo.FromDate;
            _ToDate = PartsInfo.ToDate;
            _MachineFilters = new();
            _MachineFilters.CollectionChanged += MachineFiltersSource_CollectionChanged!;
            _SerialParts = new();
            _Parts.CollectionChanged += _Parts_CollectionChanged;
            _ViewMode = AppSettings.Instance.User ??= User.Viewer;
            foreach (Part part in _Parts)
            {
                PropertyChanged += Part_PropertyChanged!;
            }
            ChipFilters = new ObservableCollection<FilterChip>();
            ChipFilters.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChipFilters));
            // Режим из настроек (вкл — агент, выкл — thinking), не форсируем.
            AiThinkingEnabled = AppSettings.Instance.AiThinkingEnabled;
            OnPropertyChanged(nameof(HasErrors));
            UpdateHasErrors();
            _ = Init();
        }

        private ObservableCollection<FilterChip> _ChipFilters = new();
        /// <summary>Активные чип-фильтры, добавленные через контекстное меню.</summary>
        public ObservableCollection<FilterChip> ChipFilters
        {
            get => _ChipFilters;
            private set => Set(ref _ChipFilters, value);
        }

        /// <summary>True если есть хотя бы один чип-фильтр — управляет видимостью панели.</summary>
        public bool HasChipFilters => ChipFilters.Count > 0;


        public List<int> AvailableYears =>
            Enumerable.Range(2023, DateTime.Now.Year - 2023 + 1).ToList();

        private void _Parts_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (Part item in e.NewItems)
                    item.PropertyChanged += Part_PropertyChanged!;

            if (e.OldItems != null)
                foreach (Part item in e.OldItems)
                {
                    item.PropertyChanged -= Part_PropertyChanged!;
                    if (_aiCheckDebounce.TryGetValue(item, out var cts))
                    {
                        cts.Cancel();
                        cts.Dispose();
                        _aiCheckDebounce.Remove(item);
                    }
                    _aiCheckVersion.Remove(item);
                }
        }

        async Task Init()
        {
            var (_, machineFilters, _) = await Database.ReadMachinesAsync();
            _MachineFilters = machineFilters.ToObservableCollection();
            _MachineFilters.CollectionChanged += MachineFiltersSource_CollectionChanged!;
            foreach (var machineFilter in MachineFilters)
            {
                machineFilter.PropertyChanged += MachineFilters_PropertyChanged!;
                machineFilter.Filter = machineFilter.Machine == PartsInfo.Machine || PartsInfo.Machine == "Все станки";
            }
            OnPropertyChanged(nameof(MachineFilters));
            OnPropertyChanged(nameof(MachineFilterSummary));
            // Демо: серийные детали из генератора (SQL Server недоступен).
            SerialParts = Core.DomainSettings.DemoMode
                ? DemoStore.GetSerialParts()
                    .Select((p, i) => new SerialPart { Id = p.Id, PartName = p.PartName, YearCount = 120 })
                    .ToList()
                : await libeLog.Infrastructure.Database.GetSerialPartsAsync(AppSettings.Instance.ConnectionString!);

            lockUpdate = false;

            await LoadPartsAsync();
        }

        private void Part_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            UpdateHasErrors();
            if (sender is Part part && IsAiCheckTrigger(e.PropertyName))
                ScheduleAiCheck(part);
        }

        // Кросс-рейзы этих имён от сеттеров времён/нормативов тоже триггерят проверку —
        // это желаемо (изменение времени меняет КПД и состав аномалий); per-keystroke
        // события от TextBox (UpdateSourceTrigger=PropertyChanged) гасит дебаунс.
        private static bool IsAiCheckTrigger(string? propertyName) =>
            propertyName == nameof(Part.MasterSetupComment)
            || propertyName == nameof(Part.MasterMachiningComment)
            || propertyName == nameof(Part.MasterComment)
            || propertyName == nameof(Part.MasterSetupDetail)
            || propertyName == nameof(Part.MasterMachiningDetail)
            || propertyName == nameof(Part.SpecifiedDowntimesComment)
            // Переопределение аналитиком меняет эффективную причину, а значит и состав
            // аномалий, уходящих на проверку, — перезапуск нужен так же, как от правки мастера.
            || propertyName == nameof(Part.SetupReasonOverride)
            || propertyName == nameof(Part.MachiningReasonOverride)
            || propertyName == nameof(Part.SetupReasonOverrideComment)
            || propertyName == nameof(Part.MachiningReasonOverrideComment);

        /// <summary>
        /// Планирует фоновую ИИ-проверку строки с дебаунсом 2.5с. Совещательный контур:
        /// ничего не блокирует, статус живёт только в памяти (Part.AiCheckStatus).
        /// </summary>
        private void ScheduleAiCheck(Part part)
        {
            // Демо: AiService недоступен вне рабочей среды — проверки не планируем.
            if (Core.DomainSettings.DemoMode) return;
            // ИИ недоступен — не шлём заведомо дохлые запросы (иначе все строки
            // получат статус ошибки); проверка перезапустится при следующей правке.
            if (!IsAiAvailable) return;
            if (!HasFeatureAiMasterCheck || lockUpdate || InProgress) return;

            int version = (_aiCheckVersion.TryGetValue(part, out var v) ? v : 0) + 1;
            _aiCheckVersion[part] = version;

            if (_aiCheckDebounce.TryGetValue(part, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            var cts = new CancellationTokenSource();
            _aiCheckDebounce[part] = cts;

            if (!part.IsReadyForAiCheck)
            {
                // Аномалия ушла или требуемый комментарий опустел — детерминированная
                // валидация берёт своё, ИИ-статус сбрасываем.
                part.AiCheckStatus = AiCheckStatus.None;
                part.AiCheckRemark = string.Empty;
                return;
            }

            part.AiCheckStatus = AiCheckStatus.Pending;
            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2500, token);
                    await RunAiCheckAsync(part, version, token);
                }
                catch (OperationCanceledException) { /* строку переправили или окно закрыто */ }
            }, token);
        }

        /// <summary>
        /// Запускает фоновую ИИ-проверку сразу для всех подходящих строк после
        /// загрузки — не дожидаясь правки мастером. Только когда открыт РОВНО
        /// один станок за РОВНО одни сутки: при широком диапазоне дат массовый
        /// запуск проверок по всем строкам забил бы очередь Ollama (она общая с
        /// дневным ИИ-анализом и обслуживает запросы строго по одному). Переиспользует
        /// ScheduleAiCheck — та же защита (дебаунс, версии, серилизация через gate),
        /// разницы для не-подходящих строк не будет (IsReadyForAiCheck внутри).
        /// </summary>
        private void ScheduleInitialAiChecks()
        {
            if (!HasFeatureAiMasterCheck || !IsSingleMachineSingleDay) return;

            foreach (var part in Parts)
                ScheduleAiCheck(part);
        }

        private async Task RunAiCheckAsync(Part part, int version, CancellationToken token)
        {
            await _aiCheckGate.WaitAsync(token);
            try
            {
                if (!_aiCheckVersion.TryGetValue(part, out var current) || current != version) return;
                if (!part.IsReadyForAiCheck) return;

                var machine = part.Machine;
                var shiftDate = part.ShiftDate;
                var result = await _aiClient.VerifyPartAsync(machine, shiftDate, part, token);

                if (!_aiCheckVersion.TryGetValue(part, out current) || current != version) return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (result.HasError)
                    {
                        part.AiCheckStatus = AiCheckStatus.Error;
                        part.AiCheckRemark = $"ИИ-проверка недоступна: {result.Error}";
                    }
                    else if (result.Ok)
                    {
                        part.AiCheckStatus = AiCheckStatus.Ok;
                        part.AiCheckRemark = string.Empty;
                    }
                    else
                    {
                        part.AiCheckStatus = AiCheckStatus.Remark;
                        part.AiCheckRemark = result.Remark;
                    }
                });
            }
            finally
            {
                _aiCheckGate.Release();
            }
        }

        /// <summary>
        /// Отменяет все запланированные/выполняющиеся ИИ-проверки и чистит словари
        /// (иначе утекают ссылки на старые Part после перезагрузки коллекции).
        /// Статусы строк не трогаем — коллекция либо пересоздаётся, либо окно закрывается.
        /// </summary>
        public void CancelAllAiChecks()
        {
            foreach (var cts in _aiCheckDebounce.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _aiCheckDebounce.Clear();
            _aiCheckVersion.Clear();
        }

        private void MachineFiltersSource_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (MachineFilter item in e.NewItems)
                    item.PropertyChanged += MachineFilters_PropertyChanged!;

            if (e.OldItems != null)
                foreach (MachineFilter item in e.OldItems)
                    item.PropertyChanged -= MachineFilters_PropertyChanged!;
        }

        private async void MachineFilters_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(MachineVisibility));
            OnPropertyChanged(nameof(MachineFilterSummary));
            if (e.PropertyName == "Filter")
                await LoadPartsAsync();
        }


        private bool _HasErrors;
        /// <summary> Есть ли ошибки валидации </summary>
        public bool HasErrors
        {
            get => _HasErrors;
            set
            {
                if (Set(ref _HasErrors, value))
                {
                    OnPropertyChanged(nameof(HasErrors));
                    OnPropertyChanged(nameof(CanOpenDailyReportButton));
                }
            }
        }

        private DayReview? _CurrentDayReview;
        /// <summary> Текущее решение аналитика по открытым суткам (null = ещё не проверено). </summary>
        public DayReview? CurrentDayReview
        {
            get => _CurrentDayReview;
            private set
            {
                if (Set(ref _CurrentDayReview, value))
                {
                    OnPropertyChanged(nameof(DayReviewStatusText));
                    OnPropertyChanged(nameof(DayReviewStatusToolTip));
                    OnPropertyChanged(nameof(DayReviewStatusColor));
                    OnPropertyChanged(nameof(IsDayReviewed));
                    OnPropertyChanged(nameof(AiVerdictText));
                    OnPropertyChanged(nameof(AiVerdictColor));
                    OnPropertyChanged(nameof(HasAiVerdict));
                    OnPropertyChanged(nameof(DozorTooltip));
                }
            }
        }

        private string _DayReviewComment = string.Empty;
        /// <summary> Комментарий аналитика к суткам — вводится перед нажатием кнопки. </summary>
        public string DayReviewComment
        {
            get => _DayReviewComment;
            set => Set(ref _DayReviewComment, value);
        }

        private string _AiFeedbackText = string.Empty;
        /// <summary> Коррекция аналитика на результат ИИ-анализа (AiFeedback). </summary>
        public string AiFeedbackText
        {
            get => _AiFeedbackText;
            set => Set(ref _AiFeedbackText, value);
        }
        private string _aiFeedbackBackup = string.Empty;

        /// <summary> Авто-вердикт совпадения ИИ и аналитика из БД (computed). </summary>
        public string? AiVerdictText => CurrentDayReview?.AiVerdict;

        /// <summary> Цвет бейджа AiVerdict. </summary>
        public string AiVerdictColor => (CurrentDayReview?.AiVerdict) switch
        {
            "совпадение"            => "#2E7D32",  // зелёный
            "ИИ пропустил"          => "#C62828",  // красный
            "ИИ лишний флаг"        => "#F57F17",  // оранжевый
            "не анализировалось"    => "#757575",  // серый
            _                       => "Transparent",
        };

        /// <summary> Есть ли вердикт (для видимости бейджа). </summary>
        public bool HasAiVerdict => !string.IsNullOrWhiteSpace(CurrentDayReview?.AiVerdict)
                                    && CurrentDayReview!.AiVerdict != "не анализировалось";

        private string _dayReviewCommentBackup = string.Empty;
        private bool _IsCommentEditorOpen = false;
        /// <summary> Открыт ли всплывающий редактор комментария. </summary>
        public bool IsCommentEditorOpen
        {
            get => _IsCommentEditorOpen;
            set => Set(ref _IsCommentEditorOpen, value);
        }

        public ICommand OpenCommentEditorCommand { get; private set; } = null!;
        public ICommand CloseCommentEditorCommand { get; private set; } = null!;
        public ICommand CancelCommentEditorCommand { get; private set; } = null!;

        private bool _IsAiExplanationPopupOpen;
        /// <summary> Открыт ли popup с результатом ИИ-анализа. </summary>
        public bool IsAiExplanationPopupOpen
        {
            get => _IsAiExplanationPopupOpen;
            set => Set(ref _IsAiExplanationPopupOpen, value);
        }

        public ICommand CloseAiExplanationPopupCommand { get; private set; } = null!;

        private void OnOpenCommentEditorExecuted(object _)
        {
            _dayReviewCommentBackup = DayReviewComment;
            IsCommentEditorOpen = true;
        }

        private void OnCloseCommentEditorExecuted(object _)
        {
            IsCommentEditorOpen = false;
        }

        private void OnCancelCommentEditorExecuted(object _)
        {
            DayReviewComment = _dayReviewCommentBackup;
            IsCommentEditorOpen = false;
        }

        private bool _AiThinkingEnabled = false;
        /// <summary> Режим ИИ-анализа: вкл — агентский контур, выкл — обычный thinking-режим. </summary>
        public bool AiThinkingEnabled
        {
            get => _AiThinkingEnabled;
            set 
            {
                if (Set(ref _AiThinkingEnabled, value))
                {
                    AppSettings.Instance.AiThinkingEnabled = value;
                    AppSettings.Save();
                    OnPropertyChanged(nameof(DozorTooltip));
                }    
            }
        }

        public bool HasUnsavedParts => Parts.Any(p => p.NeedUpdate);

        /// <summary> Количество строк, помеченных как проблемные. </summary>
        public int FlaggedPartsCount => Parts.Count(p => p.IsFlagged);

        /// <summary> Есть ли проблемные строки. </summary>
        public bool HasFlaggedParts => Parts.Any(p => p.IsFlagged);

        private AiAnalysisResult? _AiResult;
        public AiAnalysisResult? AiResult
        {
            get => _AiResult;
            private set
            {
                if (Set(ref _AiResult, value))
                {
                    OnPropertyChanged(nameof(HasAiResult));
                    OnPropertyChanged(nameof(AiResultText));
                    OnPropertyChanged(nameof(AiResultColor));
                    OnPropertyChanged(nameof(AiResultFormatted));
                    OnPropertyChanged(nameof(DozorTooltip));
                }
            }
        }

        public bool HasAiResult => AiResult != null;

        public string AiResultText
        {
            get
            {
                if (AiResult == null) return "";
                if (AiResult.HasError) return $"Ошибка: {AiResult.Error}";
                var verdict = AiResult.RequiresReview ? "Требует проверки" : "Всё в порядке";
                return $"{verdict}  ({AiResult.Confidence:0%})";
            }
        }

        public string AiResultColor => AiResult switch
        {
            null => "#757575",
            { HasError: true } => "#B71C1C",
            { RequiresReview: true } => "#F57F17",
            _ => "#2E7D32",
        };

        /// <summary> Тултип глаза-Дозора: модель, промпт, режим, время анализа. </summary>
        public string DozorTooltip
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine("Дозор — состояние ИИ");
                sb.AppendLine($"Модель: {AppSettings.AiModel}");
                var prompt = AiResult?.PromptVersion ?? CurrentDayReview?.AiPromptVersion;
                sb.AppendLine($"Промпт: {(string.IsNullOrEmpty(prompt) ? "—" : prompt)}");
                sb.Append($"Режим: {(AiThinkingEnabled ? "агент" : "thinking")}");
                if (CurrentDayReview?.AiAnalyzedAt != null)
                    sb.Append($", анализ: {CurrentDayReview.AiAnalyzedAt:dd.MM HH:mm}");
                return sb.ToString();
            }
        }

        public string AiResultFormatted
        {
            get
            {
                if (AiResult == null) return "";
                if (AiResult.HasError) return $"Ошибка: {AiResult.Error}";

                var sb = new StringBuilder();
                var verdict = AiResult.RequiresReview ? "Требует проверки" : "Всё в порядке";
                sb.AppendLine($"Оценка ИИ: {verdict}");
                sb.AppendLine($"Уверенность: {AiResult.Confidence:P0}");
                //if (!string.IsNullOrEmpty(AiResult.PromptVersion))
                //    sb.AppendLine($"Версия промпта: {AiResult.PromptVersion}");
                //if (CurrentDayReview?.AiModelVersion != null)
                //    sb.AppendLine($"Версия модели: {CurrentDayReview.AiModelVersion}");
                if (CurrentDayReview?.AiAnalyzedAt != null)
                    sb.AppendLine($"Проанализировано: {CurrentDayReview.AiAnalyzedAt:dd.MM.yyyy HH:mm:ss}");

                if (!string.IsNullOrEmpty(AiResult.Explanation))
                    sb.AppendLine($"{Environment.NewLine}Объяснение:{Environment.NewLine}{AiResult.Explanation}");

                if (AiResult.Signals.Length > 0)
                {
                    sb.AppendLine($"{Environment.NewLine}Основные признаки:");
                    foreach (var s in AiResult.Signals)
                        sb.AppendLine($"  • {s}");
                }

                if (AiResult.SuggestExcludeFromReports.Length > 0)
                {
                    sb.AppendLine($"{Environment.NewLine}Предложено исключить из отчётов:");
                    foreach (var e in AiResult.SuggestExcludeFromReports)
                    {
                        var p = e.Split('§');
                        sb.AppendLine(p.Length > 3
                            ? $"  • {p[0]} | М/Л: {p[2]} | Уст.{p[1]} — {string.Join("§", p[3..])}"
                            : $"  • {e}");
                    }
                }

                if (!AiResult.RequiresReview && AiResult.AutoCloseBlockers.Length > 0)
                {
                    sb.AppendLine($"{Environment.NewLine}Не закрыто автоматически:");
                    foreach (var b in AiResult.AutoCloseBlockers)
                        sb.AppendLine($"  • {b}");
                }

                return sb.ToString().TrimEnd();
            }
        }


        /// <summary>
        /// true — открыт ровно один станок за ровно одни сутки.
        /// Только в этом случае показываем панель первичной проверки.
        /// </summary>
        public bool IsSingleMachineSingleDay =>
            FromDate.Date == ToDate.Date
            && PartsInfo.Machine != "Все станки"
            && MachineFilters.Count(f => f.Filter) == 1;

        public bool CanUserReview => IsSingleMachineSingleDay && Util.HasFeature(RemeLogFeature.Ai);

        /// <summary> true — решение по суткам уже зафиксировано. </summary>
        public bool IsDayReviewed => CurrentDayReview != null;

        /// <summary> Текст статуса для отображения в UI. </summary>
        public string DayReviewStatusText => CurrentDayReview == null
                ? "Не проверено"
                : $"{CurrentDayReview.Decision.ToDisplayString()}  ·  {CurrentDayReview.ReviewedBy}  ·  {CurrentDayReview.ReviewedAt:dd.MM.yy HH:mm}";

        private List<AutoExcludeLogEntry> _AutoExcludeLog = new();

        /// <summary>
        /// Подсказка к статусу суток, закрытых ИИ без человека: когда/чем закрыто и какие
        /// исключения из К1 ИИ применил сам. null — сутки закрыты человеком или не закрыты.
        /// </summary>
        public string? DayReviewStatusToolTip
        {
            get
            {
                if (CurrentDayReview is not { IsAutoClosed: true } review) return null;

                var sb = new StringBuilder();
                sb.Append("Закрыто ИИ без участия человека");
                if (review.AiAnalyzedAt != null) sb.Append($" ({review.AiAnalyzedAt:dd.MM.yy HH:mm}");
                if (!string.IsNullOrEmpty(review.AiPromptVersion))
                    sb.Append(review.AiAnalyzedAt != null ? $", {review.AiPromptVersion})" : $" ({review.AiPromptVersion})");
                else if (review.AiAnalyzedAt != null) sb.Append(')');
                sb.AppendLine(".");
                sb.Append("Не согласны — меняйте решение как обычно (день откроется двойным кликом в календаре).");
                if (_AutoExcludeLog.Count > 0)
                {
                    sb.AppendLine().AppendLine().Append("Исключения из К1, применённые ИИ:");
                    foreach (var e in _AutoExcludeLog)
                    {
                        sb.AppendLine().Append($"  • {e.PartName} | М/Л: {e.Order} | Уст.{e.Setup} — {e.Reason}");
                        if (e.IsReverted) sb.Append($" [отменено {e.RevertedBy} {e.RevertedAt:dd.MM HH:mm}]");
                    }
                    sb.AppendLine().Append("Снять исключение — колонка «Исключить».");
                }
                return sb.ToString();
            }
        }

        /// <summary> Цвет статуса: зелёный / жёлтый / серый. </summary>
        public string DayReviewStatusColor => CurrentDayReview?.Decision switch
        {
            AnalystDecision.Ok => "#2E7D32",  // зелёный
            AnalystDecision.Escalated => "#F57F17",  // жёлтый
            _ => "#757575",  // серый
        };



        public Shift[] ShiftFilterItems { get; set; }

        public PartsFilter[] SerialPartsFilterItems { get; set; }

        private ObservableCollection<Part> _Parts;
        /// <summary> Описание </summary>
        public ObservableCollection<Part> Parts
        {
            get => _Parts;
            set
            {
                if (_Parts != null)
                {
                    _Parts.CollectionChanged -= _Parts_CollectionChanged!;
                    foreach (var part in _Parts) part.PropertyChanged -= Part_PropertyChanged!;
                }

                Set(ref _Parts, value);

                if (_Parts != null)
                {
                    _Parts.CollectionChanged += _Parts_CollectionChanged!;
                    foreach (var part in _Parts) part.PropertyChanged += Part_PropertyChanged!;
                }

                OnPropertyChanged(nameof(SetupTimeRatio));
                OnPropertyChanged(nameof(AverageSetupRatio));
                OnPropertyChanged(nameof(ProductionTimeRatio));
                OnPropertyChanged(nameof(AverageProductionRatio));
                OnPropertyChanged(nameof(SpecifiedDowntimesRatio));
                OnPropertyChanged(nameof(UnspecifiedDowntimesRatio));
                UpdateHasErrors();
            }
        }


        private DateTime _FromDate;
        /// <summary> Начальная дата </summary>
        public DateTime FromDate
        {
            get => _FromDate;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _FromDate, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }

        private DateTime _ToDate;
        /// <summary> Конечная дата </summary>
        public DateTime ToDate
        {
            get => _ToDate;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _ToDate, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }
        #region Фильтры

        private Shift _ShiftFilter;
        /// <summary> Фильтр смены </summary>
        public Shift ShiftFilter
        {
            get => _ShiftFilter;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _ShiftFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }


        private string _OperatorFilter;
        /// <summary> Фильтр оператора </summary>
        public string OperatorFilter
        {
            get => _OperatorFilter;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _OperatorFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }

        private string _PartNameFilter;
        /// <summary> Фильтр названия детали </summary>
        public string PartNameFilter
        {
            get => _PartNameFilter;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _PartNameFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }

        private string _OrderFilter;
        /// <summary> Фильтр названия детали </summary>
        public string OrderFilter
        {
            get => _OrderFilter;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _OrderFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }


        private int? _SetupFilter;
        /// <summary> Описание </summary>
        public int? SetupFilter
        {
            get => _SetupFilter;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _SetupFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }


        private string _FinishedCountFilter;
        /// <summary> фильтр по завершенным деталям </summary>
        public string FinishedCountFilter
        {
            get => _FinishedCountFilter;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _FinishedCountFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }


        private string _TotalCountFilter;
        /// <summary> фильтр по количеству деталей в маршрутном листе </summary>
        public string TotalCountFilter
        {
            get => _TotalCountFilter;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _TotalCountFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }


        private string _EngineerConclusionFilter;
        /// <summary> Фильтр по заключению техотдела </summary>
        public string EngineerConclusionFilter
        {
            get => _EngineerConclusionFilter;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _EngineerConclusionFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }

        private string _EngineerCommentFilter;
        /// <summary> Фильтр по комментарию техотдела </summary>
        public string EngineerCommentFilter
        {
            get => _EngineerCommentFilter;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _EngineerCommentFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }

        private PartsFilter _SerialPartsFilter;
        /// <summary> Фильтр по серийности деталей </summary>
        public PartsFilter SerialPartsFilter
        {
            get => _SerialPartsFilter;

            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _SerialPartsFilter, value))
                {
                    _ = LoadPartsAsync();
                }
            }
        }


        private ObservableCollection<MachineFilter> _MachineFilters;
        /// <summary> Список станков с необходимостью фильтрации </summary>
        public ObservableCollection<MachineFilter> MachineFilters
        {
            get => _MachineFilters;
            set
            {
                if (!CanBeChanged()) return;
                if (Set(ref _MachineFilters, value))
                {
                    OnPropertyChanged(nameof(MachineFilterSummary));
                    _ = LoadPartsAsync();
                }
            }
        }
        #endregion

        private Part? _SelectedPart;
        /// <summary> Выбранная деталь </summary>
        public Part? SelectedPart
        {
            get => _SelectedPart;
            set => Set(ref _SelectedPart, value);
        }


        private List<SerialPart> _SerialParts;
        /// <summary> Серийные детали </summary>
        public List<SerialPart> SerialParts
        {
            get => _SerialParts;
            set => Set(ref _SerialParts, value);
        }


        public CombinedParts PartsInfo { get; set; }


        private bool _InProgress;
        /// <summary> Загрузка информации </summary>
        public bool InProgress
        {
            get => _InProgress;
            set => Set(ref _InProgress, value);
        }

        private bool _BackgroundInProgress;
        /// <summary> Загрузка информации в фоне </summary>
        public bool BackgroundWorking
        {
            get => _BackgroundInProgress;
            set => Set(ref _BackgroundInProgress, value);
        }

        private bool _AiInProgress;
        /// <summary> Ai думает </summary>
        public bool AiInProgress
        {
            get => _AiInProgress;
            set => Set(ref _AiInProgress, value);
        }

        private bool _WncSearchInProgress;
        /// <summary> Поиск по Windchill в процессе </summary>
        public bool WncSearchInProgress
        {
            get => _WncSearchInProgress;
            set => Set(ref _WncSearchInProgress, value);
        }


        private string _Status = string.Empty;
        /// <summary> Статус </summary>
        public string Status
        {
            get => _Status;
            set => Set(ref _Status, value);
        }

        public bool HasFeatureAi => Util.HasFeature(RemeLogFeature.Ai);
        public bool HasFeatureAdvancedEdit => Util.HasFeature(RemeLogFeature.AdvancedEdit);
        public bool HasFeatureValidationOverride => Util.HasFeature(RemeLogFeature.ValidationOverride);
        public bool HasFeatureAiMasterCheck => Util.HasFeature(RemeLogFeature.AiMasterCheck);
        public bool HasFeatureReasonOverride => Util.HasFeature(RemeLogFeature.ReasonOverride);

        /// <summary>
        /// Кнопка суточного отчёта: доступна, если ошибок нет, либо есть фича
        /// ValidationOverride (тогда при ошибках вместо блока покажется диалог
        /// с подтверждением — см. OnOpenDailyReportWindowCommandExecuted).
        /// </summary>
        public bool CanOpenDailyReportButton => !HasErrors || HasFeatureValidationOverride;
        public bool IsAiAvailable => AiHealthMonitor.Instance.IsAiAvailable;
        public bool IsServerAvailable => AiHealthMonitor.Instance.IsServerAvailable;
        public bool IsOllamaAvailable => AiHealthMonitor.Instance.IsOllamaAvailable;
        public string? HealthError => AiHealthMonitor.Instance.HealthError;
        public string? HealthTooltip => AiHealthMonitor.Instance.HealthTooltip;


        private string _ThinkingThoughts = string.Empty;
        /// <summary> Накапливаемые мысли ИИ для отображения в выдвижной панели. </summary>
        public string ThinkingThoughts
        {
            get => _ThinkingThoughts;
            set
            {
                if (Set(ref _ThinkingThoughts, value))
                {
                    OnPropertyChanged(nameof(HasThinkingThoughts));
                    OnPropertyChanged(nameof(IsThoughtPanelVisible));
                }
            }
        }

        /// <summary> Есть ли хотя бы одна мысль. </summary>
        public bool HasThinkingThoughts => !string.IsNullOrEmpty(ThinkingThoughts);

        private bool _IsThoughtPanelOpen = false;
        /// <summary> Открыта ли панель размышлений ИИ. </summary>
        public bool IsThoughtPanelOpen
        {
            get => _IsThoughtPanelOpen;
            set
            {
                if (Set(ref _IsThoughtPanelOpen, value))
                    OnPropertyChanged(nameof(IsThoughtPanelVisible));
            }
        }

        /// <summary> Панель видна, если открыта или есть содержимое: размышления модели
        /// либо след шагов агента — оба копятся в ThinkingThoughts. </summary>
        public bool IsThoughtPanelVisible => IsThoughtPanelOpen || HasThinkingThoughts;


        private bool _CompactView = false;
        /// <summary> Компактный режим (на самом деле уже нет, это для sfGridView) </summary>
        public bool CompactView
        {
            get => _CompactView;
            set => Set(ref _CompactView, value);
        }


        private User _ViewMode;
        /// <summary> Режим просмотра (встроенная роль) </summary>
        public User ViewMode
        {
            get => _ViewMode;
            set
            {
                Set(ref _ViewMode, value);
                OnPropertyChanged(nameof(VisibleColumnIds));
            }
        }

        private string? _ActiveColumnProfileName;
        /// <summary>
        /// Имя активного пользовательского профиля столбцов. Null — видимость колонок
        /// определяется встроенной ролью (ViewMode), иначе — набором ColumnIds
        /// соответствующего профиля из AppSettings.Instance.ColumnProfiles.
        /// </summary>
        public string? ActiveColumnProfileName
        {
            get => _ActiveColumnProfileName;
            set
            {
                Set(ref _ActiveColumnProfileName, value);
                OnPropertyChanged(nameof(VisibleColumnIds));
                OnPropertyChanged(nameof(IsRoleSelected));
            }
        }

        /// <summary> Активна встроенная роль, а не пользовательский профиль столбцов </summary>
        public bool IsRoleSelected => ActiveColumnProfileName == null;

        /// <summary>
        /// Идентификаторы колонок, видимых в текущем профиле — единственный источник
        /// истины для видимости колонок PartsInfoWindow (см. ColumnVisibilityConverter).
        /// </summary>
        public HashSet<string> VisibleColumnIds =>
            (ActiveColumnProfileName is string name
                ? AppSettings.Instance.ColumnProfiles.FirstOrDefault(p => p.Name == name)?.ColumnIds
                : null)?.ToHashSet()
            ?? PartColumnMeta.GetColumnIdsForRole(ViewMode);

        /// <summary> Пользовательские профили столбцов, доступные для выбора в меню "Вид" </summary>
        public List<ColumnProfile> AvailableColumnProfiles => AppSettings.Instance.ColumnProfiles;

        /// <summary> Есть ли хотя бы один пользовательский профиль столбцов </summary>
        public bool HasColumnProfiles => AvailableColumnProfiles.Count > 0;


        private bool _ViewUnchecked;
        /// <summary> Описание </summary>
        public bool ViewUnchecked
        {
            get => _ViewUnchecked;
            set => Set(ref _ViewUnchecked, value);
        }

        private Overlay _Overlay = new(false);

        public Overlay Overlay
        {
            get => _Overlay;
            set => Set(ref _Overlay, value);
        }

        public List<string> SetupReasons => AppSettings.Instance.SetupReasons.Select(x => x.Reason).ToList();
        public Dictionary<string, bool> SetupReasonsRequireComment =>
            AppSettings.Instance.SetupReasons.ToDictionary(x => x.Reason, x => x.RequireComment);
        public List<string> MachiningReasons => AppSettings.Instance.MachiningReasons.Select(x => x.Reason).ToList();
        public Dictionary<string, bool> MachiningReasonsRequireComment =>
            AppSettings.Instance.MachiningReasons.ToDictionary(x => x.Reason, x => x.RequireComment);

        public double AverageSetupRatio =>
            MachineFilters.Any(f => f.Filter)
            ? Parts.AverageSetupRatio()
            : double.NaN;
        public double AverageProductionRatio =>
            MachineFilters.Any(f => f.Filter)
            ? Parts.AverageProductionRatio()
            : double.NaN;
        public double SetupTimeRatio =>
            MachineFilters.Any(f => f.Filter)
            ? Parts.SetupRatio()
            : double.NaN;
        public double ProductionTimeRatio =>
            MachineFilters.Any(f => f.Filter)
            ? Parts.ProductionRatio()
            : double.NaN;
        public double SpecifiedDowntimesRatio =>
            MachineFilters.Any(f => f.Filter)
            ? Parts.SpecifiedDowntimesRatio(ShiftFilter)
            : double.NaN;
        public double UnspecifiedDowntimesRatio =>
            MachineFilters.Any(f => f.Filter)
            ? Parts.UnspecifiedDowntimesRatio(FromDate, ToDate, ShiftFilter)
            : double.NaN;


        private TimeOnly _EndDateForCalc;
        /// <summary> Конечная дата для расчета </summary>
        public TimeOnly EndDateForCalc
        {
            get => _EndDateForCalc;
            set
            {
                if (Set(ref _EndDateForCalc, value))
                {
                    OnPropertyChanged(nameof(CalculatedTimeDifference));
                    OnPropertyChanged(nameof(CalculatedTimeDifferenceMinutes));
                }
            }
        }

        private TimeOnly _StartDateForCalc;
        /// <summary> Начальная дата для расчета </summary>
        public TimeOnly StartDateForCalc
        {
            get => _StartDateForCalc;
            set
            {
                if (Set(ref _StartDateForCalc, value))
                {
                    OnPropertyChanged(nameof(CalculatedTimeDifference));
                    OnPropertyChanged(nameof(CalculatedTimeDifferenceMinutes));
                }
            }
        }

        public TimeSpan CalculatedTimeDifference
        {
            get
            {
                if (StartDateForCalc == TimeOnly.MinValue || EndDateForCalc == TimeOnly.MaxValue) return TimeSpan.Zero;
                var breakfasts = DescreaseTimes ? TimeOnlys.GetBreaksBetween(StartDateForCalc, EndDateForCalc) : TimeSpan.Zero;
                return EndDateForCalc - StartDateForCalc - breakfasts - new TimeSpan(0, AdditionalDescreaseValue ?? 0, 0);
            }
        }

        public double CalculatedTimeDifferenceMinutes
            => CalculatedTimeDifference.TotalMinutes;


        private bool _CalcFixed;
        /// <summary> Описание </summary>
        public bool CalcFixed
        {
            get => _CalcFixed;
            set => Set(ref _CalcFixed, value);
        }


        private bool _DescreaseTimes;
        /// <summary> Описание </summary>
        public bool DescreaseTimes
        {
            get => _DescreaseTimes;
            set
            {
                if (Set(ref _DescreaseTimes, value))
                {
                    OnPropertyChanged(nameof(CalculatedTimeDifference));
                    OnPropertyChanged(nameof(CalculatedTimeDifferenceMinutes));
                }
            }
        }


        private int? _AdditionalDescreaseValue = null;
        /// <summary> Время для дополнительного вычитания в калькуляторе времени, также используется как разделитель для серийности в отчёте за период </summary>
        public int? AdditionalDescreaseValue
        {
            get => _AdditionalDescreaseValue;
            set
            {
                if (Set(ref _AdditionalDescreaseValue, value))
                {
                    OnPropertyChanged(nameof(CalculatedTimeDifference));
                    OnPropertyChanged(nameof(CalculatedTimeDifferenceMinutes));
                }
            }
        }


        private bool _UseMockData = false;
        /// <summary>
        /// Фиктивные данные для отладки
        /// </summary>
        public bool UseMockData
        {
            get => _UseMockData;
            set => Set(ref _UseMockData, value);
        }


        public bool MachineVisibility => MachineFilters.Count(m => m.Filter == true) == 1;

        /// <summary> Текст на кнопке фильтра по станкам — сколько выбрано. </summary>
        public string MachineFilterSummary => MachineFilters.Summary();

        #region IncreaseDateCommand
        public ICommand IncreaseDateCommand { get; }
        private void OnIncreaseDateCommandExecuted(object p)
        {
            LockUpdate();
            FromDate = FromDate.AddDays(1);
            ToDate = ToDate.AddDays(1);
            UnlockUpdate();
        }
        private bool CanIncreaseDateCommandExecute(object p) => true;
        #endregion

        #region DecreaseDateCommand
        public ICommand DecreaseDateCommand { get; }
        private void OnDecreaseDateCommandExecuted(object p)
        {
            LockUpdate();
            FromDate = FromDate.AddDays(-1);
            ToDate = ToDate.AddDays(-1);
            UnlockUpdate();
        }
        private bool CanDecreaseDateCommandExecute(object p) => true;
        #endregion

        #region SetYesterdayDateCommand
        public ICommand SetYesterdayDateCommand { get; }
        private void OnSetYesterdayDateCommandExecuted(object p)
        {
            LockUpdate();
            FromDate = DateTime.Today.AddDays(-1);
            ToDate = FromDate;
            UnlockUpdate();
        }
        private bool CanSetYesterdayDateCommandExecute(object p) => true;
        #endregion

        #region SetSpecificDateCommand
        public ICommand SetSpecificDateCommand { get; }
        /// <summary> Устанавливает выбранную в пикере дату сразу в оба календаря (От и До). </summary>
        private void OnSetSpecificDateCommandExecuted(object p)
        {
            if (p is not DateTime date) return;
            LockUpdate();
            FromDate = date.Date;
            ToDate = date.Date;
            UnlockUpdate();
        }
        private bool CanSetSpecificDateCommandExecute(object p) => true;
        #endregion

        #region SetWeekDateCommand
        public ICommand SetWeekDateCommand { get; }
        private void OnSetWeekDateCommandExecuted(object p)
        {
            FromDate = ToDate.AddDays(-7);
        }
        private bool CanSetWeekDateCommandExecute(object p) => true;
        #endregion

        #region SetMonthDateCommand
        public ICommand SetMonthDateCommand { get; }
        private void OnSetMonthDateCommandExecuted(object p)
        {
            LockUpdate();
            if (FromDate == ToDate.AddDays(-30))
            {
                FromDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                ToDate = DateTime.Today.AddDays(-1);
            }
            else
            {
                FromDate = ToDate.AddDays(-30);
            }
            UnlockUpdate();
        }
        private bool CanSetMonthDateCommandExecute(object p) => true;
        #endregion

        #region SetYearDateCommand
        public ICommand SetYearDateCommand { get; }
        private void OnSetYearDateCommandExecuted(object p)
        {
            LockUpdate();
            FromDate = new DateTime(2024, 01, 01);
            ToDate = DateTime.Today;
            UnlockUpdate();
        }
        private bool CanSetYearDateCommandExecute(object p) => true;
        #endregion

        #region SetAllDateCommand
        public ICommand SetAllDateCommand { get; }
        private void OnSetAllDateCommandExecuted(object p)
        {
            LockUpdate();
            FromDate = new DateTime(2023, 01, 01, 00, 00, 00);
            ToDate = DateTime.Today;
            UnlockUpdate();
        }
        private bool CanSetAllDateCommandExecute(object p) => true;
        #endregion

        #region SetSpecificMonthCommand
        public ICommand SetSpecificMonthCommand { get; }
        private void OnSetSpecificMonthCommandExecuted(object p)
        {
            if (!int.TryParse(p?.ToString(), out var month)) return;
            var year = ToDate.Year;
            LockUpdate();
            FromDate = new DateTime(year, month, 1);
            ToDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            UnlockUpdate();
        }
        private bool CanSetSpecificMonthCommandExecute(object p) => true;
        #endregion

        #region SetSpecificYearCommand
        public ICommand SetSpecificYearCommand { get; }
        private void OnSetSpecificYearCommandExecuted(object p)
        {
            if (!int.TryParse(p?.ToString(), out var year)) return;
            LockUpdate();
            FromDate = new DateTime(year, 1, 1);
            ToDate = new DateTime(year, 12, 31);
            UnlockUpdate();
        }
        private bool CanSetSpecificYearCommandExecute(object p) => true;
        #endregion

        #region IncreaseSetupCommand
        public ICommand IncreaseSetupCommand { get; }
        private void OnIncreaseSetupCommandExecuted(object p)
        {
            if (SetupFilter is null)
            {
                SetupFilter = 1;
            }
            else if (SetupFilter is int i && i < int.MaxValue)
            {
                SetupFilter++;
            }
        }
        private bool CanIncreaseSetupCommandExecute(object p) => true;
        #endregion

        #region DecreaseSetupCommand
        public ICommand DecreaseSetupCommand { get; }
        private void OnDecreaseSetupCommandExecuted(object p)
        {
            if (SetupFilter is int i && i > 0)
            {
                if (SetupFilter == 1)
                {
                    SetupFilter = null;
                }
                else
                {
                    SetupFilter--;
                }
            }
        }
        private bool CanDecreaseSetupCommandExecute(object p) => true;
        #endregion

        #region ClearContent
        public ICommand ClearContentCommand { get; }
        private void OnClearContentCommandExecuted(object p)
        {
            if (p is TextBox textBox)
            {
                textBox.Text = "";
            }
        }
        private static bool CanClearContentCommandExecute(object p) => true;
        #endregion

        #region SearchInWindchill
        public ICommand SearchInWindchillCommand { get; }

        /// <summary>
        /// Поиск в Windchill по имени детали — через REST API (см. <see cref="Util.SearchInWindchill"/>
        /// и класс <see cref="WindchillClient"/>). Один запрос, результат приходит сразу.
        ///
        /// Таймаут (30 сек) защищает от зависшей сети/сервера. Он отделён от отмены
        /// предыдущего поиска (если пользователь быстро меняет фильтр и запускает новый
        /// поиск, не дождавшись старого): токен предыдущего поиска в этом случае гасится
        /// явно, и фильтр в catch ниже отличает эту ситуацию от настоящего таймаута — иначе
        /// пользователь видел бы предупреждение о таймауте всякий раз, когда просто быстро
        /// меняет фильтр.
        /// </summary>
        private async void OnSearchInWindchillCommandExecuted(object p)
        {
            if (p is PartsInfoWindow w)
            {
                _wncCancellationTokenSource?.Cancel();
                _wncCancellationTokenSource?.Dispose();
                _wncCancellationTokenSource = new CancellationTokenSource();
                var supersedeToken = _wncCancellationTokenSource.Token;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(supersedeToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                var cancellationToken = timeoutCts.Token;

                try
                {
                    WncSearchInProgress = true;
                    Status = "Поиск в Windchill...";

                    var (wncObjects, truncated) = await Util.SearchInWindchill(PartNameFilter, cancellationToken);

                    Util.Debug(wncObjects);
                    if (wncObjects.Any())
                    {
                        // Уткнулись в лимит WindchillClient.MaxResults — реальных совпадений
                        // может быть больше, список не полный.
                        if (truncated)
                        {
                            MessageBoxWindow.Show(
                                $"Показаны первые {wncObjects.Count} совпадений — возможно, есть ещё. " +
                                "Уточните название детали, чтобы увидеть нужное.",
                                "Слишком широкий запрос", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }

                        var wncObjectsWindow = new WncObjectsWindow(wncObjects.ToObservableCollection(), PartNameFilter) { Owner = w };
                        wncObjectsWindow.Show();
                    }
                    else
                    {
                        MessageBoxWindow.Show("Ничего не найдено :с", ":c", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (TaskCanceledException) when (!supersedeToken.IsCancellationRequested)
                {
                    // Настоящий таймаут (30 сек), а не отмена новым поиском — сеть/сервер не
                    // ответили вовремя.
                    MessageBoxWindow.Show(
                        "Windchill не ответил за отведённое время (30 сек). Попробуйте ещё раз.",
                        "Таймаут поиска", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (TaskCanceledException)
                {
                    // Отменено запуском нового поиска — молча, это ожидаемо.
                }
                catch (Exception ex)
                {
                    MessageBoxWindow.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    WncSearchInProgress = false;
                    if (supersedeToken.IsCancellationRequested)
                    {
                        Status = "Отменено";
                        await Task.Delay(2000);
                    }
                    Status = "";
                }
            }

        }
        private bool CanSearchInWindchillCommandExecute(object p) => !string.IsNullOrWhiteSpace(PartNameFilter) && p is PartsInfoWindow;
        #endregion

        #region SearchInWinnum
        public ICommand SearchInWinnumCommand { get; }
        private async void OnSearchInWinnumCommandExecuted(object p)
        {
            if (p is Part part && part == SelectedPart)
            {
                IProgress<string> progress = new Progress<string>(m => Status = m);
                try
                {
                    BackgroundWorking = true;
                    if (SelectedPart == null || string.IsNullOrEmpty(AppSettings.Instance.ConnectionString)) return;
                    progress.Report("Получение данных о станках");
                    var machines = await Database.GetMachinesAsync(progress);
                    var machine = machines.FirstOrDefault(m => m.Name == SelectedPart.Machine);
                    if (machine == null)
                    {
                        progress.Report("Не удалось сопоставить станок указанный при изготовлении со станком в Winnum");
                        await Task.Delay(3000);
                        return;
                    }
                    progress.Report("Чтение конфигурации Winnum");
                    var winnumConfig = await libeLog.Infrastructure.Database.GetWinnumConfigAsync(AppSettings.Instance.ConnectionString);
                    var winnumClient = new ApiClient(winnumConfig.BaseUri, winnumConfig.User, winnumConfig.Pass);
                    var operation = new Operation(winnumClient, machine);
                    var signal = new Signal(winnumClient, machine);
                    progress.Report("Чтение данных из Winnum");

                    var startTime = SelectedPart.StartMachiningTime;

                    var platformDateTimeTask = operation.GetPlatformDateTimeAsync(progress);
                    var cloudDateTimeTask = operation.GetCloudDateTimeAsync(progress);
                    var priorityTagDurationTask = operation.GetPriorityTagDurationAsync(AppId.CNC_MONITORING, startTime, SelectedPart.EndMachiningTime, progress);
                    var tagIntervalCalculationTask = operation.GetTagIntervalCalculationAsync(AppId.CNC_MONITORING, TagId.NC_PROGRAM_RUN, startTime, SelectedPart.EndMachiningTime, progress, base_shift: false);
                    var simpleTagIntervalCalculationTask = operation.GetSimpleTagIntervalCalculationAsync(AppId.CNC_MONITORING, TagId.NC_PROGRAM_RUN, startTime, SelectedPart.EndMachiningTime, progress);
                    var operationsSummaryTask = operation.GetOperationSummaryAsync(AppId.CNC_MONITORING, startTime, SelectedPart.EndMachiningTime, progress);
                    var completedQtyTask = operation.GetCompletedQtyAsync(AppId.CNC_MONITORING, startTime, SelectedPart.EndMachiningTime, progress);
                    var startCountTask = signal.GetSignalAsync(machine.WnCounterSignal, SignalType.ByTime, Ordering.Asc, startTime.AddMinutes(-5), SelectedPart.EndMachiningTime.AddMinutes(5), progress: progress) ;
                    var endCountTask = signal.GetSignalAsync(machine.WnCounterSignal, SignalType.ByTime, Ordering.Asc, startTime.AddMinutes(-5), SelectedPart.EndMachiningTime.AddMinutes(5), progress: progress);
                    var programNamesTask = signal.GetUniqSignalsAsync(machine.WnNcProgramNameSignal, Ordering.Asc, startTime.AddMinutes(-5), SelectedPart.EndMachiningTime.AddMinutes(5), progress: progress);
                    var machineInfoTask = operation.GetMachineInfo(AppId.CNC_MONITORING, progress);

                    await Task.WhenAll(
                        platformDateTimeTask, 
                        cloudDateTimeTask, 
                        priorityTagDurationTask, 
                        tagIntervalCalculationTask, 
                        simpleTagIntervalCalculationTask, 
                        operationsSummaryTask,
                        completedQtyTask, 
                        startCountTask, 
                        endCountTask, 
                        machineInfoTask);

                    progress.Report("Разбор полученных данных и вычисления");

                    var platformDateTime = await platformDateTimeTask;
                    var cloudDateTime = await cloudDateTimeTask;
                    var priorityTagDuration = await priorityTagDurationTask;
                    var tagIntervalCalculation = await tagIntervalCalculationTask;
                    var simpleTagIntervalCalculation = await simpleTagIntervalCalculationTask;
                    var operationsSummary = await operationsSummaryTask;
                    var completedQty = await completedQtyTask;
                    var startCount = await startCountTask;
                    var endCount = await endCountTask;
                    var programNamesRaw = await programNamesTask;
                    var machineInfoRaw = await machineInfoTask;

                    
                    var tagIntervalCalculations = Parser.ParseTimeIntervals(Parser.ParseXmlItems(tagIntervalCalculation), true);
                    var orderedTagIntervalCalculations = tagIntervalCalculations.OrderBy(x => x.Start);
                    var priorityTagDurations = Parser.ParsePriorityTagDurations(priorityTagDuration, startTime, SelectedPart.EndMachiningTime);
                    var programNames = Parser.ParseXmlItems(programNamesRaw).Select(x => x["value"]).OrderBy(x => x).ToList();
                    var machineInfo = Parser.ParseXmlItems(machineInfoRaw).First();
                    var serialNumber = machineInfo["SerialNumber"];

                    var h1 = double.Parse(Parser.ParseXmlItems(tagIntervalCalculation).First()["hours"]);
                    var h2 = double.Parse(Parser.ParseXmlItems(simpleTagIntervalCalculation).First()["hours"]);
                    var h3 = TimeSpan.FromHours(priorityTagDurations.Where(p => p.Tag == "Программа выполняется").Sum(x => x.Duration)).TotalHours;
                    double? completed = null;
                    if (Parser.ParseXmlItems(startCount) is { Count: > 0} sc && Parser.ParseXmlItems(endCount) is { Count: > 0} fc)
                    {
                        completed = double.Parse(fc.Last()["value"]) - int.Parse(sc.First()["value"]);
                    }
                    if (completed.HasValue && completed.Value == 0) completed = SelectedPart.FinishedCount;

                    var m1 = h1 * 60 / SelectedPart.FinishedCount;
                    var m2 = h2 * 60 / SelectedPart.FinishedCount;
                    var m3 = h3 * 60 / SelectedPart.FinishedCount;

                    var intervals = orderedTagIntervalCalculations.Select(interval => new TimeInterval(interval.Start, interval.End)).ToList();
                    var sb = new StringBuilder();

                    var start = startTime.AddMinutes(-5);
                    var end = SelectedPart.EndMachiningTime.AddMinutes(5);
                    var signalNames = Enumerable.Range(0, 2000).Select(i => $"A{i}").ToList();
                    var signals = new ConcurrentDictionary<string, ConcurrentBag<string>>();

                    if (Parser.TryParseXmlItems(completedQty, out var cqty))
                    {
                        foreach (var item in cqty)
                        {
                            sb.Append("УП: ");
                            sb.Append(item["program_name"]);
                            sb.Append(" | Завершена раз: ");
                            sb.Append(item["count_completed"]);
                            sb.Append(" | Прервана раз: ");
                            sb.Append(item["count_incompleted"]);
                            sb.Append(" | Счётчик: ");
                            sb.AppendLine(item["count_parts"]);
                        }
                    }
                    
                    progress.Report("Подготовка окна");
                    sb.AppendLine($"Файлы УП: {string.Join(", ", programNames)}");
                    var completedInfo = sb.ToString();

                    progress.Report("Построение сводной таблицы");

                    var timelineSources = new List<TimelineSource>();
                    var tagsTimelineSource = TimelineSource.FromPriorityTag(
                        displayName: "Тэг",
                        fetchAsync: () => operation.GetPriorityTagDurationAsync(
                            AppId.CNC_MONITORING_WITH_STOPS_AND_MANUAL,
                            start, end),
                        filterStart: start,
                        filterEnd: end);
                    timelineSources.Add(tagsTimelineSource);
                    timelineSources.AddRange(BuildMachineTimelineSources(signal, machine, start, end, progress));

                    var timeline = await TimelineBuilder.BuildAsync(timelineSources, TimeSpan.FromSeconds(5), progress);
                    progress.Report("");

                    var pageUrl = operation.BuildPageUrl(
                        "winnum.views.pages.app.cnc.reports.DetailedTagPriorityWorkReportPage",
                        "processPage",
                        AppId.CNC_MONITORING_WITH_STOPS_AND_MANUAL,
                        startTime);

                    var win = new WinnumInfoWindow($"" +
                        $"Локальное время:      {DateTime.Now:g} │ М/В Вар.1:   {(double.IsFinite(m1) ? $"{TimeSpan.FromMinutes(m1):hh\\:mm\\:ss}" : "00:00:00")} │\n" +
                        $"Время на платформе:   {platformDateTime:g} │ М/В Вар.2:   {(double.IsFinite(m2) ? $"{TimeSpan.FromMinutes(m2):hh\\:mm\\:ss}" : "00:00:00")} │\n" +
                        $"Время на облаке:      {cloudDateTime:g} │ М/В Вар.3:   {(double.IsFinite(m3) ? $"{TimeSpan.FromMinutes(m3):hh\\:mm\\:ss}" : "00:00:00")} │\n" +
                        $"───────────────────────────────────────┴───────────────────────┘\n" +
                        $"Выполнено по счётчику Winnum: {(completed.HasValue ? completed.Value : $"{SelectedPart.FinishedCount} (не удалось получить из Winnum, отображается количество из ЭЖ)")}\n\n" +
                        $"Информация по операциям за период " +
                        $"{new DateTime(startTime.Year, startTime.Month, startTime.Day):d} - " +
                        $"{new DateTime(SelectedPart.EndMachiningTime.Year, SelectedPart.EndMachiningTime.Month, SelectedPart.EndMachiningTime.Day):d}\n" +
                        $"{completedInfo}" +
                        $"", Path.Combine(winnumConfig.NcProgramFolder, serialNumber), priorityTagDurations, intervals, timeline, pageUrl);
                    win.Show();
                }
                catch (Exception ex)
                {
                    Util.WriteLog(ex);
                    progress.Report(ex.Message);
                    await Task.Delay(3000);
                }
                finally
                {
                    BackgroundWorking = false;
                }
            }
        }
        private bool CanSearchInWinnumCommandExecute(object p) => SelectedPart is { };
        #endregion

        #region ShowInfo
        public ICommand ShowInfoCommand { get; }
        private void OnShowInfoCommandExecuted(object p)
        {
            MessageBoxWindow.Show("Тут будет информация по выборке", "Заглушка", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private static bool CanShowInfoCommandExecute(object p) => true;
        #endregion

        #region OperatorReportToExcel
        public ICommand OperatorReportToExcelCommand { get; }
        private async void OnOperatorReportToExcelCommandExecuted(object p)
        {
            try
            {
                using (Overlay = new())
                {
                    var dlg = new ExportOperatorReportDialogWindow();
                    if (p is PartsInfoWindow w) dlg.Owner = w;
                    if (dlg.ShowDialog() == false)
                    {
                        Status = "Отмена";
                        return;
                    }
                    var path = Util.GetXlsxPath();
                    if (string.IsNullOrEmpty(path))
                    {
                        Status = "Выбор файла отменён";
                        return;
                    }
                    if (dlg.DataContext is ExportOperatorDailogWindowViewModel dx)
                    {
                        var serialParts = dx.OnlySerialParts ? (await libeLog.Infrastructure.Database.GetSerialPartsAsync(AppSettings.Instance.ConnectionString!)).PartNamesHashSet(EnumerableExtensions.PartNameNormalizeOption.NormalizeAndRemoveParentheses) : null;
                        var includeExcludedParts = dx.IncludeExcludedlParts;
                        InProgress = true;
                        Status = await Xl.ExportOperatorReportAsync(Parts, FromDate, ToDate, path, dx.IncludeSmallBatch, serialParts, includeExcludedParts);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanOperatorReportToExcelCommandExecute(object p) => true;
        #endregion

        #region OperatorsShiftsReportToExcel
        public ICommand OperatorsShiftsReportToExcelCommand { get; }
        private async void OnOperatorsShiftsReportToExcelCommandExecuted(object p)
        {
            try
            {
                using (Overlay = new())
                {
                    var path = Util.GetXlsxPath();
                    if (string.IsNullOrEmpty(path))
                    {
                        Status = "Выбор файла отменён";
                        return;
                    }
                    await Task.Run(() =>
                    {
                        InProgress = true;
                        Status = Xl.ExportOperatorsShiftsReport(Parts, FromDate, ToDate, path);
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanOperatorsShiftsReportToExcelCommandExecute(object p) => true;
        #endregion

        #region ExportPartsReportToExcel
        public ICommand ExportPartsReportToExcelCommand { get; }
        private async void OnExportPartsReportToExcelCommandExecuted(object p)
        {
            try
            {

                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                await Task.Run(() =>
                {
                    InProgress = true;
                    Status = Xl.ExportPartsInfo(Parts, path, FromDate, ToDate);
                });
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportPartsReportToExcelCommandExecute(object p) => true;
        #endregion

        #region ExportNewReportForPeriodToExcel
        public ICommand ExportNewReportForPeriodToExcelCommand { get; }
        private async void OnExportNewReportForPeriodToExcelCommandExecuted(object p)
        {
            try
            {
                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                var progress = new Progress<string>(message =>
                {
                    Status = message;
                });

                await App.Current.Dispatcher.InvokeAsync(() => InProgress = true);
                Status = await Task.Run(() =>
                    Xl.ExportNewReportForPeroidAsync(
                        Parts, FromDate, ToDate, ShiftFilter, path, progress
                    ).GetAwaiter().GetResult()
                );
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportNewReportForPeriodToExcelCommandExecute(object p) => true;
        #endregion

        #region ExportPeriodsComparisonToExcel
        public ICommand ExportPeriodsComparisonCommand { get; }
        private async void OnExportPeriodsComparisonCommandExecuted(object p)
        {
            try
            {
                var dlg = new PeriodsComparisonWindow(FromDate, ToDate) { Owner = Application.Current.MainWindow };
                if (dlg.ShowDialog() != true)
                {
                    Status = "Отмена";
                    return;
                }

                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                var progress = new Progress<string>(message =>
                {
                    Status = message;
                });

                await App.Current.Dispatcher.InvokeAsync(() => InProgress = true);
                Status = await Xl.ExportPeriodsComparisonAsync(
                    dlg.FromDate1, dlg.ToDate1, dlg.Label1,
                    dlg.FromDate2, dlg.ToDate2, dlg.Label2,
                    ShiftFilter, path, progress
                );
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportPeriodsComparisonCommandExecute(object p) => true;
        #endregion

        #region ExportReportForPeriodToExcel
        public ICommand ExportReportForPeriodToExcelCommand { get; }
        private async void OnExportReportForPeriodToExcelCommandExecuted(object p)
        {
            try
            {
                string runCountFilter = "";
                bool addSheetPerMachine = true;
                if (MessageBoxWindow.Show("Задать фильтр по количеству запусков?", "Вопросик", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxDefaultButton.Yes) == MessageBoxResult.Yes)
                {
                    var dialog = new PartSelectionFilterWindow("", true)
                    {
                        Owner = p as PartsInfoWindow
                    };
                    if (dialog.ShowDialog() != true)
                    {
                        Status = "Отмена";
                        return;
                    }
                    runCountFilter = dialog.RunCountFilter;
                    addSheetPerMachine = dialog.AddSheetPerMachine;
                }
                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                var progress = new Progress<string>(message =>
                {
                    Status = message;
                });

                await Task.Run(() =>
                {
                    InProgress = true;
                    Status = Xl.ExportReportForPeroid(Parts, FromDate, ToDate, ShiftFilter, path, AdditionalDescreaseValue, runCountFilter, addSheetPerMachine, progress);
                });
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportReportForPeriodToExcelCommandExecute(object p) => true;
        #endregion

        #region ExportLongSetups
        public ICommand ExportLongSetupsCommand { get; }
        private async void OnExportLongSetupsCommandExecuted(object p)
        {
            try
            {

                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                await Task.Run(() =>
                {
                    InProgress = true;
                    Status = Xl.ExportLongSetups(Parts, path);
                });
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportLongSetupsCommandExecute(object p) => true;
        #endregion

        #region ExportValidationErrors
        public ICommand ExportValidationErrorsCommand { get; }
        private async void OnExportValidationErrorsCommandExecuted(object p)
        {
            try
            {
                if (!Xl.HasValidationErrors(Parts))
                {
                    MessageBoxWindow.Show("За выбранный период записей без объяснения мастера не найдено.",
                        "Ошибок валидации нет", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                await Task.Run(() =>
                {
                    InProgress = true;
                    Status = Xl.ExportValidationErrors(Parts, path);
                });
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportValidationErrorsCommandExecute(object p) => true;
        #endregion

        #region ExportIncorrectFills
        public ICommand ExportIncorrectFillsCommand { get; }
        private async void OnExportIncorrectFillsCommandExecuted(object p)
        {
            try
            {
                if (!Xl.HasReasonOverrides(Parts))
                {
                    MessageBoxWindow.Show("За выбранный период переопределённых причин не найдено.",
                        "Переопределений нет", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                await Task.Run(() =>
                {
                    InProgress = true;
                    Status = Xl.ExportIncorrectFills(Parts, path);
                });
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportIncorrectFillsCommandExecute(object p) => true;
        #endregion

        #region ExportHistoryToExcel
        public ICommand ExportHistoryToExcelCommand { get; }
        private async void OnExportHistoryToExcelCommandExecuted(object p)
        {
            try
            {
                var dlg = new PartnameDialogWindow(PartNameFilter);
                if (p is PartsInfoWindow w) dlg.Owner = w;
                if (dlg.ShowDialog() != true) return;
                var path = Util.GetXlsxPath();
                var partName = dlg.PartName;
                var oredersCount = dlg.OrderCount;
                await Task.Run(async () =>
                {
                    InProgress = true;
                    var cancellationToken = _cancellationTokenSource.Token;
                    var parts = await Task.Run(() => Database.ReadPartsWithConditions(BuildConditionsForPartForAllTime(partName), cancellationToken));
                    Status = Xl.ExportHistory(parts, path, oredersCount);
                });
                
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportHistoryToExcelCommandExecute(object p) => true;
        #endregion

        #region ExportToolSearchCasesToExcel
        public ICommand ExportToolSearchCasesToExcelCommand { get; }
        private async void OnExportToolSearchCasesToExcelCommandExecuted(object p)
        {
            try
            {
                InProgress = true;
                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                IProgress<string> progress = new Progress<string>(p => Status = p);
                Status = $"Файл сохранен в: {await Xl.ExportToolSearchCasesAsync(Parts, path, progress)}";
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
                Util.WriteLog(ex, "Ошибка при экспорте отчёта по поискам.");
                Status = "";
            }
            finally { InProgress = false; }
        }
        private static bool CanExportToolSearchCasesToExcelCommandExecute(object p) => true;
        #endregion

        #region CheckAssignmentWithFact
        public ICommand CheckAssignmentWithFactCommand { get; }
        private async void OnCheckAssignmentWithFactCommandExecuted(object p)
        {
            try
            {
                if (!File.Exists(AppSettings.Instance.GoogleCredentialPath) )
                {
                    MessageBoxWindow.Show("Файл с учетными данными Google не существует.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                if (!File.Exists(AppSettings.Instance.GoogleCredentialPath) && string.IsNullOrWhiteSpace(AppSettings.Instance.AssignedPartsSheet))
                {
                    MessageBoxWindow.Show("Не указан ID Google таблицы.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                IProgress<string> progress = new Progress<string>(m => Status = m);
                InProgress = true;
                progress.Report("Подключение к Google таблице");
                var gs = new GoogleSheet(AppSettings.Instance.GoogleCredentialPath!, AppSettings.Instance.AssignedPartsSheet!);
                progress.Report("Получение информации из Google таблицы");
                var assignedParts = await gs.GetAssignedPartsAsync(progress);
                progress.Report("Данные из СЗН получены, формирование отчёта...");
                Status = await Task.Run(() => Xl.ExportAssignmentCheckResult(Parts, assignedParts, path, progress));


            }
            catch (Google.GoogleApiException ex)
            {
                MessageBoxWindow.Show(GoogleSheet.ExceptionMessage(ex), "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { InProgress = false; }
        }
        private static bool CanCheckAssignmentWithFactCommandExecute(object p) => true;
        #endregion

        #region NormsAndWorkloadAnalysis
        public ICommand NormsAndWorkloadAnalysisCommand { get; }
        private async void OnNormsAndWorkloadAnalysisCommandExecuted(object p)
        {
            try
            {
                
                IProgress<string> progress = new Progress<string>(m => Status = m);
                InProgress = true;

                //List<DateTime> dates = new();

                //var dlg = new GetDatesWindow(ToDate);
                //if (dlg.ShowDialog() == false)
                //{
                //    Status = "Отмена";
                //    InProgress = false;
                //    await Task.Delay(3000);
                //    Status = "";
                //    return;
                //}
                //dates = dlg.Dates.ToList();
                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                Status = await Xl.ExportNormsAndWorkloadAnalysisAsync(Parts, path, progress);


            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { InProgress = false; }
        }
        private static bool CanNormsAndWorkloadAnalysisCommandExecute(object p) => true;
        #endregion
        
        #region ExportShiftsInfoReport
        public ICommand ExportShiftsInfoReportCommand { get; }
        private async void OnExportShiftsInfoReportCommandExecuted(object p)
        {
            try
            {

                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                await Task.Run(() =>
                {
                    InProgress = true;
                    Status = Xl.ExportShiftsInfo(Parts, path, FromDate, ToDate);
                }
                );

            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportShiftsInfoReportCommandExecute(object p) => true;
        #endregion

        #region ExportVerevkinReportReport
        /// <summary>
        /// Выгружает в xlsx сводку по детали и заказу: изготовленное количество и машинное
        /// время, разложенное по установкам 1–5.
        /// </summary>
        public ICommand ExportVerevkinReportCommand { get; }
        private async void OnExportVerevkinReportCommandExecuted(object p)
        {
            try
            {
                var path = Util.GetXlsxPath(false);
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                InProgress = true;
                Progress<string> progress = new(p => Status = p);
                await Task.Run(() =>
                {
                    var verevkinParts = Parts.GroupBy(p => new { p.PartName, p.Order })
                    .Select(g =>
                    {
                        var verevkinPart = new VerevkinPart(g.Key.PartName, g.Key.Order, g.Sum(p => p.FinishedCount));

                        foreach (var part in g)
                        {
                            switch (part.Setup)
                            {
                                case 1:
                                    verevkinPart.MachineTime1 += part.MachiningTime;
                                    break;
                                case 2:
                                    verevkinPart.MachineTime2 += part.MachiningTime;
                                    break;
                                case 3:
                                    verevkinPart.MachineTime3 += part.MachiningTime;
                                    break;
                                case 4:
                                    verevkinPart.MachineTime4 += part.MachiningTime;
                                    break;
                                case 5:
                                    verevkinPart.MachineTime5 += part.MachiningTime;
                                    break;
                            }
                        }
                        return verevkinPart;
                    }).ToList();
                    Xl.ExportVerevkinReport(verevkinParts, path, progress);
                });
            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportVerevkinReportCommandExecute(object p) => true;
        #endregion

        #region ExportToExcel
        public ICommand ExportToExcelCommand { get; }
        private async void OnExportToExcelCommandExecuted(object p)
        {
            try
            {

                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                await Task.Run(() =>
                {
                    InProgress = true;
                    Status = Xl.ExportDataset(Parts, path);
                });

            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
        }
        private static bool CanExportToExcelCommandExecute(object p) => true;
        #endregion

        #region DeleteFilter
        public ICommand DeleteFilterCommand { get; }
        private void OnDeleteFilterCommandExecuted(object p)
        {
            LockUpdate();
            ShiftFilter = new(ShiftType.All);
            SerialPartsFilter = new(PartsFilterType.All);
            OperatorFilter = string.Empty;
            PartNameFilter = string.Empty;
            OrderFilter = string.Empty;
            SetupFilter = null;
            FinishedCountFilter = "";
            ToDate = PartsInfo.ToDate;
            FromDate = PartsInfo.FromDate;
            _EngineerConclusionFilter = "";
            _EngineerCommentFilter = "";
            for (int i = 0; i < MachineFilters.Count; i++)
            {
                MachineFilters[i].Filter = MachineFilters[i].Machine == PartsInfo.Machine;
            }
            UnlockUpdate();
        }
        private static bool CanDeleteFilterCommandExecute(object p) => true;
        #endregion

        #region ShowAllMachines
        public ICommand ShowAllMachinesCommand { get; }
        private void OnShowAllMachinesCommandExecuted(object p)
        {
            LockUpdate();
            for (int i = 0; i < MachineFilters.Count; i++)
            {
                MachineFilters[i].Filter = true;
            }
            UnlockUpdate();
        }
        private static bool CanShowAllMachinesCommandExecute(object p) => true;
        #endregion

        #region HideAllMachines
        public ICommand HideAllMachinesCommand { get; }
        private void OnHideAllMachinesCommandExecuted(object p)
        {
            LockUpdate();
            for (int i = 0; i < MachineFilters.Count; i++)
            {
                MachineFilters[i].Filter = false;
            }
            UnlockUpdate();
        }
        private static bool CanHideAllMachinesCommandExecute(object p) => true;
        #endregion

        #region InvertMachines
        public ICommand InvertMachinesCommand { get; }
        private void OnInvertMachinesCommandExecuted(object p)
        {
            LockUpdate();
            for (int i = 0; i < MachineFilters.Count; i++)
            {
                MachineFilters[i].Filter = !MachineFilters[i].Filter;
            }
            UnlockUpdate();
        }
        private static bool CanInvertMachinesCommandExecute(object p) => true;
        #endregion

        #region SetLatheMachines
        public ICommand SetLatheMachinesCommand { get; }
        private void OnSetLatheMachinesCommandExecuted(object p)
        {
            LockUpdate();
            for (int i = 0; i < MachineFilters.Count; i++)
            {
                MachineFilters[i].Filter = MachineFilters[i].Type.ToLowerInvariant().Contains("токарный");
            }
            UnlockUpdate();
        }
        private static bool CanSetLatheMachinesCommandExecute(object p) => true;
        #endregion

        #region SetMillMachines
        public ICommand SetMillMachinesCommand { get; }
        private void OnSetMillMachinesCommandExecuted(object p)
        {
            LockUpdate();
            for (int i = 0; i < MachineFilters.Count; i++)
            {
                MachineFilters[i].Filter = MachineFilters[i].Type.ToLowerInvariant().Contains("фрезерный");
            }
            UnlockUpdate();
        }
        private static bool CanSetMillMachinesCommandExecute(object p) => true;
        #endregion

        #region SetHorMillMachines
        public ICommand SetHorMillMachinesCommand { get; }
        private void OnSetHorMillMachinesCommandExecuted(object p)
        {
            LockUpdate();
            for (int i = 0; i < MachineFilters.Count; i++)
            {
                MachineFilters[i].Filter = MachineFilters[i].Type.ToLowerInvariant().Contains("горизонтально");
            }
            UnlockUpdate();
        }
        private static bool CanSetHorMillMachinesCommandExecute(object p) => true;
        #endregion

        #region SetVerMillMachines
        public ICommand SetVerMillMachinesCommand { get; }
        private void OnSetVerMillMachinesCommandExecuted(object p)
        {
            LockUpdate();
            for (int i = 0; i < MachineFilters.Count; i++)
            {
                MachineFilters[i].Filter = MachineFilters[i].Type.ToLowerInvariant().Contains("вертикально");
            }
            UnlockUpdate();
        }
        private static bool CanSetVerMillMachinesCommandExecute(object p) => true;
        #endregion

        #region ShowArchiveTable
        public ICommand ShowArchiveTableCommand { get; }
        private void OnShowArchiveTableCommandExecuted(object p)
        {
            using (Overlay = new())
            {
                //var disqualifiedKeys = new HashSet<(string PartName, int Setup, string Machine)>();
                //var filteredPrograms = new List<Part>();
                //foreach (var part in Parts)
                //{
                //    var key = (part.PartName, part.Setup, part.Machine);

                //}

                var parts = Parts
                    .GroupBy(p => (p.PartName, p.Setup, p.Machine))
                    .Select(g => g.OrderByDescending(p => p.StartSetupTime).First())
                    .OrderBy(p => p.Machine)
                    .ThenBy(p => p.StartSetupTime).ToList();

                ArchiveListWindow archiveListWindow = new(parts) { Owner = p as PartsInfoWindow };
                archiveListWindow.ShowDialog();
            }
        }
        private static bool CanShowArchiveTableCommandExecute(object p) => true;
        #endregion

        #region ChangeSerialPartNormatives
        public ICommand ChangeSerialPartNormativesCommand { get; }
        private void OnChangeSerialPartNormativesCommandExecuted(object p)
        {

            var serialPart = SerialParts.FirstOrDefault(sp => sp.PartName.NormalizedPartNameWithoutComments() == SelectedPart?.PartName.NormalizedPartNameWithoutComments());
            if (serialPart == null) return;
            if (!Util.HasFeature(RemeLogFeature.AdvancedEdit))
            {
                MessageBoxWindow.Show("Нет прав на выполнение операции", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!serialPart.Operations.Any())
            {
                MessageBoxWindow.Show(
                    "У этой серийной детали нет операций, к которым могут принадлежать нормативы. Создайте операции с установками в окне 'Серийные детали'", 
                    SelectedPart?.PartName, 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
                return;
            }
            var dlg = new EditSerialPartNormativeWindow() { SerialPart = serialPart };
            if (dlg.ShowDialog() != true) return;
            if (!dlg.NewSetupNormative.HasValue && !dlg.NewProductionNormative.HasValue)
            {
                return;
            }
            foreach (var part in Parts.Where(p => 
            p.PartName.NormalizedPartNameWithoutComments() == SelectedPart?.PartName.NormalizedPartNameWithoutComments() 
            && p.Order == SelectedPart?.Order 
            && p.Setup == SelectedPart?.Setup))
            {
                if (dlg.NewSetupNormative.HasValue 
                    && dlg.NewSetupNormative.Value > 0 
                    && SelectedPart?.SetupTimePlan == part.SetupTimePlan) 
                        part.FixedSetupTimePlan = dlg.NewSetupNormative.Value;
                if (dlg.NewProductionNormative.HasValue 
                    && dlg.NewProductionNormative.Value > 0 
                    && SelectedPart?.SingleProductionTimePlan == part.SingleProductionTimePlan) 
                        part.FixedProductionTimePlan = dlg.NewProductionNormative.Value;
            }
            UpdatePartsCommand.Execute(true);
        }
        private bool CanChangeSerialPartNormativesCommandExecute(object p) => SelectedPart != null && SerialParts.FirstOrDefault(sp => sp.PartName.NormalizedPartNameWithoutComments() == SelectedPart?.PartName.NormalizedPartNameWithoutComments()) != null;
        #endregion

        #region UnlockSerialPartNormatives
        public ICommand UnlockSerialPartNormativesCommand { get; }
        private void OnUnlockSerialPartNormativesCommandExecuted(object p)
        {
 
            var serialPart = SerialParts.FirstOrDefault(sp => sp.PartName.NormalizedPartNameWithoutComments() == SelectedPart?.PartName.NormalizedPartNameWithoutComments());
            if (serialPart == null) return;
            foreach (var part in Parts.Where(p => p.PartName.NormalizedPartNameWithoutComments() == serialPart.PartName.NormalizedPartNameWithoutComments()))
            {
                part.IsUnlocked = true;
            }
            
        }
        private bool CanUnlockSerialPartNormativesCommandExecute(object p) => SelectedPart is { };
        #endregion

        #region DeletePart
        public ICommand DeletePartCommand { get; }
        private void OnDeletePartCommandExecuted(object p)
        {
            if (p is Part part && part == SelectedPart
                && MessageBoxWindow.Show($"Удалить деталь: {part.PartName}?\nДанное действие невозможно отменить.", "Удаление информации", MessageBoxButton.YesNo, MessageBoxImage.Exclamation, MessageBoxDefaultButton.No) == MessageBoxResult.Yes)
            {
                var deleteResult = part.DeletePart();
                switch (deleteResult.Status)
                {
                    case remeLog.Core.Db.DbResult.Ok:
                        _ = LoadPartsAsync();
                        Status = $"Деталь {SelectedPart.PartName} удалена из БД";
                        break;
                    case remeLog.Core.Db.DbResult.AuthError:
                        Status = "Ошибка авторизации";
                        break;
                    case remeLog.Core.Db.DbResult.Error:
                        Status = "Ошибка";
                        break;
                    case remeLog.Core.Db.DbResult.NoConnection:
                        Status = "Нет соединения с БД";
                        break;
                }
            };
        }
        private bool CanDeletePartCommandExecute(object p) => SelectedPart is { };
        #endregion

        #region UpdateParts
        public ICommand UpdatePartsCommand { get; }
        private async void OnUpdatePartsCommandExecutedAsync(object p)
        {
            if (p is bool skipAsk && !skipAsk || p is not bool)
            {
                if (MessageBoxWindow.Show("Обновить информацию?", "Вы точно уверены?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxDefaultButton.Yes) is MessageBoxResult.No) return;
            }

            InProgress = true;

            foreach (var part in Parts.Where(p => p.NeedUpdate))
            {
                var updateResult = await part.UpdatePartAsync();
                switch (updateResult.Status)
                {
                    case remeLog.Core.Db.DbResult.Ok:
                        part.NeedUpdate = false;
                        break;
                    case remeLog.Core.Db.DbResult.AuthError:
                        MessageBoxWindow.Show(updateResult.Error);
                        break;
                    case remeLog.Core.Db.DbResult.Error:
                        MessageBoxWindow.Show(updateResult.Error);
                        break;
                }
            }

            if (CurrentDayReview != null)
            {
                // "Записать" переприменяет уже принятое решение аналитика (например, после
                // правки строк) — это не новая проверка, ReviewedBy/ReviewedAt трогать не надо,
                // если только аналитик заодно не изменил AiFeedback.
                var feedbackChanged = !string.Equals(AiFeedbackText ?? string.Empty,
                    CurrentDayReview.AiFeedback ?? string.Empty, StringComparison.Ordinal);
                await SaveDayReviewInternalAsync(CurrentDayReview.Decision, CurrentDayReview.IsFullyReviewed,
                    touchReviewMeta: feedbackChanged);
            }

            _ = LoadPartsAsync();
        }
        private static bool CanUpdatePartsCommandExecute(object p) => true;
        #endregion

        #region RefreshParts
        public ICommand RefreshPartsCommand { get; }
        private void OnRefreshPartsCommandExecuted(object p) => _ = LoadPartsAsync();
        private static bool CanRefreshPartsCommandExecute(object p) => true;
        #endregion

        #region ChangeCompactView
        public ICommand ChangeCompactViewCommand { get; }
        private void OnChangeCompactViewCommandExecuted(object p) => CompactView = !CompactView;
        private static bool CanChangeCompactViewCommandExecute(object p) => true;
        #endregion

        #region ChangeRole
        public ICommand ChangeRoleCommand { get; }
        private void OnChangeRoleCommandExecuted(object p)
        {
            if (p is string str && Enum.TryParse(str, out User newRole))
            {
                ActiveColumnProfileName = null;
                ViewMode = newRole;
            }
        }
        private static bool CanChangeRoleCommandExecute(object p) => true;
        #endregion

        #region SelectColumnProfile
        public ICommand SelectColumnProfileCommand { get; }
        private void OnSelectColumnProfileCommandExecuted(object p)
        {
            if (p is string name)
            {
                ActiveColumnProfileName = name;
            }
        }
        private static bool CanSelectColumnProfileCommandExecute(object p) => true;
        #endregion

        #region ManageColumnProfiles
        public ICommand ManageColumnProfilesCommand { get; }
        private void OnManageColumnProfilesCommandExecuted(object p)
        {
            ColumnProfilesWindow window = new();
            if (p is PartsInfoWindow w) window.Owner = w;
            if (window.ShowDialog() == true && window.DataContext is ColumnProfilesWindowViewModel vm)
            {
                AppSettings.Instance.ColumnProfiles = vm.Profiles.ToList();
                AppSettings.Save();
                OnPropertyChanged(nameof(AvailableColumnProfiles));
                OnPropertyChanged(nameof(HasColumnProfiles));
                OnPropertyChanged(nameof(VisibleColumnIds));
                // Ширины могли измениться в редакторе — переприменяем их к активному профилю (code-behind).
                OnPropertyChanged(nameof(ActiveColumnProfileName));
            }
        }
        private static bool CanManageColumnProfilesCommandExecute(object p) => true;
        #endregion

        #region ChangeShowUnchecked
        public ICommand ChangeShowUncheckedCommand { get; }
        private void OnChangeShowUncheckedCommandExecuted(object p)
        {
            ViewUnchecked = !ViewUnchecked;
            EngineerConclusionFilter = ViewUnchecked ? "ожидание" : "";
        }
        private static bool CanChangeShowUncheckedCommandExecute(object p) => true;
        #endregion

        #region ChangeCalcFixed
        public ICommand ChangeCalcFixedCommand { get; }
        private void OnChangeCalcFixedCommandExecuted(object p)
        {
            Part.CalcFixed = !Part.CalcFixed;
            CalcFixed = Part.CalcFixed;
            Task.Run(() => LoadPartsAsync());
        }
        private static bool CanChangeCalcFixedCommandExecute(object p) => true;
        #endregion

        #region FillProcessComplianceAudit
        public ICommand FillProcessComplianceAuditCommand { get; }

        private async void OnFillProcessComplianceAuditCommandExecuted(object p)
        {
            if (p is not Part part) return;
            try
            {

                var path = Util.GetXlsxPath();
                if (string.IsNullOrEmpty(path))
                {
                    Status = "Выбор файла отменён";
                    return;
                }
                await Task.Run(() =>
                {
                    InProgress = true;
                    Status = Xl.FillPcaReport(part, path);
                });

            }
            catch (Exception ex)
            {
                MessageBoxWindow.Show(ex.Message);
            }
            finally { InProgress = false; }
            using (Overlay = new())
            {
                
            }
        }
        private bool CanFillProcessComplianceAuditCommandExecute(object p) => SelectedPart is { };
        #endregion
        
        #region OpenDailyReportWindow
        public ICommand OpenDailyReportWindowCommand { get; }

        public string Error => throw new NotImplementedException();

        public string this[string columnName]
        {
            get
            {
                switch (columnName)
                {
                    case nameof(FinishedCountFilter) when !Util.TryParseComparison(FinishedCountFilter, out _, out _) && !string.IsNullOrWhiteSpace(FinishedCountFilter):
                        return "Неверно указан фильтр по количеству изготовленных деталей.";

                    default:
                        return null!;
                }
            }
        }

        private void OnOpenDailyReportWindowCommandExecuted(object p)
        {
            if (FromDate != ToDate)
            {
                MessageBoxWindow.Show("Для составления суточного отчета должны быть выбраны одинаковые даты.", "Разные даты", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MachineFilters.Count(f => f.Filter) > 1)
            {
                MessageBoxWindow.Show("Для составления суточного отчета в фильтре должен быть только один станок.", "Выбрано слишком много станков.", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            else if (!MachineFilters.Any(f => f.Filter))
            {
                MessageBoxWindow.Show("Для составления суточного отчета в фильтре должен быть как минимум один станок.", "Выбрано слишком мало станков.", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (ShiftFilter.Type != ShiftType.All)
            {
                MessageBoxWindow.Show("Для составления суточного отчета не должно быть фильтра по смене.", "Лишние фильтры.", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.IsNullOrEmpty(OperatorFilter))
            {
                MessageBoxWindow.Show("Для составления суточного отчета не должно быть фильтра по оператору.", "Лишние фильтры.", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.IsNullOrEmpty(PartNameFilter))
            {
                MessageBoxWindow.Show("Для составления суточного отчета не должно быть фильтра по детали.", "Лишние фильтры.", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.IsNullOrEmpty(OrderFilter))
            {
                MessageBoxWindow.Show("Для составления суточного отчета не должно быть фильтра по маршрутному листу.", "Лишние фильтры.", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (SetupFilter != null)
            {
                MessageBoxWindow.Show("Для составления суточного отчета не должно быть фильтра по установке.", "Лишние фильтры.", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (Parts.Any(x => x.NeedUpdate))
            {
                MessageBoxWindow.Show("Есть несохраненные данные.", "Подтверждение.",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (HasErrors)
            {
                if (!Util.HasFeature(RemeLogFeature.ValidationOverride))
                {
                    MessageBoxWindow.Show("Не всё заполнено корректно.", "Предупреждение.",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var errorCount = Parts.Count(x => !string.IsNullOrEmpty(x.Error));
                var choice = MessageBoxWindow.Show(
                    $"Найдены записи без объяснения мастера.\n\n" +
                    "Всё равно открыть отчёт?",
                    "Не всё заполнено корректно",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (choice != MessageBoxResult.Yes) return;
            }

            // Совещательная сводка ИИ-замечаний (фича AiMasterCheck): в отличие от
            // валидации выше, НЕ блокирует — жёсткие ограничения остаются за
            // детерминированными проверками.
            if (HasFeatureAiMasterCheck)
            {
                var remarkParts = Parts.Where(x => x.AiCheckStatus == AiCheckStatus.Remark).ToList();
                if (remarkParts.Count > 0)
                {
                    var pendingCount = Parts.Count(x => x.AiCheckStatus == AiCheckStatus.Pending);
                    var preview = string.Join("\n", remarkParts.Take(5)
                        .Select(x => $"• {x.PartName} (уст. {x.Setup}): {x.AiCheckRemark}"));
                    var aiChoice = MessageBoxWindow.Show(
                        $"ИИ-проверка нашла замечания к комментариям мастера ({remarkParts.Count} шт.)" +
                        (pendingCount > 0 ? $", ещё {pendingCount} проверяются" : "") +
                        ":\n\n" + preview +
                        "\n\nЗамечания совещательные. Всё равно открыть отчёт?",
                        "Замечания ИИ-проверки",
                        MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (aiChoice != MessageBoxResult.Yes) return;
                }
            }

            using (Overlay = new())
            {
                var dailyInfoWindow = new DailyReportWindow((Parts, ToDate, MachineFilters.Where(mf => mf.Filter).First().Machine)) { Owner = p as PartsInfoWindow };
                dailyInfoWindow.ShowDialog();
            }
        }
        private static bool CanOpenDailyReportWindowCommandExecute(object p) => true;
        #endregion

        #region OpenMultiValueEditorCommand
        public ICommand OpenMultiValueEditorCommand { get; }

        private void OnOpenMultiValueEditorCommandExecuted(object p)
        {
            if (p is not string fieldKey) return;

            // Текущее значение для предзаполнения редактора
            string currentValue = fieldKey switch
            {
                "Operator" => OperatorFilter,
                "Order" => OrderFilter,
                _ => ChipFilters.FirstOrDefault(c => c.SqlColumn == fieldKey)?.Value ?? "",
            };

            // Отображаемое название для заголовка окна редактора
            string displayName = fieldKey switch
            {
                "Operator" => "Оператор",
                "Order" => "М/Л",
                _ when fieldKey.StartsWith("col:", StringComparison.OrdinalIgnoreCase)
                    && PartColumnMeta.Map.TryGetValue(fieldKey[4..], out var metaById)
                        => metaById.DisplayName,

                _ => PartColumnMeta.Map.Values
                         .FirstOrDefault(m => m.SqlColumn == fieldKey)?.DisplayName
                     ?? fieldKey,
            };

            var editor = new MultiValueEditorWindow(displayName, currentValue)
            {
                Owner = Application.Current.Windows.OfType<PartsInfoWindow>().FirstOrDefault(),
                ShowInTaskbar = false,
                Topmost = true,
            };

            editor.Closed += (_, _) =>
            {
                _editors.Remove(fieldKey);

                if (!editor.Resilt) return;

                switch (fieldKey)
                {
                    case "Operator":
                        OperatorFilter = editor.ResultString;
                        break;
                    case "Order":
                        OrderFilter = editor.ResultString;
                        break;

                    default:
                        ColumnMeta? meta = null;

                        if (fieldKey.StartsWith("col:"))
                        {
                            PartColumnMeta.Map.TryGetValue(fieldKey[4..], out meta);
                        }
                        else
                        {
                            meta = PartColumnMeta.Map.Values
                                .FirstOrDefault(m => m.SqlColumn == fieldKey);
                        }

                        if (meta is null)
                            break;

                        var existing = ChipFilters.FirstOrDefault(c => c.SqlColumn == fieldKey);
                        if (existing is not null) ChipFilters.Remove(existing);

                        if (!string.IsNullOrWhiteSpace(editor.ResultString))
                        {
                            // Для InMemory-колонок пересобираем предикат из всех значений
                            Func<Part, bool>? predicate = null;
                            if (meta.Kind == FilterKind.InMemory && meta.Predicate is not null)
                            {
                                var values = editor.ResultString
                                    .Split(';', StringSplitOptions.RemoveEmptyEntries
                                              | StringSplitOptions.TrimEntries)
                                    .ToArray();

                                predicate = p =>
                                    values.Any(v => meta.Predicate(p, v));
                            }

                            ChipFilters.Add(new FilterChip
                            {
                                SqlColumn = fieldKey,
                                DisplayName = meta.DisplayName,
                                Value = editor.ResultString,
                                IsInMemory = meta.Kind == FilterKind.InMemory,
                            });

                        }

                        _ = LoadPartsAsync();
                        break;
                }
            };

            _editors[fieldKey] = editor;
            editor.Show();
        }

        private static bool CanOpenMultiValueEditorCommandExecute(object p) => p is string;
        #endregion

        #region RemoveChipFilter
        public ICommand RemoveChipFilterCommand { get; }
        private void OnRemoveChipFilterExecuted(object p)
        {
            if (p is FilterChip chip)
                RemoveChipFilter(chip);
        }

        #endregion

        #region EditChipFilter
        public ICommand EditChipFilterCommand { get; }
        public void EditChipFilter(FilterChip chip)
        {
            var editor = new MultiValueEditorWindow(chip.DisplayName, chip.Value);
            if (Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) is { } owner)
                editor.Owner = owner;
            editor.ShowDialog();
            if (!editor.Resilt) return;
            if (string.IsNullOrEmpty(editor.ResultString))
            {
                RemoveChipFilter(chip);
                return;
            }
            if (editor.ResultString == chip.Value) return;
            var idx = ChipFilters.IndexOf(chip);
            if (idx < 0) return;
            ChipFilters[idx] = new FilterChip
            {
                SqlColumn = chip.SqlColumn,
                DisplayName = chip.DisplayName,
                Value = editor.ResultString,
                IsInMemory = chip.IsInMemory
            };
            _ = LoadPartsAsync();
        }

        #endregion

        #region ClearChipFilters
        public ICommand ClearChipFiltersCommand { get; }

        #endregion

        #region MarkDayOk
        public ICommand MarkDayOkCommand { get; private set; } = null!;
        private async void OnMarkDayOkExecuted(object _)
            => await SaveDayReviewInternalAsync(AnalystDecision.Ok, isFullyReviewed: true);
        private bool CanMarkDayOkExecute(object _) => IsSingleMachineSingleDay && !Parts.Any(p => p.NeedUpdate);

        #endregion

        #region MarkDayEscalated
        public ICommand MarkDayEscalatedCommand { get; private set; } = null!;
        private async void OnMarkDayEscalatedExecuted(object _)
            => await SaveDayReviewInternalAsync(AnalystDecision.Escalated, isFullyReviewed: false);
        private bool CanMarkDayEscalatedExecute(object _) => IsSingleMachineSingleDay && !Parts.Any(p => p.NeedUpdate);
        #endregion

        #region SnapParts
        public ICommand SnapPartsCommand { get; private set; } = null!;

        /// <summary> Максимальный зазор/перекрытие для автослепки, мин. </summary>
        private const int SnapMaxGapMinutes = 180;

        private static string PluralizeSnap(int n)
        {
            var m10 = n % 10;
            var m100 = n % 100;
            var word = m10 == 1 && m100 != 11 ? "слепка"
                : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? "слепки" : "слепок";
            return $"{n} {word}";
        }

        private bool CanSnapPartsExecute(object _)
            => IsSingleMachineSingleDay && Parts.Count > 1
               && !Parts.Any(p => p.NeedUpdate) && !InProgress;

        // Слепка деталей встык в рамках смены: начало следующей детали тянется
        // к концу предыдущей. День и ночь не смешиваются, к границам смены
        // не тянем. Каждая слепка пишется в part_snap_log (кто/когда/что).
        private async void OnSnapPartsExecuted(object _)
        {
            if (!IsSingleMachineSingleDay) return;
            if (Parts.Any(p => p.NeedUpdate))
            {
                MessageBoxWindow.Show(
                    "Сначала запишите несохранённые правки («Записать»).",
                    "Слепка деталей", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var machine = MachineFilters.FirstOrDefault(f => f.Filter)?.Machine
                          ?? PartsInfo.Machine;
            var date = FromDate.Date;

            var snap = PartSnapper.Plan(Parts, SnapMaxGapMinutes);
            var plans = snap.Plans;
            var skipped = snap.SkippedInvariant;
            var skippedDowntime = snap.SkippedPartial;

            if (plans.Count == 0)
            {
                MessageBoxWindow.Show(
                    "Стыков для слепки нет: детали уже стоят встык или зазоры больше 180 мин."
                    + (skipped > 0 ? $" Пропущено стыков: {skipped} (конец предыдущей позже начала изготовления следующей)." : "")
                    + (skippedDowntime > 0 ? $" Пропущено стыков: {skippedDowntime} (сдвиг увёл бы частичную наладку в минус)." : ""),
                    "Слепка деталей", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var preview = new StringBuilder();
            preview.AppendLine($"Станок {machine}, {date:dd.MM.yyyy}.");
            preview.AppendLine("Начало следующей детали тянется к концу предыдущей:");
            foreach (var (_, next, oldStart, newStart, gap, oldPartial, newPartial) in plans)
                preview.AppendLine($"• {next.PartName} (Уст.{next.Setup}): {oldStart:HH:mm} → {newStart:HH:mm} (зазор {gap} мин)"
                    + (Math.Abs(newPartial - oldPartial) > 1e-9 ? $", частичная {oldPartial:0.#} → {newPartial:0.#} мин" : ""));
            if (skipped > 0)
                preview.AppendLine($"Пропущено стыков: {skipped} (конец предыдущей позже начала изготовления следующей).");
            if (skippedDowntime > 0)
                preview.AppendLine($"Пропущено стыков: {skippedDowntime} (сдвиг увёл бы частичную наладку в минус).");
            var answer = MessageBoxWindow.Show(
                preview.ToString(), "Слепка деталей",
                MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxDefaultButton.No);
            if (answer != MessageBoxResult.Yes) return;

            var by = Environment.UserName;
            var at = DateTime.Now;
            var logs = new List<PartSnapLog>();
            foreach (var (prev, next, oldStart, newStart, gap, _, newPartial) in plans)
            {
                next.StartSetupTime = newStart; // сеттер ставит NeedUpdate
                if (Math.Abs(next.PartialSetupTime - newPartial) > 1e-9)
                    next.PartialSetupTime = newPartial;
                logs.Add(new PartSnapLog
                {
                    Machine = machine,
                    ShiftDate = date,
                    Shift = next.Shift,
                    PrevPartGuid = prev.Guid,
                    NextPartGuid = next.Guid,
                    NextPartName = next.PartName,
                    OldStartSetupTime = oldStart,
                    NewStartSetupTime = newStart,
                    GapMinutes = gap,
                    SnappedBy = by,
                    SnappedAt = at,
                });
            }

            Status = "Сохранение слепки...";
            if (!await SaveDirtyPartsAsync()) { Status = string.Empty; return; }

            var logResult = await Database.SavePartSnapLogsAsync(logs);
            await LoadPartsAsync();
            Status = logResult.IsOk
                ? $"Слепка готова: {PluralizeSnap(plans.Count)}"
                : $"Слепка сохранена ({PluralizeSnap(plans.Count)}), но история не записалась: {logResult.Error}";
            await Task.Delay(2500);
            Status = string.Empty;
        }
        #endregion

        #region TogglePartFlag
        public ICommand TogglePartFlagCommand { get; private set; } = null!;
        private void OnTogglePartFlagExecuted(object p)
        {
            if (p is Part part)
            {
                part.IsFlagged = !part.IsFlagged;
                OnPropertyChanged(nameof(FlaggedPartsCount));
                OnPropertyChanged(nameof(HasFlaggedParts));
            }
        }

        private bool CanTogglePartFlagExecute(object _) => IsSingleMachineSingleDay && !Parts.Any(p => p.NeedUpdate) && Util.HasFeature(RemeLogFeature.Ai);
        #endregion

        #region AnalyzeDay
        public ICommand AnalyzeDayCommand { get; private set; } = null!;

        private async void OnAnalyzeDayExecuted(object _)
        {
            if (!Util.HasFeature(RemeLogFeature.Ai) || !IsAiAvailable) return;

            if (AiInProgress)
            {
                _aiCancellationTokenSource?.Cancel();
                Status = "AI анализ отменён";
                return;
            }

            // Некорректные данные ИИ на проверку не отдаём — тот же обход, что у
            // перехода к суточному отчёту: фича ValidationOverride + подтверждение.
            if (!ConfirmAnalyzeWithValidationErrors()) return;

            var machine = MachineFilters.FirstOrDefault(f => f.Filter)?.Machine
                          ?? PartsInfo.Machine;

            // Снимок того, что уходит на анализ. Решение по итогам ИИ обязано попасть
            // в ревью ИМЕННО этих суток: кнопка «Все даты» (SetAllDateCommand) двигает
            // FromDate на 2023-01-01, и если между анализом и нажатием «Принять» фильтр
            // уехал, ревью записывалось на пустой день — кейс Quaser MV134 02.10.2026
            // (анализ за 01.09, в базе ревью за 2023-01-01, 0 записей за этот день).
            var analyzedDate = FromDate.Date;

            AiResult = null;
            AiInProgress = true;
            ThinkingThoughts = "";
            IsThoughtPanelOpen = true;

            _aiCancellationTokenSource?.Cancel();
            _aiCancellationTokenSource?.Dispose();
            _aiCancellationTokenSource = new CancellationTokenSource();
            var ct = _aiCancellationTokenSource.Token;

            var thinkingStarted = false;
            var progress = new Progress<AiProgress>(update =>
            {
                if (update.Kind == AiProgressKind.Queue)
                {
                    // Транзитный статус: заменяет текст и сбрасывает флаг —
                    // следующий чанк размышлений начнёт поле заново, без склейки.
                    ThinkingThoughts = update.Text;
                    Status = update.Text;
                    thinkingStarted = false;
                }
                else if (update.Kind == AiProgressKind.Tool)
                {
                    // Шаг агента — отдельной строкой, видно что делает и что проверяет.
                    // Флаг взводим: следующие чанки мыслей должны дописаться, а не
                    // заменить след (иначе первая же мысль сотрёт его).
                    // Завершающий перенос: мысли начнутся с новой строки, а не встык.
                    ThinkingThoughts += (ThinkingThoughts.Length > 0 ? "\n" : "") + "⚙ " + update.Text + "\n";
                    thinkingStarted = true;
                    Status = update.Text;
                }
                else
                {
                    ThinkingThoughts = thinkingStarted ? ThinkingThoughts + update.Text : update.Text;
                    thinkingStarted = true;
                    Status = "ИИ думает...";
                }
            });

            try
            {
                var result = await _aiClient.AnalyzeAsync(
                    machine, analyzedDate, Parts,
                    thinkingProgress: progress,
                    ct: ct);

                if (ct.IsCancellationRequested)
                {
                    Status = "ИИ анализ отменён";
                    return;
                }

                AiResult = result;

                // Агентский вердикт пишется в БД наравне со штатным: этап сравнения
                // завершён (1012 дней, recall ~99.8%), аналитики работают от одного
                // вердикта. Трассируемость — суффикс @agent в версии промпта + тег.
                // Теневой режим удалён 25.09.2026 (isAgentResult оставлен только для статуса).
                var isAgentResult = result.PromptVersion?.EndsWith("@agent") == true;

                if (CurrentDayReview != null && !result.HasError)
                {
                    var saveResult = await Database.SaveAiAnalysisAsync(
                        CurrentDayReview.Id, result, AppSettings.AiModel, AiThinkingEnabled);
                    if (saveResult.IsOk)
                    {
                        var refreshed = await Database.GetDayReviewAsync(machine, analyzedDate);
                        if (refreshed != null)
                            CurrentDayReview = refreshed;
                    }
                    else
                    {
                        // Не бросаем диалог — сам анализ прошёл успешно (AiResult уже показан),
                        // но CurrentDayReview.AiAnalyzedAt останется от предыдущего прогона, если
                        // не сообщить об этом явно (симптом «дата анализа не обновляется»).
                        Status = $"ИИ подумал, но результат не сохранён в БД: {saveResult.Error}";
                        await ShowAiVerdictAsync(result, machine, analyzedDate, canChangeDayStatus: false);
                        return;
                    }
                }
                Status = isAgentResult ? "ИИ (агент) подумал" : "ИИ подумал";
                await ShowAiVerdictAsync(result, machine, analyzedDate, canChangeDayStatus: true);
            }
            catch (OperationCanceledException)
            {
                Status = "ИИ анализ отменён";
            }
            finally
            {
                AiInProgress = false;
                IsThoughtPanelOpen = false;
                _aiCancellationTokenSource?.Dispose();
                _aiCancellationTokenSource = null;
            }
        }

        /// <summary>
        /// Гейт валидации для ИИ-анализа: запрещает отдавать ИИ некорректные данные
        /// (есть ошибки валидации). Обход — как у перехода к суточному отчёту
        /// (см. OnOpenDailyReportWindowCommandExecuted): фича ValidationOverride
        /// (для разбора старых записей), после подтверждения — анализ всё равно идёт.
        /// </summary>
        private bool ConfirmAnalyzeWithValidationErrors()
        {
            if (!HasErrors) return true;

            if (!Util.HasFeature(RemeLogFeature.ValidationOverride))
            {
                MessageBoxWindow.Show("Не всё заполнено корректно.", "Предупреждение.",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var errorCount = Parts.Count(x => !string.IsNullOrEmpty(x.Error));
            var choice = MessageBoxWindow.Show(
                $"Найдены записи с ошибками валидации ({errorCount} шт.).\n\n" +
                "Всё равно запустить ИИ-анализ?",
                "Не всё заполнено корректно",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            return choice == MessageBoxResult.Yes;
        }

        /// <summary>
        /// Диалог вердикта ИИ (этап 2 «Тестовый запуск»): вместо попапа на каждую строку —
        /// один диалог с оценкой, объяснением, чекбоксами кандидатов на исключение из К1
        /// и предложением зафиксировать статус проверки СГТ. При согласии выполняет
        /// accept-путь: исключения → сохранение строк → DayReview (+ИИ) → «Проверено техотделом».
        /// </summary>
        private async Task ShowAiVerdictAsync(
            AiAnalysisResult result, string machine, DateTime analyzedDate, bool canChangeDayStatus)
        {
            var suggestions = AiExcludeSuggestion.ParseMany(
                result.SuggestExcludeFromReports, result.Explanation);

            Part? FindPart(AiExcludeSuggestion s) => AiExcludeSuggestion.FindRow(Parts, s);
            static string DisplayOf(AiExcludeSuggestion s) => $"{s.PartName} | М/Л: {s.Order} | Уст.{s.Setup}";

            var matched = new List<(Part Part, string Reason)>();
            var unmatched = new List<string>();
            foreach (var s in suggestions)
            {
                var part = FindPart(s);
                if (part == null)
                    unmatched.Add(DisplayOf(s));
                else
                    matched.Add((part, s.Reason));
            }

            // Строки, которые ИИ предлагает отметить проблемными (hard + непониженные soft).
            var aiFlagged = AiExcludeSuggestion.ParseMany(result.FlaggedParts, string.Empty)
                .Select(s => FindPart(s))
                .OfType<Part>()
                .Distinct()
                .ToList();
            var flaggedPreview = aiFlagged
                .Select(p => $"{p.PartName} | М/Л: {p.Order} | Уст.{p.Setup} | {p.StartSetupTime:dd.MM HH:mm}")
                .ToList();

            string alreadyReviewedHint = string.Empty;
            if (canChangeDayStatus && CurrentDayReview != null)
            {
                alreadyReviewedHint =
                    $"День уже имеет решение СГТ: {CurrentDayReview.Decision.ToDisplayString()} " +
                    $"({CurrentDayReview.ReviewedBy}, {CurrentDayReview.ReviewedAt:dd.MM.yy HH:mm}) — " +
                    "принятие перезапишет его.";
            }

            var owner = Application.Current?.Windows.OfType<PartsInfoWindow>()
                            .FirstOrDefault(w => ReferenceEquals(w.DataContext, this))
                        ?? Application.Current?.MainWindow;
            // Вопросы к отчёту мастера — отдельным блоком диалога, из «Признаков» убираются,
            // чтобы не дублироваться (в Signals сервера они остаются для персистентности).
            var shiftIssues = result.ShiftReportIssues ?? Array.Empty<string>();
            var otherSignals = (result.Signals ?? Array.Empty<string>())
                .Except(shiftIssues)
                .ToList();
            var dlg = new AiVerdictDialogWindow(
                machine, analyzedDate,
                result.RequiresReview, result.Confidence, result.Explanation ?? string.Empty,
                otherSignals,
                matched, unmatched,
                canChangeDayStatus,
                alreadyReviewedHint,
                flaggedPreview,
                shiftIssues,
                result.ShiftReportSummary ?? Array.Empty<string>(),
                result.EscalatedByShiftReport,
                result.EscalatedByData)
            {
                Owner = owner
            };
            dlg.ShowDialog();

            if (dlg.DialogResult != true)
            {
                // Отказ: решение и строки не трогаем, сохраняем только отзыв (если есть что обновлять).
                if (!string.IsNullOrWhiteSpace(dlg.Feedback) && CurrentDayReview != null)
                {
                    AiFeedbackText = dlg.Feedback;
                    await SaveDayReviewInternalAsync(
                        CurrentDayReview.Decision, CurrentDayReview.IsFullyReviewed,
                        touchReviewMeta: false, shiftDate: analyzedDate);
                }
                return;
            }

            // 1. Исключения из К1 по выбранным чекбоксам.
            var accepted = dlg.AcceptedParts;
            foreach (var part in accepted)
                part.ExcludeFromReports = true;
            if (!await SaveDirtyPartsAsync()) return;

            // 2. Отзыв + аудиторский тег модели/промпта.
            if (!string.IsNullOrWhiteSpace(dlg.Feedback))
                AiFeedbackText = dlg.Feedback;
            AppendAiAuditTag(result);

            // 3. Флаги строк расставляет сам ИИ, человек только согласился/не согласился:
            //    OK — все строки однозначны, флаги снимаются; эскалация — ИИ отмечает
            //    свои строки, ручные пометки аналитика сохраняются (объединение).
            //    Только потом пишем решение в БД (SaveDayReviewInternalAsync персистит флаги).
            //    Несогласие — то же противоположное действие, но с обязательным отзывом.
            AnalystDecision? dayDecision = dlg.ChosenAction switch
            {
                AiVerdictAction.ConfirmOk or AiVerdictAction.OppositeOk => AnalystDecision.Ok,
                AiVerdictAction.Escalate or AiVerdictAction.OppositeEscalate => AnalystDecision.Escalated,
                _ => null,
            };
            var againstAiVerdict = dlg.ChosenAction is AiVerdictAction.OppositeOk or AiVerdictAction.OppositeEscalate;
            if (againstAiVerdict && string.IsNullOrWhiteSpace(dlg.Feedback))
            {
                MessageBoxWindow.Show("Для несогласия с вердиктом ИИ нужен отзыв.",
                    "Нужен отзыв", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (dayDecision == AnalystDecision.Ok)
            {
                foreach (var part in Parts.Where(p => p.IsFlagged))
                    part.IsFlagged = false;
                OnPropertyChanged(nameof(FlaggedPartsCount));
                OnPropertyChanged(nameof(HasFlaggedParts));

                if (!MarkShiftsCheckedAsReviewed(machine, analyzedDate)) return;
                await SaveDayReviewInternalAsync(AnalystDecision.Ok, isFullyReviewed: true,
                    acceptedFromAi: true, againstAiVerdict: againstAiVerdict,
                    acceptedAiResult: result, shiftDate: analyzedDate);
            }
            else if (dayDecision == AnalystDecision.Escalated)
            {
                foreach (var part in aiFlagged.Where(p => !p.IsFlagged))
                    part.IsFlagged = true;
                OnPropertyChanged(nameof(FlaggedPartsCount));
                OnPropertyChanged(nameof(HasFlaggedParts));

                await SaveDayReviewInternalAsync(AnalystDecision.Escalated, isFullyReviewed: false,
                    acceptedFromAi: true, againstAiVerdict: againstAiVerdict,
                    acceptedAiResult: result, shiftDate: analyzedDate);
            }
            else if (CurrentDayReview != null)
            {
                // Только исключения (+отзыв): решение дня не меняем, но отзыв persist'им.
                // Принятый вердикт ИИ тоже — иначе он остался бы от предыдущего прогона.
                await SaveDayReviewInternalAsync(
                    CurrentDayReview.Decision, CurrentDayReview.IsFullyReviewed,
                    touchReviewMeta: false, acceptedAiResult: result, shiftDate: analyzedDate);
                Status = accepted.Count > 0
                    ? $"Исключено из отчётов: {accepted.Count}"
                    : "Вердикт ИИ принят без изменений";
                await Task.Delay(2500);
                Status = string.Empty;
            }
            else
            {
                // Решения дня нет (аналитик взял только исключения) — строки ревью тоже
                // нет, вердикт ИИ сохранять некуда: Decision NOT NULL, создавать запись
                // без решения аналитика нельзя. На метрики это не влияет — без решения
                // сутки в ai_day_reviews не попадают.
                Status = accepted.Count > 0
                    ? $"Исключено из отчётов: {accepted.Count}"
                    : "Вердикт ИИ принят без изменений";
                await Task.Delay(2500);
                Status = string.Empty;
            }
        }

        /// <summary> Молча сохраняет изменённые строки (accept-путь вердикта ИИ). </summary>
        /// <returns> false — были ошибки сохранения, статус дня менять нельзя. </returns>
        private async Task<bool> SaveDirtyPartsAsync()
        {
            foreach (var part in Parts.Where(p => p.NeedUpdate).ToList())
            {
                var updateResult = await part.UpdatePartAsync();
                if (updateResult.Status == remeLog.Core.Db.DbResult.Ok)
                {
                    part.NeedUpdate = false;
                }
                else
                {
                    MessageBoxWindow.Show(
                        $"Не удалось сохранить строку {part.PartName} (Уст.{part.Setup}):{Environment.NewLine}{updateResult.Error}",
                        "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Проставляет «Проверено техотделом» (cnc_shifts.IsChecked) на обе смены суток —
        /// тот флаг, по которому работает календарь проверки. Содержимое отчётов мастера
        /// не трогается: читаем запись и меняем только IsChecked.
        /// </summary>
        /// <returns> false — отчёта нет или ошибка БД, день закрывать нельзя. </returns>
        private bool MarkShiftsCheckedAsReviewed(string machine, DateTime date)
        {
            foreach (var shiftType in new[] { ShiftType.Day, ShiftType.Night })
            {
                var shiftName = shiftType == ShiftType.Day ? "дневной" : "ночной";
                var read = Database.ReadShiftInfo(new ShiftInfo(date, shiftType, machine));
                if (!read.IsOk)
                {
                    MessageBoxWindow.Show(
                        $"Не удалось прочитать отчёт {shiftName} смены: {read.Error}",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
                if (read.Value is not { Count: 1 })
                {
                    MessageBoxWindow.Show(
                        "Нет суточного отчёта мастера за эти сутки — откройте суточный отчёт " +
                        "и поставьте «Проверено техотделом» вручную.",
                        "Нет отчёта", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                read.Value[0].IsChecked = true;
                var write = Database.WriteShiftInfo(read.Value[0]);
                if (!write.IsOk)
                {
                    MessageBoxWindow.Show(
                        $"Не удалось отметить {shiftName} смену как проверенную: {write.Error}",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
            return true;
        }

        /// <summary> Аудиторский тег принятого вердикта: модель и промпт — для разбора расхождений.
        /// Агентский путь помечается (суффикс @agent в версии промпта). </summary>
        private void AppendAiAuditTag(AiAnalysisResult result)
        {
            var agentMark = result.PromptVersion?.EndsWith("@agent") == true ? " агент" : "";
            var tag = $"[ИИ {AppSettings.AiModel}/{result.PromptVersion}{agentMark}]";
            if ((AiFeedbackText ?? string.Empty).Contains("[ИИ")) return;
            AiFeedbackText = string.IsNullOrWhiteSpace(AiFeedbackText)
                ? tag
                : AiFeedbackText.TrimEnd() + " " + tag;
        }

        // Пустые сутки тоже анализируются: отсутствие записей само по себе ок,
        // решают отчёты мастера (та же логика, что пакетное автозакрытие пустых).
        private bool CanAnalyzeDayExecute(object _) =>
            Util.HasFeature(RemeLogFeature.Ai) && IsAiAvailable && IsSingleMachineSingleDay && !InProgress;

        #endregion

        #region ShowAiExplanation
        public ICommand ShowAiExplanationCommand { get; private set; } = null!;

        private void OnShowAiExplanationExecuted(object _)
        {
            IsAiExplanationPopupOpen = true;
        }

        private bool CanShowAiExplanationExecute(object _) =>
            AiResult != null;

        #endregion

        public void PushValueToEditor(string fieldKey, string value)
        {
            if (!_editors.TryGetValue(fieldKey, out var existing) || !existing.IsVisible)
            {
                OpenMultiValueEditorCommand.Execute(fieldKey);
            }

            if (_editors.TryGetValue(fieldKey, out var editor))
            {
                if (!editor.Values.Any(v =>
                        string.Equals(v.Value, value, StringComparison.OrdinalIgnoreCase)))
                {
                    editor.Values.Add(new() { Value = value });
                }
            }
        }



        private async Task<bool> LoadPartsAsync(bool first = false)
        {
            if (lockUpdate) return false;
            // Перезагрузка пересоздаёт коллекцию — эфемерные ИИ-статусы строк пропадают
            // by design (вердикт совещательный и нигде не сохраняется).
            CancelAllAiChecks();
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource = new();
            var cancellationToken = _cancellationTokenSource.Token;
            await semaphoreSlim.WaitAsync(cancellationToken);
            try
            {
                InProgress = true;
                Status = "Получение информации...";
                if (AppSettings.Instance.ConnectionString == null)
                {
                    Status = "Не настроено соединение с БД";
                    return false;
                }
                await Util.UpdateAppSettingsAsync();
                if (!first) await Task.Delay(1000, cancellationToken);

                if (UseMockData && !Core.DomainSettings.DemoMode)
                {
                    Parts = await Util.GenerateMockPartsAsync();
                    InProgress = false;
                    return true;
                }

                var isValidFinishedCountFilter = Util.TryParseComparison(FinishedCountFilter, out string finishedCountOperator, out int finishedCountValue);
                var isValidTotalCountFilter = Util.TryParseComparison(TotalCountFilter, out string totalCountOperator, out int totalCountValue);
                var partNameStartStar = PartNameFilter.StartsWith('*');

                if (!MachineFilters.Any(mf => mf.Filter == true))
                {
                    Application.Current.Dispatcher.Invoke(() => { Parts.Clear(); });
                    InProgress = false;
                    return false;
                }

                // Демо: SQL недоступен — те же критерии, фильтрация в памяти.
                var tempParts = Core.DomainSettings.DemoMode
                    ? await Database.ReadPartsWithCriteria(BuildCriteria(), cancellationToken)
                    : await Database.ReadPartsWithConditions(BuildConditions(), cancellationToken);
                tempParts = ApplyInMemoryFilters(tempParts);

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Parts.Clear();
                    Parts = new(tempParts);
                });
                InProgress = false;
                await LoadDayReviewAsync();
                LoadExistingFlags();
                ScheduleInitialAiChecks();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                var message = ex.Message;
#if DEBUG
                message = message += ex.StackTrace;
#endif
                MessageBoxWindow.Show($"{message}");
                InProgress = false;
                return false;
            }
            finally
            {
                semaphoreSlim.Release();
                Status = "";
            }
        }

        /// <summary>
        /// Собирает критерии фильтрации (используются и для SQL, и для демо).
        /// </summary>
        private PartsFilterCriteria BuildCriteria()
        {
            var finishedCountFilter = Util.TryParseComparison(FinishedCountFilter, out var finishedOp, out var finishedVal)
                ? (finishedOp, finishedVal) : ((string, int)?)null;
            var totalCountFilter = Util.TryParseComparison(TotalCountFilter, out var totalOp, out var totalVal)
                ? (totalOp, totalVal) : ((string, int)?)null;

            return new PartsFilterCriteria(
                FromDate, ToDate, ShiftFilter,
                OperatorFilter, PartNameFilter, OrderFilter,
                EngineerConclusionFilter, EngineerCommentFilter,
                finishedCountFilter, totalCountFilter, SetupFilter,
                SerialPartsFilter.Type,
                SerialParts.Select(sp => sp.PartName.NormalizedPartNameWithoutComments()).ToList(),
                MachineFilters.Where(mf => mf.Filter).Select(m => m.Machine).ToList(),
                ChipFilters.ToList());
        }

        /// <summary>
        /// Собирает строку запроса
        /// </summary>
        private string BuildConditions() => PartsFilterService.BuildConditions(BuildCriteria());

        /// <summary>
        /// Собирает строку запроса для получения списка всех изготовлений на всех станках для этой детали
        /// </summary>
        /// <returns></returns>
        private string BuildConditionsForPartForAllTime(string partName) =>
            PartsFilterService.BuildConditionsForPartForAllTime(partName);

        /// <summary>
        /// Подтверждение изменения свойства, которое повлечет изменение списка.
        /// Сразу возвращает true, если нет незаписанных позиций, либо спрашивает подтверждения.
        /// </summary>
        /// <returns></returns>
        private bool CanBeChanged()
        {
            if (Parts.Any(x => x.NeedUpdate)
                && MessageBoxWindow.Show("Есть незаписанные изменения. При продолжении они будут утеряны.", "Обновить список деталей?",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxDefaultButton.Cancel) == MessageBoxResult.Cancel) return false;
            return true;
        }

        public static string FormatSearchInput(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            if (input.StartsWith('*') && input.EndsWith('*')) return input.Replace('*', '%');
            if (input.StartsWith('=')) return input[1..];
            if (input.StartsWith('*')) return $"%{input[1..]}";
            if (input.EndsWith('*')) return $"{input[..^1]}%";
            return $"%{input}%";
        }

        private static IEnumerable<TimelineSource> BuildMachineTimelineSources(Signal signal, Machine machine, DateTime start, DateTime end, IProgress<string>? progress = null)
        {
            TimelineSource? FromSignal(string displayName, string signalId, Func<string, string>? mapper = null)
            {
                if (string.IsNullOrEmpty(signalId)) return null;
                return TimelineSource.FromSignal(
                    displayName,
                    () => signal.GetSignalAsync(signalId, SignalType.ByTime, Ordering.Asc, start, end, progress: progress), valueMapper: mapper);
            }

            var sources = new (string DisplayName, string SignalId, Func<string, string>? Mapper)[]
            {
                // Общие
                ("№ УП",                machine.WnNcProgramNameSignal,           null),
                ("Имя УП",              machine.WnNcPartNameSignal,              null),
                ("Счётчик",             machine.WnCounterSignal,                 null),
                ("Режим ЧПУ",           machine.WnNcModeSignal,                  machine.Name.Contains("XH6300") ? SignalMappers.NcModeSiemens : SignalMappers.NcModeFanuc),
                ("УП выполняется",      machine.WnProgramRunningSignal,          SignalMappers.Bool),
                ("Останов по M0",       machine.WnStopSignal,                    SignalMappers.Bool),
                ("Останов по М1",       machine.WnOpStopSignal,                  SignalMappers.Bool),
                ("М-код",               machine.WnMcodeSignal,                   null),
                ("Движение",            machine.WnGMoveSignal,                   null),
                ("Цикл",                machine.WnGCycleSignal,                  null),
                ("G-код",               machine.WnGcodeSignal,                   null),
                ("Feed Hold",           machine.WnFeedHoldSignal,                SignalMappers.Bool),
                ("Single Block",        machine.WnSBKSignal,                     SignalMappers.Bool),
                ("Dry Run",             machine.WnDryRunSignal,                  SignalMappers.Bool),
                ("MSTLK",               machine.WnMSTLKSignal,                   SignalMappers.Bool),
                ("MLK",                 machine.WnMLKSignal,                     SignalMappers.Bool),
                ("N",                   machine.WnCurrentBlockNumberSignal,      null),
                ("Текст кадра",         machine.WnCurrentBlockTextSignal,        null),
                ("СК",                  machine.WnCurrentCSSignal,               null),
                ("Плоскость",           machine.WnCurrentPlaneSignal,            null),
                // Коррекции                                                     
                ("Корр. подачи",        machine.WnFeedMultiplierSignal,          null),
                ("Корр. ускор. хода",   machine.WnRapidMultiplierSignal,         null),
                ("Корр. шпинделя 1",    machine.WnSpindleSpeed1MultiplierSignal, null),
                ("Корр. шпинделя 2",    machine.WnSpindleSpeed2MultiplierSignal, null),
                // Актуальные значения                                           
                ("Обор. 1 шпинделя",    machine.WnActSpindle1SpeedSignal,        null),
                ("Обор. 2 шпинделя",    machine.WnActSpindle2SpeedSignal,        null),
                ("Скорость резания",    machine.WnActCutSpeedSignal,             null),
                ("Подача мм/мин",       machine.WnActFeedPerMinSignal,           null),
                ("Подача мм/об",        machine.WnActFeedPerRevSignal,           null),
                // Инструмент                                                    
                ("Инструмент",          machine.WnToolSignal,                    null),
                ("Корр. H",             machine.WnToolHSignal,                   null),
                ("Корр. D",             machine.WnToolDSignal,                   null),
                ("Вектор инстр.",       machine.WnToolVectorSignal,              null),
                ("Геом. R",             machine.WnToolGeomRSignal,               null),
                ("Геом. X",             machine.WnToolGeomXSignal,               null),
                ("Геом. Y",             machine.WnToolGeomYSignal,               null),
                ("Геом. Z",             machine.WnToolGeomZSignal,               null),
                ("Износ R",             machine.WnToolWearRSignal,               null),
                ("Износ X",             machine.WnToolWearXSignal,               null),
                ("Износ Y",             machine.WnToolWearYSignal,               null),
                ("Износ Z",             machine.WnToolWearZSignal,               null),
                // Абсолютные координаты                                         
                ("Abs X",               machine.WnAbsXSignal,                    null),
                ("Abs Y",               machine.WnAbsYSignal,                    null),
                ("Abs Z",               machine.WnAbsZSignal,                    null),
                ("Abs ZA",              machine.WnAbsZASignal,                   null),
                ("Abs B",               machine.WnAbsBSignal,                    null),
                ("Abs C",               machine.WnAbsCSignal,                    null),
                ("Abs W",               machine.WnAbsWSignal,                    null),
                // Относительные координаты                                      
                ("Rel X",               machine.WnRelXSignal,                    null),
                ("Rel Y",               machine.WnRelYSignal,                    null),
                ("Rel Z",               machine.WnRelZSignal,                    null),
                ("Rel ZA",              machine.WnRelZASignal,                   null),
                ("Rel B",               machine.WnRelBSignal,                    null),
                ("Rel C",               machine.WnRelCSignal,                    null),
                ("Rel W",               machine.WnRelWSignal,                    null),
                // Машинные координаты                                           
                ("Mach X",              machine.WnMachXSignal,                   null),
                ("Mach Y",              machine.WnMachYSignal,                   null),
                ("Mach Z",              machine.WnMachZSignal,                   null),
                ("Mach ZA",             machine.WnMachZASignal,                  null),
                ("Mach B",              machine.WnMachBSignal,                   null),
                ("Mach C",              machine.WnMachCSignal,                   null),
                ("Mach W",              machine.WnMachWSignal,                   null),
                // Аварии и нагрузки                                             
                ("Авария",              machine.WnAlarmMessageSignal,            null),
                ("Нагрузка X",          machine.WnLoadXSignal,                   null),
                ("Нагрузка Y",          machine.WnLoadYSignal,                   null),
                ("Нагрузка Z",          machine.WnLoadZSignal,                   null),
                ("Нагрузка C",          machine.WnLoadCSignal,                   null),
                ("Нагрузка B",          machine.WnLoadBSignal,                   null),
                ("Нагрузка W",          machine.WnLoadWSignal,                   null),
                ("Нагрузка 1 шпинд.",   machine.WnLoadSpindle1Signal,            null),
                ("Нагрузка 2 шпинд.",   machine.WnLoadSpindle2Signal,            null),
            };

            foreach (var (displayName, signalId, mapper) in sources)
            {
                var source = FromSignal(displayName, signalId, mapper);
                if (source != null)
                    yield return source;
            }
        }

        /// <summary>
        /// Добавляет чип-фильтр или объединяет со существующим для того же столбца
        /// (через точку с запятой, как MultiValueEditor).
        /// </summary>
        public void AddChipFilter(string sqlColumn, string displayName, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            var existing = ChipFilters.FirstOrDefault(c => c.SqlColumn == sqlColumn);
            if (existing != null)
            {
                // Не добавляем дубли
                var existingValues = existing.Value
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (existingValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                    return;

                ChipFilters.Remove(existing);
                ChipFilters.Add(new FilterChip
                {
                    SqlColumn = sqlColumn,
                    DisplayName = displayName,
                    Value = $"{existing.Value};{value}",
                });
            }
            else
            {
                ChipFilters.Add(new FilterChip
                {
                    SqlColumn = sqlColumn,
                    DisplayName = displayName,
                    Value = value,
                });
            }

            _ = LoadPartsAsync();
        }

        /// <summary>
        /// Добавляет чип-фильтр. Если meta.Kind == InMemory - фильтр применяется
        /// после загрузки в памяти, не в SQL.
        /// </summary>
        public void AddChipFilter(ColumnMeta meta, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            var existing = ChipFilters.FirstOrDefault(c => c.DisplayName == meta.DisplayName);

            if (existing is not null)
            {
                // Не добавляем дубли
                var existingValues = existing.Value.Split(';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (existingValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                    return;

                ChipFilters.Remove(existing);

                ChipFilters.Add(new FilterChip
                {
                    SqlColumn = meta.SqlColumn,
                    DisplayName = meta.DisplayName,
                    Value = $"{existing.Value};{value}",
                    IsInMemory = meta.Kind == FilterKind.InMemory,
                });
            }
            else
            {
                ChipFilters.Add(new FilterChip
                {
                    SqlColumn = meta.SqlColumn,
                    DisplayName = meta.DisplayName,
                    Value = value,
                    IsInMemory = meta.Kind == FilterKind.InMemory,
                });
            }

            _ = LoadPartsAsync();
        }



        /// <summary>Устанавливает ровно одно значение чипа для столбца, убирая предыдущее.</summary>
        public void SetChipFilter(ColumnMeta meta, string value)
        {
            var existing = ChipFilters.FirstOrDefault(c => c.DisplayName == meta.DisplayName);
            if (existing is not null) ChipFilters.Remove(existing);

            if (!string.IsNullOrWhiteSpace(value))
            {
                ChipFilters.Add(new FilterChip
                {
                    SqlColumn = meta.SqlColumn,
                    DisplayName = meta.DisplayName,
                    Value = value,
                    IsInMemory = meta.Kind == FilterKind.InMemory,
                });
            }

            _ = LoadPartsAsync();
        }


        public void RemoveChipFilter(FilterChip chip)
        {
            ChipFilters.Remove(chip);
            _ = LoadPartsAsync();
        }

        public void ClearChipFilters()
        {
            ChipFilters.Clear();
            _ = LoadPartsAsync();
        }

        /// <summary>
        /// Применяет InMemory-фильтры к уже загруженной коллекции.
        /// </summary>
        private List<Part> ApplyInMemoryFilters(List<Part> parts) =>
            PartsFilterService.ApplyInMemoryFilters(parts, ChipFilters);


        private static bool TryGetMetaByEditorKey(string fieldKey, out ColumnMeta? meta)
        {
            meta = null;

            if (fieldKey.StartsWith("col:", StringComparison.OrdinalIgnoreCase) &&
                PartColumnMeta.Map.TryGetValue(fieldKey[4..], out var byId))
            {
                meta = byId;
                return true;
            }

            meta = PartColumnMeta.Map.Values.FirstOrDefault(m => m.SqlColumn == fieldKey);
            return meta is not null;
        }

        /// <summary>
        /// Сохраняет решение аналитика и обновляет UI.
        /// </summary>
        /// <param name="acceptedFromAi">Решение принято через диалог вердикта ИИ (этап 2):
        /// автор фиксируется как «аналитик+ИИ».</param>
        /// <param name="againstAiVerdict">Решение вопреки вердикту (кнопка «Не согласен») —
        /// помечается в комментарии; отзыв при этом обязателен.</param>
        /// <param name="acceptedAiResult">Вердикт ИИ, показанный в диалоге, — пишется
        /// тем же запросом, что и решение. Именно этот прогон видел аналитик, даже
        /// если параллельно был другой (иначе AiVerdict считается по чужому вердикту,
        /// кейс QTS350 26.09.2026). null — обычное сохранение решения, ИИ-поля не трогаем.</param>
        /// <param name="shiftDate">Сутки, к которым относится решение. null — текущий
        /// фильтр дат; из пути принятия вердикта ИИ передаётся снимок на момент анализа,
        /// иначе кнопка «Все даты» успела бы увести ревью на пустой день.</param>
        private async Task SaveDayReviewInternalAsync(AnalystDecision decision, bool isFullyReviewed, bool touchReviewMeta = true, bool acceptedFromAi = false, bool againstAiVerdict = false, AiAnalysisResult? acceptedAiResult = null, DateTime? shiftDate = null)
        {
            var machine = MachineFilters.FirstOrDefault(f => f.Filter)?.Machine
                          ?? PartsInfo.Machine;

            var flaggedParts = Parts.Where(p => p.IsFlagged).ToList();

            // При эскалации без комментария — предупредить, но не блокировать
            if (decision == AnalystDecision.Escalated
                && string.IsNullOrWhiteSpace(DayReviewComment)
                && flaggedParts.Count == 0)
            {
                var answer = MessageBoxWindow.Show(
                    "Не указан комментарий и не отмечены проблемные строки.\nПродолжить?",
                    "Эскалация без деталей",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxDefaultButton.No);
                if (answer == MessageBoxResult.No) return;
            }

            var comment = acceptedFromAi
                ? AiReviewComment.ApplyMarker(DayReviewComment, againstAiVerdict)
                : DayReviewComment ?? string.Empty;
            var review = new DayReview(
                machine: machine,
                shiftDate: shiftDate ?? FromDate.Date,
                reviewedBy: acceptedFromAi ? Environment.UserName + "+ИИ" : Environment.UserName,
                decision: decision,
                isFullyReviewed: isFullyReviewed,
                comment: comment
            );
            review.AiFeedback = AiFeedbackText;
            review.TouchReviewMeta = touchReviewMeta;
            if (acceptedAiResult is { HasError: false })
            {
                review.AiResult = acceptedAiResult;
                review.AiThinkingEnabled = AiThinkingEnabled;
                review.AiModelVersion = AppSettings.AiModel;
            }

            Status = "Сохранение решения...";
            var (result, dayReviewId, message) = await Database.SaveDayReviewAsync(review);

            if (result != remeLog.Core.Db.DbResult.Ok)
            {
                MessageBoxWindow.Show(message, "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
                Status = string.Empty;
                return;
            }

            // Очищаем старые флаги перед записью новых
            await Database.ClearPartFlagsAsync(dayReviewId);

            // Сохраняем флаги проблемных строк
            foreach (var part in flaggedParts)
            {
                var flag = new PartFlag(
                    dayReviewId: dayReviewId,
                    partGuid: part.Guid,
                    isCleared: false          // false = проблемная запись
                );
                await Database.SavePartFlagAsync(flag);
            }

            // Снимаем флаги с "однозначных" строк при эскалации — они помечаются как cleared
            if (decision == AnalystDecision.Escalated)
            {
                foreach (var part in Parts.Where(p => !p.IsFlagged))
                {
                    var flag = new PartFlag(
                        dayReviewId: dayReviewId,
                        partGuid: part.Guid,
                        isCleared: true       // true = эта строка ок, несмотря на эскалацию
                    );
                    await Database.SavePartFlagAsync(flag);
                }
            }

            CurrentDayReview = review;

            var flagInfo = flaggedParts.Count > 0
                ? $" ({flaggedParts.Count} проблемных строк)"
                : "";
            Status = $"Решение сохранено: {decision.ToDisplayString()}{flagInfo}"
                + (acceptedFromAi ? (againstAiVerdict ? " (вопреки ИИ)" : " (по ИИ)") : "");
            await Task.Delay(2500);
            Status = string.Empty;
        }


        /// <summary>
        /// Загружает существующее решение по текущим суткам/станку.
        /// Вызывается из Init() и LoadPartsAsync().
        /// </summary>
        private async Task LoadDayReviewAsync()
        {
            OnPropertyChanged(nameof(FlaggedPartsCount));
            OnPropertyChanged(nameof(HasFlaggedParts));

            if (!IsSingleMachineSingleDay)
            {
                CurrentDayReview = null;
                OnPropertyChanged(nameof(IsSingleMachineSingleDay));
                OnPropertyChanged(nameof(CanUserReview));
                OnPropertyChanged(nameof(HasFeatureAi));
                OnPropertyChanged(nameof(HasFeatureAdvancedEdit));
                OnPropertyChanged(nameof(FlaggedPartsCount));
                return;
            }

            OnPropertyChanged(nameof(IsSingleMachineSingleDay));
            OnPropertyChanged(nameof(CanUserReview));

            var machine = MachineFilters.FirstOrDefault(f => f.Filter)?.Machine
                          ?? PartsInfo.Machine;

            CurrentDayReview = await Database.GetDayReviewAsync(machine, FromDate.Date);
            _AutoExcludeLog = CurrentDayReview is { IsAutoClosed: true }
                ? await Database.GetAutoExcludeLogAsync(machine, FromDate.Date)
                : new List<AutoExcludeLogEntry>();
            OnPropertyChanged(nameof(DayReviewStatusToolTip));

            if (CurrentDayReview?.HasAiResult == true)
            {
                string[]? signals = null;
                if (CurrentDayReview.AiSignals != null)
                {
                    try { signals = JsonSerializer.Deserialize<string[]>(CurrentDayReview.AiSignals) ?? Array.Empty<string>(); }
                    catch { signals = Array.Empty<string>(); }
                }

                AiResult = new AiAnalysisResult
                {
                    RequiresReview = CurrentDayReview.AiRequiresReview ?? false,
                    Confidence = CurrentDayReview.AiConfidence ?? 0,
                    Signals = signals ?? Array.Empty<string>(),
                    Explanation = CurrentDayReview.AiExplanation ?? string.Empty,
                    PromptVersion = CurrentDayReview.AiPromptVersion,
                };
            }
            else
            {
                AiResult = null;
            }

            DayReviewComment = CurrentDayReview?.Comment ?? string.Empty;
            AiFeedbackText = CurrentDayReview?.AiFeedback ?? string.Empty;
        }

        /// <summary>
        /// Восстанавливает флаги строк из уже сохранённых PartFlag'ов.
        /// Вызывается после LoadPartsAsync.
        /// </summary>
        private async void LoadExistingFlags()
        {
            if (CurrentDayReview == null) return;

            var flags = await Database.GetPartFlagsAsync(CurrentDayReview.Id);
            var problematicGuids = flags
                .Where(f => !f.IsCleared)
                .Select(f => f.PartGuid)
                .ToHashSet();

            foreach (var part in Parts)
                part.IsFlagged = problematicGuids.Contains(part.Guid);

            OnPropertyChanged(nameof(FlaggedPartsCount));
        }


        private void LockUpdate() => lockUpdate = true;

        private void UnlockUpdate(bool first = false)
        {
            lockUpdate = false;
            _ = LoadPartsAsync(first);
        }

        private void UpdateHasErrors()
        {
            HasErrors = Parts.Any(x => !string.IsNullOrEmpty(x.Error));
        }
    }   
}
