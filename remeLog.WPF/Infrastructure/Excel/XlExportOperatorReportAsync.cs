using ClosedXML.Excel;
using libeLog;
using libeLog.Extensions;
using libeLog.Infrastructure;
using libeLog.Infrastructure.Excel;
using libeLog.Models;
using remeLog.Infrastructure.Extensions;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using remeLog.Models.Reports;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CM = remeLog.Infrastructure.ColumnManager;
using Part = remeLog.Models.Part;

namespace remeLog.Infrastructure
{
    public static partial class Xl
    {
        /// <summary>
        /// Экспорт отчета по операторам. Расчёт К1 выполняет <see cref="OperatorKpiService"/>,
        /// здесь только вывод: значения по деталям, живые формулы (собраны <see cref="K1ExcelFormulas"/>
        /// из тех же правил) и колонки сверки формулы с расчётом приложения.
        /// </summary>
        /// <param name="parts">Отметки по которым будут производиться расчеты</param>
        /// <param name="fromDate">Начальная дата</param>
        /// <param name="toDate">Конечная дата</param>
        /// <param name="path">Путь к формируемому файлу</param>
        /// <param name="includeSmallBatch">false (по умолчанию) — изготовление без штучных партий;
        /// true — включая штучные (все записи). Штучная партия по регламенту: м/в &lt; 3 мин и ≤ 10 шт,
        /// либо м/в ≥ 3 мин и ≤ 5 шт (Part.IsSmallBatch)</param>
        /// <param name="serialParts">Серийные детали (опционально)</param>
        /// <param name="rules">Правила К1 (по умолчанию — текущие)</param>
        /// <returns>При удачном выполнении возвращает путь к записанному файлу</returns>
        public static async Task<string> ExportOperatorReportAsync(IEnumerable<Part> parts,
                                                                    DateTime fromDate,
                                                                    DateTime toDate,
                                                                    string path,
                                                                    bool includeSmallBatch,
                                                                    HashSet<string>? serialParts = null,
                                                                    bool includeExcludedParts = false,
                                                                    IProgress<string>? progress = null,
                                                                    K1RuleSet? rules = null)
        {
            progress?.Report("Проверка вводных данных");
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Путь не может быть пустым", nameof(path));
            if (fromDate > toDate) throw new ArgumentException("Начальная дата не может быть позже конечной");
            rules ??= K1RuleSet.Current;

            progress?.Report("Получения данных об операторах");
            var operatorsTask = Database.GetOperatorsAsync();
            var qualificationsTask = Database.GetQualificationsAsync();
            var machinesTask = Database.GetMachineProfilesAsync();
            await Task.WhenAll(operatorsTask, qualificationsTask, machinesTask);
            var qualifications = (await qualificationsTask).OrderBy(q => q.Value).ToList();

            progress?.Report("Расчёт К1");
            var kpiRows = OperatorKpiService.Calculate(new OperatorKpiRequest(
                parts, fromDate, toDate, includeSmallBatch, serialParts, includeExcludedParts,
                await operatorsTask, qualifications, await machinesTask, rules));
            var requiredShifts = OperatorKpiService.RequiredShifts(fromDate, toDate);
            var onlySerial = serialParts?.Any() == true;

            progress?.Report("Создание Ecxel книги");
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Отчет по операторам");
            var cm = new CM.Builder()
                .Add(CM.Operator)
                .Add(CM.Qualification)
                .Add(CM.Machine)
                .Add(CM.SerialOrders, "Не штучный")
                .Add(CM.SetupRatio, "Наладка средняя")
                .Add(CM.ProductionRatio, "Изготовление общее")
                .Add(CM.AverageReplacementTime)
                .Add(CM.CreateNcProgramTime)
                .Add(CM.MaintenanceTime)
                .Add(CM.ToolSearchingTime)
                .Add(CM.ToolChangingTime)
                .Add(CM.MentoringTime)
                .Add(CM.ContactingDepartmentsTime)
                .Add(CM.FixtureMakingTime)
                .Add(CM.HardwareFailureTime)
                .Add(CM.SpecialDowntimeTime)
                .Add(CM.SpecifiedDowntimes)
                .Add(CM.SpecifiedDowntimesEx, $"Простои{Environment.NewLine}(без отказа оборудования и обучения)")
                .Add(CM.GeneralRatio)
                .Add(CM.SetupsCount)
                .Add(CM.ProductionsCount)
                .Add(CM.TotalSetupsCount)
                .Add(CM.TotalProductionsCount)
                .Add(CM.WorkedShifts)
                .Add(CM.IncludedOperationsTime)
                .Add(CM.TotalTime)
                .Add(CM.EfficiencyCoefficient)
                .Add(CM.DowntimesCoefficient)
                .Add(CM.Coefficient)
                .Add(CM.CoefficientApp)
                .Add(CM.CoefficientCheck)
                .Build();

            var ci = cm.GetIndexes();
            ConfigureWorksheetHeader(ws, cm);
            var coeffWs = wb.AddWorksheet(K1ExcelFormulas.CoefficientsSheet);
            CreateCoefficientsWorksheet(coeffWs, qualifications);

            // Параметры отчёта справа от таблицы: значения можно менять, формулы пересчитаются.
            var labelCol = cm.Count + 2;
            var paramCol = cm.Count + 3;
            var minIncludedCell = ws.Cell(2, paramCol);
            var requiredShiftsCell = ws.Cell(3, paramCol);
            var minHoursCell = ws.Cell(4, paramCol);
            var paramRefs = new K1ExcelFormulas.ParamRefs(
                minIncludedCell.Address.ToStringFixed(),
                requiredShiftsCell.Address.ToStringFixed(),
                rules.MinimumMarkedHoursPerShift is null ? null : minHoursCell.Address.ToStringFixed());

            var row = 3;
            foreach (var kpi in kpiRows)
            {
                progress?.Report($"Сбор данных о операторе {kpi.Operator} на станке {kpi.Machine}");
                var groupParts = kpi.Parts;
                var agg = kpi.Aggregates;
                var isSerialMachine = kpi.Profile.IsSerial;

                ws.Cell(row, ci[CM.Operator]).SetValue(kpi.Operator);
                ws.Cell(row, ci[CM.Qualification]).SetValue(kpi.Qualification.Value);
                ws.Cell(row, ci[CM.Machine]).SetValue(kpi.Machine);
                ws.Cell(row, ci[CM.SerialOrders]).SetValue(isSerialMachine ? 1 : 0)
                    .Style.NumberFormat.SetFormat("\"✓\";;\"✗\"")
                    .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

                ws.Cell(row, ci[CM.SetupRatio])
                    .SetValue(agg.AverageSetupRatio)
                    .Style.NumberFormat.NumberFormatId = (int)XLPredefinedFormat.Number.PercentInteger;
                ws.Cell(row, ci[CM.ProductionRatio])
                    .SetValue(agg.ProductionRatio)
                    .Style.NumberFormat.NumberFormatId = (int)XLPredefinedFormat.Number.PercentInteger;

                ws.Cell(row, ci[CM.SetupsCount]).SetValue(agg.SetupsCount);
                ws.Cell(row, ci[CM.ProductionsCount]).SetValue(agg.ProductionsCount);
                ws.Cell(row, ci[CM.TotalSetupsCount]).SetValue(agg.TotalSetupsCount);
                ws.Cell(row, ci[CM.TotalProductionsCount]).SetValue(agg.TotalProductionsCount);

                ws.Cell(row, ci[CM.AverageReplacementTime])
                    .SetValue(groupParts.AverageReplacementTimeRatio())
                    .Style.NumberFormat.NumberFormatId = (int)XLPredefinedFormat.Number.Precision2;

                ws.Cell(row, ci[CM.CreateNcProgramTime]).SetValue(groupParts.Sum(p => p.CreateNcProgramTime));
                ws.Cell(row, ci[CM.MaintenanceTime]).SetValue(groupParts.Sum(p => p.MaintenanceTime));
                ws.Cell(row, ci[CM.ToolSearchingTime]).SetValue(groupParts.Sum(p => p.ToolSearchingTime));
                ws.Cell(row, ci[CM.ToolChangingTime]).SetValue(groupParts.Sum(p => p.ToolChangingTime));
                ws.Cell(row, ci[CM.MentoringTime]).SetValue(groupParts.Sum(p => p.MentoringTime));
                ws.Cell(row, ci[CM.ContactingDepartmentsTime]).SetValue(groupParts.Sum(p => p.ContactingDepartmentsTime));
                ws.Cell(row, ci[CM.FixtureMakingTime]).SetValue(groupParts.Sum(p => p.FixtureMakingTime));
                ws.Cell(row, ci[CM.HardwareFailureTime]).SetValue(groupParts.Sum(p => p.HardwareFailureTime));
                ws.Cell(row, ci[CM.SpecialDowntimeTime]).SetValue(groupParts.Sum(p => p.SpecialDowntimeTime));

                ws.Cell(row, ci[CM.SpecifiedDowntimes])
                    .SetValue(groupParts.SpecifiedDowntimesRatio(ShiftType.All))
                    .Style.NumberFormat.NumberFormatId = (int)XLPredefinedFormat.Number.PercentInteger;
                ws.Cell(row, ci[CM.SpecifiedDowntimesEx])
                    .SetValue(agg.SpecifiedDowntimesRatioEx)
                    .Style.NumberFormat.NumberFormatId = (int)XLPredefinedFormat.Number.PercentInteger;

                ws.Cell(row, ci[CM.WorkedShifts]).SetValue(agg.WorkedShifts);
                ws.Cell(row, ci[CM.IncludedOperationsTime]).SetValue(agg.IncludedHours)
                    .Style.NumberFormat.SetNumberFormatId((int)XLPredefinedFormat.Number.Integer);
                ws.Cell(row, ci[CM.TotalTime]).SetValue(agg.TotalHours)
                    .Style.NumberFormat.SetNumberFormatId((int)XLPredefinedFormat.Number.Integer);

                string Addr(string column) => ws.Cell(row, ci[column]).Address.ToStringRelative();
                var refs = new K1ExcelFormulas.RowRefs(
                    Addr(CM.Qualification), Addr(CM.SetupRatio), Addr(CM.ProductionRatio),
                    Addr(CM.SetupsCount), Addr(CM.ProductionsCount), Addr(CM.SpecifiedDowntimesEx),
                    Addr(CM.GeneralRatio), Addr(CM.WorkedShifts), Addr(CM.IncludedOperationsTime), Addr(CM.TotalTime),
                    Addr(CM.EfficiencyCoefficient), Addr(CM.DowntimesCoefficient), Addr(CM.Coefficient), Addr(CM.CoefficientApp));
                var coefficientsRow = qualifications.IndexOf(kpi.Qualification) + 2; // первая строка листа — заголовки

                ws.Cell(row, ci[CM.GeneralRatio]).FormulaA1 = K1ExcelFormulas.General(isSerialMachine, refs);
                ws.Cell(row, ci[CM.GeneralRatio]).Style.NumberFormat.NumberFormatId = (int)XLPredefinedFormat.Number.PercentInteger;
                ws.Cell(row, ci[CM.EfficiencyCoefficient]).FormulaA1 = K1ExcelFormulas.Efficiency(isSerialMachine, coefficientsRow, refs, paramRefs, rules);
                ws.Cell(row, ci[CM.DowntimesCoefficient]).FormulaA1 = K1ExcelFormulas.Downtime(coefficientsRow, kpi.Profile.DowntimeLimit, refs, paramRefs, rules);
                var coefficientCell = ws.Cell(row, ci[CM.Coefficient]);
                coefficientCell.FormulaA1 = K1ExcelFormulas.K1(isSerialMachine, refs, rules);

                if (kpi.Result.K1 is double app)
                    ws.Cell(row, ci[CM.CoefficientApp]).SetValue(app);
                ws.Cell(row, ci[CM.CoefficientCheck]).FormulaA1 = K1ExcelFormulas.Check(refs);

                if (kpi.Result.Reasons.Count > 0)
                    coefficientCell.CreateComment().AddText($"Причины:\n{string.Join(";\n", kpi.Result.Reasons)}");
                row++;
            }

            ws.ApplyStandardBorders();
            ws.ApplyAutoFilter();
            ws.AdjustColumns();
            ws.Column(ci[CM.Qualification]).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Column(ci[CM.Qualification]).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Columns(ci[CM.CreateNcProgramTime], ci[CM.SpecifiedDowntimes]).Group(true);

            if (row > 3)
            {
                var checkRange = ws.Range(3, ci[CM.CoefficientCheck], row - 1, ci[CM.CoefficientCheck]);
                checkRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                checkRange.AddConditionalFormat().WhenEquals("≠").Fill.SetBackgroundColor(_lightRed);
            }

            // Параметры К1 и подписи к ним.
            minIncludedCell.SetValue(rules.MinimumIncludedTimeRatio).Style.NumberFormat.SetNumberFormatId((int)XLPredefinedFormat.Number.PercentInteger);
            requiredShiftsCell.SetValue(requiredShifts);
            ws.Cell(2, labelCol).SetValue("Мин. доля учтённого времени");
            ws.Cell(3, labelCol).SetValue("Мин. смен (рабочие дни / 6)");
            if (rules.MinimumMarkedHoursPerShift is double minHours)
            {
                minHoursCell.SetValue(minHours);
                ws.Cell(4, labelCol).SetValue("Мин. отмеченных часов на смену (среднее)");
            }
            var labels = ws.Range(2, labelCol, 4, labelCol);
            labels.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            labels.Style.Alignment.WrapText = false;
            ws.Column(labelCol).Width = 38;
            ws.Column(cm.Count + 1).Width = 3;
            ws.Column(paramCol).Width = 8;

            // Происхождение расчёта: версия правил и методы, формулы которых продублированы в файле.
            AddHeaderNote(ws, ci[CM.EfficiencyCoefficient], $"Правила К1 v{rules.Version}. Формула собрана из K1Calculator.EfficiencyCoefficient: " +
                "ступень выбирается по первому порогу листа «Коэффициенты», который превышает «Эффективность». Допуск — см. параметры справа.");
            AddHeaderNote(ws, ci[CM.DowntimesCoefficient], $"Правила К1 v{rules.Version}. Формула собрана из K1Calculator.DowntimeCoefficient: " +
                "ступень N шкалы простоев не ниже границы станка (cnc_machines.DowntimeLimit, у SKT21 15%).");
            AddHeaderNote(ws, ci[CM.Coefficient], $"Правила К1 v{rules.Version}. K1Calculator.Calculate: коэффициент эффективности × коэффициент простоев; " +
                "на несерийном станке при изготовлении ниже 75% К1 выше 1 не засчитывается (1 и ниже остаются). Причины пустого К1 — в примечании ячейки.");
            AddHeaderNote(ws, ci[CM.CoefficientApp], "Значение, посчитанное приложением (OperatorKpiService). Если вы меняли пороги или параметры в файле, формула в колонке «Коэффициент» пересчитается, а это значение останется прежним.");
            AddHeaderNote(ws, ci[CM.CoefficientCheck], "✓ — формула в файле даёт то же, что расчёт приложения. ≠ — значения разошлись: либо правили пороги в файле, либо ошибка в расчёте.");

            // Заголовок отчета
            var batchScope = includeSmallBatch
                ? "включая штучные партии"
                : "без штучных партий (м/в < 3 мин и ≤ 10 шт или м/в ≥ 3 мин и ≤ 5 шт)";
            ws.Cell(1, 1).Value =
                $"Отчёт по операторам за период с {fromDate:dd.MM.yyyy} по {toDate:dd.MM.yyyy} " +
                $"(изготовление: {batchScope}{(onlySerial ? "; только серийка" : "")}; правила К1 v{rules.Version})";
            ws.Range(1, ci[CM.Operator], 1, cm.Count).Merge();
            ws.Range(1, ci[CM.Operator], 1, 1).Style.Font.FontSize = 14;
            ws.Columns(ci[CM.SetupRatio], cm.Count).Width = 7;
            ws.Column(ci[CM.CoefficientApp]).Width = 11;

            wb.SaveAndOfferOpen(path);
            return path;
        }

        private static void AddHeaderNote(IXLWorksheet ws, int column, string text)
        {
            var comment = ws.Cell(2, column).CreateComment();
            comment.AddText(text);
            comment.Style.Size.SetWidth(320);
            comment.Style.Size.SetHeight(110);
        }
    }
}
