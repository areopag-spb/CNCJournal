using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace remeLog.Infrastructure
{
    /// <summary>
    /// Класс для управления колонками в таблице.
    /// </summary>
    public class ColumnManager
    {
        private readonly List<(string Key, string Header)> _columns = new();

        /// <summary>
        /// Добавляет новую колонку в таблицу с заголовком по-умолчанию.
        /// </summary>
        /// <param name="key">Ключ колонки (идентификатор).</param>
        public void Add(string key)
        {
            var description = GetDescription(key);
            _columns.Add((key, description));
        }

        /// <summary>
        /// Добавляет новую колонку в таблицу.
        /// </summary>
        /// <param name="key">Ключ колонки (идентификатор).</param>
        /// <param name="header">Заголовок колонки.</param>
        public void Add(string key, string header)
        {
            _columns.Add((key, header));
        }

        /// <summary>
        /// Возвращает словарь, где ключи — идентификаторы колонок, а значения — индексы (1-based).
        /// </summary>
        /// <returns>Словарь с индексами колонок.</returns>
        public Dictionary<string, int> GetIndexes()
        {
            return _columns.Select((col, index) => new { col.Key, Index = index + 1 })
                          .ToDictionary(c => c.Key, c => c.Index);
        }

        /// <summary>
        /// Возвращает список пар (индекс, заголовок) для построения таблицы.
        /// </summary>
        /// <returns>Список пар (индекс, заголовок).</returns>
        public IEnumerable<(int Index, string Header)> GetIndexedHeaders()
        {
            return _columns.Select((col, index) => (index + 1, col.Header));
        }

        /// <summary>
        /// Возвращает количество колонок.
        /// </summary>
        public int Count => _columns.Count;

        /// <summary>
        /// GUID
        /// </summary>
        public const string Guid = "guid";

        /// <summary>
        /// Станок
        /// </summary>
        public const string Machine = "machine";

        /// <summary>
        /// Станок закрепленный
        /// </summary>
        public const string MachineAssigned = "machineAssigned";

        /// <summary>
        /// Дата
        /// </summary>
        public const string Date = "date";

        /// <summary>
        /// Смена
        /// </summary>
        public const string Shift = "shift";

        /// <summary>
        /// Оператор
        /// </summary>
        public const string Operator = "operator";

        /// <summary>
        /// Разряд
        /// </summary>
        public const string Qualification = "qualification";

        /// <summary>
        /// Деталь
        /// </summary>
        public const string Part = "part";

        // <summary>
        /// Деталь закрепленная
        /// </summary>
        public const string PartAssigned = "partAssigned";

        /// <summary>
        /// М/Л
        /// </summary>
        public const string Order = "order";

        /// <summary>
        /// Всего по М/Л
        /// </summary>
        public const string TotalByOrder = "totalByOrder";

        /// <summary>
        /// Выполнено
        /// </summary>
        public const string Finished = "finished";

        /// <summary>
        /// Установка
        /// </summary>
        public const string Setup = "setup";

        /// <summary>
        /// Количество установок
        /// </summary>
        public const string SetupsCount = "setups";

        /// <summary>
        /// Количество изготовлений
        /// </summary>
        public const string ProductionsCount = "productions";

        /// <summary>
        /// Всего наладок (включая не учтённые в КПД — без норматива, б/н и т.д.)
        /// </summary>
        public const string TotalSetupsCount = "totalSetups";

        /// <summary>
        /// Всего изготовлений (включая не учтённые в КПД — без норматива, б/и и т.д.)
        /// </summary>
        public const string TotalProductionsCount = "totalProductions";

        /// <summary>
        /// Начало наладки
        /// </summary>
        public const string StartSetupTime = "startSetupTime";

        /// <summary>
        /// Начало изготовления
        /// </summary>
        public const string StartMachiningTime = "startMachiningTime";

        /// <summary>
        /// Конец изготовления
        /// </summary>
        public const string EndMachiningTime = "endMachiningTime";

        /// <summary>
        /// Норматив наладки
        /// </summary>
        public const string SetupTimePlan = "setupTimePlan";

        /// <summary>
        /// Фактическая наладка
        /// </summary>
        public const string SetupTimeFact = "setupTimeFact";

        /// <summary>
        /// Лимит наладки
        /// </summary>
        public const string SetupLimit = "setupLimit";

        /// <summary>
        /// Норматив штучный
        /// </summary>
        public const string SingleProductionTimePlan = "singleProductionTimePlan";

        /// <summary>
        /// Машинное время
        /// </summary>
        public const string MachiningTime = "machiningTime";

        /// <summary>
        /// Штучное фактическое
        /// </summary>
        public const string SingleProductionTime = "singleProductionTime";

        /// <summary>
        /// Время замены
        /// </summary>
        public const string PartReplacementTime = "partReplacementTime";

        /// <summary>
        /// Фактическое изготовление
        /// </summary>
        public const string ProductionTimeFact = "productionTimeFact";

        /// <summary>
        /// Норматив на партию
        /// </summary>
        public const string PlanForBatch = "planForBatch";

        /// <summary>
        /// Комментарий оператора
        /// </summary>
        public const string OperatorComment = "operatorComment";

        /// <summary>
        /// Типовые проблемы
        /// </summary>
        public const string Problems = "problems";

        /// <summary>
        /// Простои в наладке
        /// </summary>
        public const string SetupDowntimes = "setupDowntimes";

        /// <summary>
        /// Простои в изготовлении
        /// </summary>
        public const string MachiningDowntimes = "machiningDowntimes";

        /// <summary>
        /// Частичная наладка
        /// </summary>
        public const string PartialSetupTime = "partialSetupTime";

        /// <summary>
        /// Написание УП
        /// </summary>
        public const string CreateNcProgramTime = "createNcProgramTime";

        /// <summary>
        /// Обслуживание
        /// </summary>
        public const string MaintenanceTime = "maintenanceTime";

        /// <summary>
        /// Поиск инструмента
        /// </summary>
        public const string ToolSearchingTime = "toolSearchingTime";

        /// <summary>
        /// Замена инструмента
        /// </summary>
        public const string ToolChangingTime = "toolChangingTime";

        /// <summary>
        /// Обучение
        /// </summary>
        public const string MentoringTime = "mentoringTime";

        /// <summary>
        /// Другие службы
        /// </summary>
        public const string ContactingDepartmentsTime = "contactingDepartmentsTime";

        /// <summary>
        /// Изготовление оснастки
        /// </summary>
        public const string FixtureMakingTime = "fixtureMakingTime";

        /// <summary>
        /// Отказ оборудования
        /// </summary>
        public const string HardwareFailureTime = "hardwareFailureTime";

        /// <summary>
        /// Исключенные простои
        /// </summary>
        public const string SpecialDowntimeTime = "specialDowntimeTime";

        /// <summary>
        /// Отмеченные простои
        /// </summary>
        public const string SpecifiedDowntimesRatio = "specifiedDowntimesRatio";

        /// <summary>
        /// Комментарий к простоям
        /// </summary>
        public const string SpecifiedDowntimesComment = "specifiedDowntimesComment";

        /// <summary>
        /// Наладка или Б/Н
        /// </summary>
        public const string SetupRatioTitle = "setupRatioTitle";

        /// <summary>
        /// Невыполнение норматива наладки
        /// </summary>
        public const string MasterSetupComment = "masterSetupComment";

        /// <summary>
        /// Изготовление или Б/И
        /// </summary>
        public const string ProductionRatioTitle = "productionRatioTitle";

        /// <summary>
        /// Невыполнение норматива изготовления
        /// </summary>
        public const string MasterProductionComment = "masterProductionComment";

        /// <summary>
        /// Исходная отметка мастера в наладке, до переопределения аналитиком. Нужна рядом с
        /// итоговой причиной в датасете: пара «что выбрал мастер → на что исправил СГТ».
        /// </summary>
        public const string MasterSetupReasonOriginal = "masterSetupReasonOriginal";

        /// <summary>
        /// Исходная отметка мастера в изготовлении, до переопределения аналитиком.
        /// </summary>
        public const string MasterMachiningReasonOriginal = "masterMachiningReasonOriginal";

        /// <summary>
        /// Считается ли переопределение причины ошибкой мастера. Пусто, если переопределения
        /// не было, — иначе неправленые записи давали бы FALSE и портили счёт.
        /// </summary>
        public const string MasterFault = "masterFault";

        /// <summary>
        /// Причина отклонения наладки одной ячейкой: «что выбрал мастер → на что исправил СГТ»
        /// (Part.MasterSetupComment / Part.SetupReasonOverride).
        /// </summary>
        public const string SetupReasonSummary = "setupReasonSummary";

        /// <summary>
        /// Комментарий по наладке одной ячейкой: детализация мастера → обоснование СГТ
        /// (Part.MasterSetupDetail / Part.SetupReasonOverrideComment).
        /// </summary>
        public const string SetupDetailSummary = "setupDetailSummary";

        /// <summary>
        /// Пояснение СГТ, в чём именно ошибка мастера в наладке (Part.SetupReasonOverrideMasterFaultComment).
        /// Пусто, если переопределение не отмечено как ошибка мастера.
        /// </summary>
        public const string SetupFaultComment = "setupFaultComment";

        /// <summary>
        /// Причина отклонения изготовления одной ячейкой. См. <see cref="SetupReasonSummary"/>.
        /// </summary>
        public const string MachiningReasonSummary = "machiningReasonSummary";

        /// <summary>
        /// Комментарий по изготовлению одной ячейкой. См. <see cref="SetupDetailSummary"/>.
        /// </summary>
        public const string MachiningDetailSummary = "machiningDetailSummary";

        /// <summary>
        /// Пояснение СГТ, в чём именно ошибка мастера в изготовлении (Part.MachiningReasonOverrideMasterFaultComment).
        /// </summary>
        public const string MachiningFaultComment = "machiningFaultComment";

        /// <summary>
        /// Кто переопределил причину (Part.ReasonOverrideBy).
        /// </summary>
        public const string ReasonOverrideBy = "reasonOverrideBy";

        /// <summary>
        /// Когда переопределена причина (Part.ReasonOverrideAt).
        /// </summary>
        public const string ReasonOverrideAt = "reasonOverrideAt";

        /// <summary>
        /// Запись исключена из расчётов отчётов (Part.ExcludeFromReports)
        /// </summary>
        public const string ExcludedFromReports = "excludedFromReports";

        /// <summary>
        /// Комментарий мастера (архив, до разделения на наладку/изготовление)
        /// </summary>
        public const string MasterComment = "masterComment";

        /// <summary>
        /// Комментарий мастера к отклонениям в наладке
        /// </summary>
        public const string MasterSetupDetail = "masterSetupDetail";

        /// <summary>
        /// Комментарий мастера к отклонениям в изготовлении
        /// </summary>
        public const string MasterMachiningDetail = "masterMachiningDetail";

        /// <summary>
        /// Текст ошибок валидации записи (Part.this[columnName])
        /// </summary>
        public const string ValidationErrors = "validationErrors";

        /// <summary>
        /// Норматив наладки (И)
        /// </summary>
        public const string FixedSetupTimePlan = "fixedSetupTimePlan";

        /// <summary>
        /// Норматив изготовления (И)
        /// </summary>
        public const string FixedProductionTimePlan = "fixedProductionTimePlan";

        /// <summary>
        /// Заключение техотдела
        /// </summary>
        public const string EngineerConclusion = "engineerConclusion";

        /// <summary>
        /// Комментарий техотдела
        /// </summary>
        public const string EngineerComment = "engineerComment";

        /// <summary>
        /// Средний размер партии
        /// </summary>
        public const string AveragePartsCount = "averagePartsCount";

        /// <summary>
        /// Средний размер серийной партии
        /// </summary>
        public const string AveragePartsCountSerial = "averagePartsCountSerial";

        /// <summary>
        /// Средний размер не серийной партии
        /// </summary>
        public const string AveragePartsCountNonSerial = "averagePartsCountNonSerial";


        /// <summary>
        /// Среднее количество изготовленных деталей
        /// </summary>
        public const string AverageFinishedCount = "averageFinishedCount";

        /// <summary>
        /// Среднее количество изготовленных серийных деталей
        /// </summary>
        public const string AverageFinishedCountSerial = "averageFinishedCountSerial";

        /// <summary>
        /// Среднее количество изготовленных не серийных деталей
        /// </summary>
        public const string AverageFinishedCountNonSerial = "averageFinishedCountNonSerial";

        /// <summary>
        /// Количество штучных изготовлений
        /// </summary>
        public const string SmallProductions = "smallProductions";

        /// <summary>
        /// Доля штучных изготовлений
        /// </summary>
        public const string SmallProductionsRatio = "smallProductionsRatio";

        /// <summary>
        /// Количество штучных партий
        /// </summary>
        public const string SmallSeries = "smallSeries";

        /// <summary>
        /// Доля штучных партий
        /// </summary>
        public const string SmallSeriesRatio = "smallSeriesRatio";

        // <summary>
        /// Количество серийных партий
        /// </summary>
        public const string SerialOrders = "serialOrders";

        // <summary>
        /// Доля серийных партий
        /// </summary>
        public const string SerialOrdersRatio = "serialOrdersRatio";

        // <summary>
        /// Количество партий
        /// </summary>
        public const string Orders = "orders";

        /// <summary>
        /// Количество серийных изготовлений
        /// </summary>
        public const string SerialCount = "serialCount";

        /// <summary>
        /// Доля серийных изготовлений
        /// </summary>
        public const string SerialCountRatio = "serialCountRatio";

        /// <summary>
        /// Отработано смен
        /// </summary>
        public const string WorkedShifts = "workedShifts";

        /// <summary>
        /// Смены без операторов
        /// </summary>
        public const string NoOperatorShifts = "noOperatorShifts";

        /// <summary>
        /// Смены с ремонтом оборудования
        /// </summary>
        public const string HardwareRepairShifts = "hardwareRepairShifts";

        /// <summary>
        /// Смены без электропитания
        /// </summary>
        public const string NoPowerShifts = "noPowerShifts";

        /// <summary>
        /// Организационные потери
        /// </summary>
        public const string ProcessRelatedLossShifts = "processRelatedLossShifts";

        /// <summary>
        /// Смены без работы по другим причинам
        /// </summary>
        public const string UnspecifiedOtherShifts = "unspecifiedOtherShifts";

        /// <summary>
        /// Коэффициент наладки
        /// </summary>
        public const string SetupRatio = "setupRatio";

        /// <summary>
        /// Коэффициент наладки включая время частичных наладок
        /// </summary>
        public const string SetupRatioIncludePartialSetups = "setupRatioIncludePartialSetups";

        /// <summary>
        /// Коэффициент наладки не исключая простоев
        /// </summary>
        public const string SetupRatioIncludeDowntimes = "setupRatioIncludeDowntimes";

        /// <summary>
        /// Коэффициент изготовления
        /// </summary>
        public const string ProductionRatio = "productionRatio";

        /// <summary>
        /// Коэффициент изготовления не исключая простоев
        /// </summary>
        public const string ProductionRatioIncludeDowntimes = "productionRatioIncludeDowntimes";

        /// <summary>
        /// Общая эффективность
        /// </summary>
        public const string GeneralRatio = "generalRatio";

        /// <summary>
        /// Коэффициент наладки на штучке
        /// </summary>
        public const string SetupRatioUnder = "setupRatioUnder";

        /// <summary>
        /// Коэффициент изготовления на штучке
        /// </summary>
        public const string ProductionRatioUnder = "productionRatioUnder";

        /// <summary>
        /// Коэффициент наладки на серийке
        /// </summary>
        public const string SetupRatioOver = "setupRatioOver";

        /// <summary>
        /// Коэффициент изготовления на серийке
        /// </summary>
        public const string ProductionRatioOver = "productionRatioOver";

        /// <summary>
        /// Соотношение штучки к серийке при наладке
        /// </summary>
        public const string SetupUnderOverRatio = "setupUnderOverRatio";

        /// <summary>
        /// Соотношение штучки к серийке при изготовлении
        /// </summary>
        public const string ProductionUnderOverRatio = "productionUnderOverRatio";

        /// <summary>
        /// Отношение наладки к общему времени
        /// </summary>
        public const string SetupToTotalRatio = "setupToTotalRatio";

        /// <summary>
        /// Отношение изготовления к общему времени
        /// </summary>
        public const string ProductionToTotalRatio = "productionToTotalRatio";

        /// <summary>
        /// Отношение нормативов к общему времени
        /// </summary>
        public const string ProductionEfficiencyToTotalRatio = "productionEfficiencyToTotalRatio";


        /// <summary>
        /// Доля машинного времени
        /// </summary>
        public const string MachineTimeToTotalRatio = "machineTimeToTotalRatio";

        /// <summary>
        /// Общее время замены
        /// </summary>
        public const string TotalReplacementTime = "totalReplacementTime";

        /// <summary>
        /// Среднее время замены детали
        /// </summary>
        public const string AverageReplacementTime = "averageReplacementTime";

        /// <summary>
        /// Отмеченные простои
        /// </summary>
        public const string SpecifiedDowntimes = "specifiedDowntimes";

        /// <summary>
        /// Отмеченные простои (для К1)
        /// </summary>
        public const string SpecifiedDowntimesEx = "specifiedDowntimesEx";

        /// <summary>
        /// Неуказанные простои
        /// </summary>
        public const string UnspecifiedDowntimes = "unspecifiedDowntimes";

        /// <summary>
        /// Количество по станку
        /// </summary>
        public const string CountPerMachine = "countPerMachine";

        /// <summary>
        /// Среднее время наладки
        /// </summary>
        public const string AverageSetupTime = "averageSetupTime";

        /// <summary>
        /// Среднее время простоев
        /// </summary>
        public const string AverageDowntimesTime = "averageDowntimesTime";

        /// <summary>
        /// Среднее время норматива наладки
        /// </summary>
        public const string AverageSetupNormative = "averageSetupNormative";

        /// <summary>
        /// Время в наладке
        /// </summary>
        public const string TotalSetupTime = "setupTime";

        /// <summary>
        /// Время в наладке на серийке
        /// </summary>
        public const string TotalSetupTimeSerial = "setupTimeSerial";

        /// <summary>
        /// Вермя изготовления
        /// </summary>
        public const string TotalProductionTime = "productionTime";

        /// <summary>
        /// Вермя изготовления на серийке
        /// </summary>
        public const string TotalProductionTimeSerial = "productionTimeSerial";

        /// <summary>
        /// Вермя простоев
        /// </summary>
        public const string TotalDowntimesTime = "downtimesTime";

        /// <summary>
        /// Сумма машинного времени
        /// </summary>
        public const string TotalMachinigTime = "totalMachinigTime";

        /// <summary>
        /// Вермя простоев на серийке
        /// </summary>
        public const string TotalDowntimesTimeSerial = "downtimesTimeSerial";

        /// <summary>
        /// Вермя общее
        /// </summary>
        public const string TotalTime = "totalTime";

        /// <summary>
        /// Совпадает
        /// </summary>
        public const string IsEqual = "isEqual";

        /// <summary>
        /// Коэффициент
        /// </summary>
        public const string Coefficient = "coefficient";

        /// <summary>
        /// Коэффициент эффективности
        /// </summary>
        public const string EfficiencyCoefficient = "efficiencyCoefficient";

        /// <summary>
        /// Коэффициент простоев
        /// </summary>
        public const string DowntimesCoefficient = "downtimesCoefficient";

        /// <summary>
        /// К1, посчитанный приложением (значение, не формула) — для сверки с формулой в файле
        /// </summary>
        public const string CoefficientApp = "coefficientApp";

        /// <summary>
        /// Сверка формулы К1 в файле с расчётом приложения
        /// </summary>
        public const string CoefficientCheck = "coefficientCheck";

        /// <summary>
        /// Тип
        /// </summary>
        public const string Type = "type";

        /// <summary>
        /// Описание
        /// </summary>
        public const string Description = "description";

        /// <summary>
        /// Годовая потребность
        /// </summary>
        public const string YearCount = "yearCount";

        /// <summary>
        /// Успешно ли
        /// </summary>
        public const string IsSuccess = "isSuccess";

        /// <summary>
        /// Старое значение
        /// </summary>
        public const string OldValue = "oldValue";

        /// <summary>
        /// Новое значение
        /// </summary>
        public const string NewValue = "newValue";

        /// <summary>
        /// Изменение
        /// </summary>
        public const string Change = "change";

        /// <summary>
        /// Изменение в процентах
        /// </summary>
        public const string ChangeRatio = "changeRatio";

        /// <summary>
        /// Старое годовое значение
        /// </summary>
        public const string OldYearValue = "oldYearValue";

        /// <summary>
        /// Новое годовое значение
        /// </summary>
        public const string NewYearValue = "newYearValue";

        /// <summary>
        /// Изменение годовое
        /// </summary>
        public const string YearChange = "yearChange";

        /// <summary>
        /// Изменение % годовое
        /// </summary>
        public const string YearChangeRatio = "yearChangeRatio";

        /// <summary>
        /// Серийная по запускам
        /// </summary>
        public const string SerialPerRuns = "serialPerRuns";

        /// <summary>
        /// Серийная по списку
        /// </summary>
        public const string SerialPerList = "serialPerList";

        /// <summary>
        /// Время включенных операций
        /// </summary>
        public const string IncludedOperationsTime = "includedOperationsTime";

        /// <summary>
        /// Время исключенных операций
        /// </summary>
        public const string ExcludedOperationsTime = "excludedOperationsTime";

        /// <summary>
        /// Причина увеличения норматива
        /// </summary>
        public const string IncreaseReason = "increaseReason";

        /// <summary>
        /// Время затраченное на серийную продукцию
        /// </summary>
        public const string SerialPartsTime = "serialPartsTime";

        /// <summary>
        /// Доля затраченноого времени на серийную продукцию
        /// </summary>
        public const string SerialPartsTimeRatio = "serialPartsTimeRatio";

        /// <summary>
        /// Время затраченное на не серийную продукцию
        /// </summary>
        public const string NonSerialPartsTime = "nonSerialPartsTime";

        private static readonly Dictionary<string, string> _descriptions = new()
        {
            { Guid, "GUID" },
            { Machine, "Станок" },
            { MachineAssigned, "Станок закрепленный" },
            { Date, "Дата" },
            { Shift, "Смена" },
            { Operator, "Оператор" },
            { Qualification, "Разряд" },
            { Part, "Деталь" },
            { PartAssigned, "Деталь закрепленная" },
            { Order, "М/Л" },
            { TotalByOrder, "Всего по М/Л" },
            { Finished, "Выполнено" },
            { Setup, "Установка" },
            { SetupsCount, $"Количество{Environment.NewLine}наладок" },
            { ProductionsCount, $"Количество{Environment.NewLine}изготовлений" },
            { TotalSetupsCount, $"Наладок{Environment.NewLine}всего" },
            { TotalProductionsCount, $"Изготовлений{Environment.NewLine}всего" },
            { StartSetupTime, "Начало наладки" },
            { StartMachiningTime, $"Начало{Environment.NewLine}изготовления" },
            { EndMachiningTime, $"Конец{Environment.NewLine}изготовления" },
            { SetupTimePlan, $"Норматив{Environment.NewLine}наладки" },
            { SetupLimit, $"Лимит{Environment.NewLine}наладки" },
            { SetupTimeFact, $"Фактическая{Environment.NewLine}наладка" },
            { SingleProductionTimePlan, $"Норматив{Environment.NewLine}штучный" },
            { MachiningTime, "Машинное время" },
            { SingleProductionTime, $"Штучное{Environment.NewLine}фактическое" },
            { PartReplacementTime, "Время замены" },
            { ProductionTimeFact, $"Фактическое{Environment.NewLine}изготовление" },
            { PlanForBatch, $"Норматив{Environment.NewLine}на партию" },
            { OperatorComment, $"Комментарий{Environment.NewLine}оператора" },
            { Problems, $"Типовые{Environment.NewLine}проблемы" },
            { SetupDowntimes, $"Простои{Environment.NewLine}в наладке" },
            { MachiningDowntimes, $"Простои{Environment.NewLine}в изготовлении" },
            { PartialSetupTime, "Частичная наладка" },
            { CreateNcProgramTime, "Написание УП" },
            { MaintenanceTime, "Обслуживание" },
            { ToolSearchingTime, "Поиск инструмента" },
            { ToolChangingTime, "Замена инструмента" },
            { MentoringTime, "Обучение" },
            { ContactingDepartmentsTime, "Другие службы" },
            { FixtureMakingTime, "Изготовление оснастки" },
            { HardwareFailureTime, "Отказ оборудования" },
            { SpecialDowntimeTime, "Исключённые простои" },
            { SpecifiedDowntimesRatio, "Отмеченные простои" },
            { SpecifiedDowntimesComment, $"Комментарий{Environment.NewLine}к простоям" },
            { SetupRatioTitle, "Наладка" },
            { MasterSetupComment, $"Отклонения в{Environment.NewLine}наладке" },
            { ProductionRatioTitle, "Изготовление" },
            { MasterProductionComment, $"Отклонения в{Environment.NewLine}изготовлении" },
            { MasterSetupReasonOriginal, $"Отметка мастера{Environment.NewLine}(наладка)" },
            { MasterMachiningReasonOriginal, $"Отметка мастера{Environment.NewLine}(изготовление)" },
            { MasterFault, $"Ошибка{Environment.NewLine}мастера" },
            { SetupReasonSummary, $"Причина отклонения{Environment.NewLine}наладки" },
            { SetupDetailSummary, $"Комментарий{Environment.NewLine}(наладка)" },
            { SetupFaultComment, $"В чём ошибка{Environment.NewLine}(наладка)" },
            { MachiningReasonSummary, $"Причина отклонения{Environment.NewLine}изготовления" },
            { MachiningDetailSummary, $"Комментарий{Environment.NewLine}(изготовление)" },
            { MachiningFaultComment, $"В чём ошибка{Environment.NewLine}(изготовление)" },
            { ReasonOverrideBy, $"Кто{Environment.NewLine}переопределил" },
            { ReasonOverrideAt, $"Когда{Environment.NewLine}переопределено" },
            { ExcludedFromReports, "Исключено" },
            { ValidationErrors, $"Не заполнено{Environment.NewLine}(требует мастера)" },
            { MasterComment, "Комментарий мастера" },
            { MasterSetupDetail, "Комментарий мастера (наладка)" },
            { MasterMachiningDetail, "Комментарий мастера (изготовление)" },
            { FixedSetupTimePlan, "Норматив наладки (И)" },
            { FixedProductionTimePlan, "Норматив изготовления (И)" },
            { EngineerConclusion, "Заключение техотдела" },
            { EngineerComment, "Комментарий техотдела" },
            { AveragePartsCount, "Средняя партия" },
            { AveragePartsCountSerial, "Средняя серийная партия" },
            { AveragePartsCountNonSerial, "Средняя не серийная партия" },
            { AverageFinishedCount, "Среднее изготовление" },
            { AverageFinishedCountSerial, "Среднее изготовление серийки" },
            { AverageFinishedCountNonSerial, "Среднее изготовление не серийки" },
            { SmallProductions, $"Количество штучных{Environment.NewLine}изготовлений" },
            { SmallSeries, $"Количество штучных{Environment.NewLine}партий" },
            { SmallProductionsRatio, $"Доля штучных{Environment.NewLine}изготовлений" },
            { SmallSeriesRatio, $"Доля штучных{Environment.NewLine}партий" },
            { WorkedShifts, "Отработанные смены" },
            { NoOperatorShifts, "Смены без операторов" },
            { HardwareRepairShifts, $"Смены с ремонтом{Environment.NewLine}оборудования" },
            { NoPowerShifts, "Смены без электропитания" },
            { ProcessRelatedLossShifts, "Организационные потери" },
            { UnspecifiedOtherShifts, $"Смены без работы{Environment.NewLine}по другим причинам" },
            { GeneralRatio, $"Эффективность" },
            { SetupRatio, $"Выполнение норматива{Environment.NewLine}наладки" },
            { SetupRatioIncludePartialSetups, $"Эффективность наладки{Environment.NewLine}без вычета частичных" },
            { SetupRatioIncludeDowntimes, $"Выполнение норматива{Environment.NewLine}наладки{Environment.NewLine}(с простоями)" },
            { ProductionRatio, $"Выполнение норматива{Environment.NewLine}изготовления" },
            { ProductionRatioIncludeDowntimes, $"Выполнение норматива{Environment.NewLine}изготовления{Environment.NewLine}(с простоями)" },
            { SetupRatioUnder, $"Выполнение норматива{Environment.NewLine}наладки на штучке" },
            { ProductionRatioUnder, $"Выполнение норматива{Environment.NewLine}изготовления на штучке" },
            { SetupRatioOver, $"Выполнение норматива{Environment.NewLine}наладки на серийке" },
            { ProductionRatioOver, $"Выполнение норматива{Environment.NewLine}изготовления на серийке" },
            { SetupUnderOverRatio, $"Соотношение штучки{Environment.NewLine}к серийке при наладке" },
            { ProductionUnderOverRatio, $"Соотношение штучки{Environment.NewLine}к серийке при изготовлении" },
            { SetupToTotalRatio, $"Доля наладок" },
            { ProductionToTotalRatio, $"Доля изготовлений" },
            { ProductionEfficiencyToTotalRatio, $"Отношение нормативов{Environment.NewLine}к общему времени" },
            { MachineTimeToTotalRatio, $"Доля машинного времени" },
            { AverageReplacementTime, $"Среднее время{Environment.NewLine}замены детали" },
            { TotalReplacementTime, $"Общее время{Environment.NewLine}замены детали" },
            { SpecifiedDowntimes, "Отмеченные простои" },
            { SpecifiedDowntimesEx, $"Отмеченные простои{Environment.NewLine}(для К1)" },
            { UnspecifiedDowntimes, "Неуказанные простои" },
            { CountPerMachine, $"Количество{Environment.NewLine}по станку" },
            { AverageSetupTime, $"Среднее{Environment.NewLine}время наладки" },
            { AverageDowntimesTime, $"Среднее время{Environment.NewLine}простоев" },
            { AverageSetupNormative, $"Среднее время{Environment.NewLine}норматива наладки" },
            { TotalSetupTime, $"Время наладки" },
            { TotalSetupTimeSerial, $"Время наладки на серийке" },
            { TotalProductionTime, $"Время изготовления" },
            { TotalProductionTimeSerial, $"Время изготовления на серийке" },
            { TotalDowntimesTime, $"Время простоев" },
            { TotalDowntimesTimeSerial, $"Время простоев на серийке" },
            { TotalTime, $"Отмеченное общее время" },
            { TotalMachinigTime, $"Сумма машинного времени" },
            { IsEqual, $"Совпадает" },
            { Coefficient, $"Коэффициент" },
            { EfficiencyCoefficient, $"Коэффициент эффективности" },
            { DowntimesCoefficient, $"Коэффициент простоев" },
            { CoefficientApp, $"Коэффициент{Environment.NewLine}(расчёт приложения)" },
            { CoefficientCheck, $"Сверка" },
            { Type, $"Тип" },
            { Description, $"Описание" },
            { YearCount, $"Годовая потребность" },
            { IsSuccess, $"Успешно ли" },
            { OldValue, "Старое значение" },
            { NewValue, "Новое значение" },
            { Change, "Изменение" },
            { ChangeRatio, "Изменение %" },
            { OldYearValue, $"Старое{Environment.NewLine}годовое значение" },
            { NewYearValue, $"Новое{Environment.NewLine}годовое значение" },
            { YearChange, "Изменение годовое" },
            { YearChangeRatio, "Изменение % годовое" },
            { SerialPerRuns, $"Серийная{Environment.NewLine}по запускам" },
            { SerialPerList, $"Серийная{Environment.NewLine}по списку" },
            { IncludedOperationsTime, $"Время включённых{Environment.NewLine}операций" },
            { ExcludedOperationsTime, $"Время исключённых{Environment.NewLine}операций" },
            { IncreaseReason, $"Причина увеличения{Environment.NewLine}норматива" },
            { SerialPartsTime, $"Время затраченное на{Environment.NewLine}серийную продукцию, час" },
            { SerialPartsTimeRatio, $"Доля времени затраченноого{Environment.NewLine}на серийную продукцию" },
            { Orders, $"Количество заказов, шт" },
            { SerialOrders, $"Количество заказов{Environment.NewLine}серийной продукции, шт" },
            { SerialOrdersRatio, $"Доля заказов{Environment.NewLine}серийной продукции" },
            { SerialCount, $"Количество записей{Environment.NewLine}о серийной продукции, шт" },
            { SerialCountRatio, $"Доля записей{Environment.NewLine}о серийной продукции" },
            { NonSerialPartsTime, $"Время затраченное на{Environment.NewLine}не серийную продукцию, час" }
        };

        /// <summary>
        /// Возвращает описание для ключа.
        /// </summary>
        /// <param name="key">Ключ колонки.</param>
        /// <returns>Описание или "Н/Д", если описание не найдено.</returns>
        static string GetDescription(string key)
        {
            return _descriptions.TryGetValue(key, out var description) ? description : "Н/Д";
        }


        /// <summary>
        /// Билдер для создания экземпляров ColumnManager.
        /// </summary>
        public class Builder
        {
            private readonly List<(string Key, string Header)> _columns = new();

            public Builder Add(string key)
            {
                var description = GetDescription(key);
                _columns.Add((key, description));
                return this;
            }

            public Builder Add(string key, string header)
            {
                _columns.Add((key, header));
                return this;
            }

            public Builder AddRange(IEnumerable<string> keys)
            {
                foreach (var key in keys)
                {
                    Add(key);
                }
                return this;
            }

            public Builder AddRange(IEnumerable<(string Key, string Header)> columns)
            {
                foreach (var column in columns)
                {
                    Add(column.Key, column.Header);
                }
                return this;
            }

            public ColumnManager Build()
            {
                var manager = new ColumnManager();
                foreach (var column in _columns)
                {
                    manager.Add(column.Key, column.Header);
                }
                return manager;
            }
        }
    }
}
