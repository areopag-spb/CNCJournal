using ClosedXML.Excel;
using libeLog;
using libeLog.Extensions;
using libeLog.Infrastructure;
using libeLog.Models;
using remeLog.Infrastructure.Extensions;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using remeLog.Models.Reports;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System.Windows;
using CM = remeLog.Infrastructure.ColumnManager;
using Part = remeLog.Models.Part;

namespace remeLog.Infrastructure
{
    public static partial class Xl
    {
        private static readonly XLColor _lightRed = XLColor.FromHtml("#DA9694");
        private static readonly XLColor _lightGreen = XLColor.FromHtml("#96DA94");

        /// <summary>
        /// Типы экспорта отчетов операторов.
        /// </summary>
        public enum ExportOperatorReportType
        {
            /// <summary>
            /// Отчет по выполнению норм, где операторы выполнили план ниже нормы.
            /// </summary>
            Under,

            /// <summary>
            /// Отчет по выполнению норм, где операторы выполнили план значительно ниже нормы.
            /// (например, на уровне критической точки).
            /// </summary>
            Below
        }

        /// <summary>
        /// Варианты ориентации заголовков.
        /// </summary>
        public enum HeaderRotateOption
        {
            /// <summary>
            /// Горизонтальная ориентация заголовков.
            /// </summary>
            Horizontal,

            /// <summary>
            /// Вертикальная ориентация заголовков.
            /// </summary>
            Vertical
        }


        /// <summary>
        /// Перечисление, которое определяет полужирность для левого и правого заголовков.
        /// </summary>
        public enum BoldOption
        {
            /// <summary>
            /// Левый заголовок выделен полужирным, правый — нет.
            /// </summary>
            Left,

            /// <summary>
            /// Правый заголовок выделен полужирным, левый — нет.
            /// </summary>
            Right,

            /// <summary>
            /// Оба заголовка выделены полужирным.
            /// </summary>
            Both
        }



        private static void ConfigureMachineSheetForPeriod(XLWorkbook wb, IEnumerable<Part> parts, string machine, CM cm, ImmutableHashSet<string>? serialParts = null )
        {
            var ws = wb.AddWorksheet(machine);
            ConfigureWorksheetHeader(ws, cm);
            ws.Style.Font.FontSize = 10;
            ws.Style.Alignment.WrapText = true;
            ws.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            var ci = cm.GetIndexes();
            int row = 3;

            foreach (var part in parts.Where(p => p.Machine == machine))
            {
                ws.Cell(row, ci[CM.Date])
                    .SetValue(part.ShiftDate)
                    .Style.DateFormat.Format = "dd.MM.yy";
                ws.Cell(row, ci[CM.Shift]).SetValue(part.Shift);
                ws.Cell(row, ci[CM.Operator]).SetValue(part.Operator);
                ws.Cell(row, ci[CM.Part]).SetValue(part.PartName);
                if (serialParts != null) 
                    ws.Cell(row, ci[CM.SerialPerList])
                        .SetValue(serialParts.Contains(part.PartName.NormalizedPartNameWithoutComments()));
                ws.Cell(row, ci[CM.Order]).SetValue(part.Order);
                ws.Cell(row, ci[CM.TotalByOrder]).SetValue(part.TotalCount);
                ws.Cell(row, ci[CM.Finished]).SetValue(part.FinishedCount);
                ws.Cell(row, ci[CM.Setup]).SetValue(part.Setup);
                ws.Cell(row, ci[CM.StartSetupTime])
                    .SetValue(part.StartSetupTime)
                    .Style.DateFormat.Format = "HH:mm";
                ws.Cell(row, ci[CM.StartMachiningTime])
                    .SetValue(part.StartMachiningTime)
                    .Style.DateFormat.Format = "HH:mm";
                ws.Cell(row, ci[CM.EndMachiningTime])
                    .SetValue(part.EndMachiningTime)
                    .Style.DateFormat.Format = "HH:mm";
                ws.Cell(row, ci[CM.SetupTimePlan]).SetValue(part.SetupTimePlan);
                ws.Cell(row, ci[CM.SetupTimeFact]).SetValue(part.SetupTimeFact);
                ws.Cell(row, ci[CM.SingleProductionTimePlan]).SetValue(part.SingleProductionTimePlan);
                ws.Cell(row, ci[CM.MachiningTime]).SetValue(part.MachiningTime);
                if (part.SingleProductionTime is double spt && spt is not (double.NaN or double.NegativeInfinity or double.PositiveInfinity))
                    ws.Cell(row, ci[CM.SingleProductionTime])
                        .SetValue(spt)
                        .Style.NumberFormat.NumberFormatId = (int)XLPredefinedFormat.Number.Precision2;
                if (part.PartReplacementTime is double prt && prt is not (double.NaN or double.NegativeInfinity or double.PositiveInfinity))
                    ws.Cell(row, ci[CM.PartReplacementTime])
                        .SetValue(prt)
                        .Style.NumberFormat.NumberFormatId = (int)XLPredefinedFormat.Number.Precision2;
                ws.Cell(row, ci[CM.ProductionTimeFact]).SetValue(part.ProductionTimeFact);
                ws.Cell(row, ci[CM.PlanForBatch]).SetValue(part.PlanForBatch);
                ws.Cell(row, ci[CM.OperatorComment])
                    .SetValue(part.OperatorComment)
                    .Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                ws.Cell(row, ci[CM.SetupDowntimes]).SetValue(part.SetupDowntimes);
                ws.Cell(row, ci[CM.MachiningDowntimes]).SetValue(part.MachiningDowntimes);
                ws.Cell(row, ci[CM.PartialSetupTime]).SetValue(part.PartialSetupTime);
                ws.Cell(row, ci[CM.CreateNcProgramTime]).SetValue(part.CreateNcProgramTime);
                ws.Cell(row, ci[CM.MaintenanceTime]).SetValue(part.MaintenanceTime);
                ws.Cell(row, ci[CM.ToolSearchingTime]).SetValue(part.ToolSearchingTime);
                ws.Cell(row, ci[CM.MentoringTime]).SetValue(part.MentoringTime);
                ws.Cell(row, ci[CM.ContactingDepartmentsTime]).SetValue(part.ContactingDepartmentsTime);
                ws.Cell(row, ci[CM.FixtureMakingTime]).SetValue(part.FixtureMakingTime);
                ws.Cell(row, ci[CM.HardwareFailureTime]).SetValue(part.HardwareFailureTime);
                if (part.SpecifiedDowntimesRatio is not (double.NaN or double.NegativeInfinity or double.PositiveInfinity))
                    ws.Cell(row, ci[CM.SpecifiedDowntimesRatio])
                        .SetValue(part.SpecifiedDowntimesRatio)
                        .Style.NumberFormat.NumberFormatId = (int)XLPredefinedFormat.Number.PercentInteger;
                ws.Cell(row, ci[CM.SpecifiedDowntimesComment]).SetValue(part.SpecifiedDowntimesComment);
                ws.Cell(row, ci[CM.SetupRatioTitle]).SetValue(part.SetupRatioTitle);
                // Итоговая классификация: переопределение СГТ, если оно есть, иначе отметка мастера.
                // Детализация идёт тем же путём — обоснование СГТ, если причина переопределена,
                // иначе комментарий мастера, иначе комментарий мастера объяснял бы уже не ту причину.
                ws.Cell(row, ci[CM.MasterSetupComment]).SetValue(part.EffectiveSetupReason);
                ws.Cell(row, ci[CM.MasterSetupDetail]).SetValue(part.EffectiveSetupDetail);
                ws.Cell(row, ci[CM.ProductionRatioTitle]).SetValue(part.ProductionRatioTitle);
                ws.Cell(row, ci[CM.MasterProductionComment]).SetValue(part.EffectiveMachiningReason);
                ws.Cell(row, ci[CM.MasterMachiningDetail]).SetValue(part.EffectiveMachiningDetail);
                ws.Cell(row, ci[CM.MasterComment]).SetValue(part.MasterComment);
                ws.Cell(row, ci[CM.FixedSetupTimePlan]).SetValue(part.FixedSetupTimePlan);
                ws.Cell(row, ci[CM.FixedProductionTimePlan]).SetValue(part.FixedProductionTimePlan);
                ws.Cell(row, ci[CM.EngineerConclusion]).SetValue(part.EngineerConclusion);
                row++;
            }
            ws.RangeUsed().Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            ws.Columns().AdjustToContents();


            ws.RangeUsed().Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            ws.RangeUsed().Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            ws.RangeUsed().Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            ws.RangeUsed().SetAutoFilter(true);
            ws.Columns().AdjustToContents();

            ws.Column(ci[CM.Operator]).Width = 15;

            ws.Column(ci[CM.Part]).Width = 25;

            ws.Range(3, ci[CM.OperatorComment], row, ci[CM.OperatorComment])
               .Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            ws.Column(ci[CM.OperatorComment]).Width = 35;

            ws.Column(ci[CM.MasterSetupComment]).Width = 20;
            ws.Column(ci[CM.MasterProductionComment]).Width = 20;
            ws.Column(ci[CM.MasterComment]).Width = 20;
            ws.Row(1).Delete();
            ws.SheetView.FreezeRows(1);
        }

        /// <summary>
        /// Создает лист с коэффициентами для всех квалификаций. Раскладка колонок задана в
        /// <see cref="K1ExcelFormulas"/> — по ней же строятся ссылки в формулах отчёта.
        /// </summary>
        private static void CreateCoefficientsWorksheet(IXLWorksheet ws, IEnumerable<Qualification> qualifications)
        {
            var headers = K1ExcelFormulas.CoefficientHeaders;
            for (var i = 0; i < headers.Count; i++)
                ws.Cell(1, i + 1).SetValue(headers[i]);

            var row = 2;
            foreach (var qual in qualifications.OrderBy(q => q.Value))
            {
                var values = K1ExcelFormulas.CoefficientRow(qual);
                for (var i = 0; i < values.Count; i++)
                    ws.Cell(row, i + 1).SetValue(values[i]);
                row++;
            }

            var headerRange = ws.Range(1, 1, 1, headers.Count);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        }







        //public static string GetOperatorQualification(this string operatorName)
        //{
        //    if (!File.Exists(AppSettings.Instance.QualificationSourcePath)) return "Н/Д";
        //    try
        //    {
        //        using (var fs = new FileStream(AppSettings.Instance.QualificationSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        //        {
        //            var wb = new XLWorkbook(fs);
        //            foreach (var xlRow in wb.Worksheet(1).Rows().Skip(2))
        //            {
        //                if (xlRow.Cell(2).Value.IsText && xlRow.Cell(2).Value.GetText() == operatorName)
        //                {
        //                    if (xlRow.Cell(4).Value.IsText) return xlRow.Cell(4).Value.GetText();
        //                    else if (xlRow.Cell(4).Value.IsNumber) return xlRow.Cell(4).Value.GetNumber().ToString();
        //                }
        //            }
        //            return "Н/Д";
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Util.WriteLog(ex);
        //        return "Н/Д";
        //    }
        //}

        public static string GetOperatorQualification(this string operatorFullName, IEnumerable<OperatorInfo> operators)
        {
            foreach (var @operator in operators)
            {
                if (@operator.FullName == operatorFullName)
                {
                    return @operator.Qualification.ToString();
                }
            }
            return "Н/Д";
        }

        private static void ConfigureWorksheetStyles(IXLWorksheet ws)
        {
            ws.Style.Font.FontSize = 12;
            ws.Style.Alignment.WrapText = true;
            ws.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        /// <summary>
        /// Настраивает заголовки на рабочем листе Excel на основе словаря колонок.
        /// </summary>
        /// <param name="ws">Рабочий лист Excel.</param>
        /// <param name="columns">Словарь колонок, где ключ — идентификатор, значение — пара (индекс, заголовок).</param>
        /// <param name="headerRotateOption">Опция вращения заголовков (по умолчанию вертикально).</param>
        /// <param name="height">Высота строки заголовков (по умолчанию 90).</param>
        /// <param name="fontSize">Размер шрифта заголовков (по умолчанию 10).</param>
        private static void ConfigureWorksheetHeader(IXLWorksheet ws, Dictionary<string, (int index, string header)> columns, HeaderRotateOption headerRotateOption = HeaderRotateOption.Vertical, int height = 90, int fontSize = 10)
        {
            ConfigureWorksheetHeaderInternal(ws, columns.Select(c => (c.Value.index, c.Value.header)).ToList(), headerRotateOption, height, fontSize);
        }

        /// <summary>
        /// Настраивает заголовки на рабочем листе Excel на основе объекта CM.
        /// </summary>
        /// <param name="ws">Рабочий лист Excel.</param>
        /// <param name="cm">Объект CM, содержащий информацию о колонках.</param>
        /// <param name="headerRotateOption">Опция вращения заголовков (по умолчанию вертикально).</param>
        /// <param name="height">Высота строки заголовков (по умолчанию 90).</param>
        /// <param name="fontSize">Размер шрифта заголовков (по умолчанию 10).</param>
        private static void ConfigureWorksheetHeader(IXLWorksheet ws, CM cm, HeaderRotateOption headerRotateOption = HeaderRotateOption.Vertical, int height = 90, int fontSize = 10)
        {
            ConfigureWorksheetHeaderInternal(ws, cm.GetIndexedHeaders().ToList(), headerRotateOption, height, fontSize);
        }

        /// <summary>
        /// Настраивает заголовки на листе Excel.
        /// </summary>
        /// <param name="ws">Рабочий лист Excel.</param>
        /// <param name="columns">Список колонок, где каждая пара состоит из индекса и заголовка.</param>
        /// <param name="headerRotateOption">Опция вращения заголовков (по умолчанию вертикально).</param>
        /// <param name="height">Высота строки заголовков (по умолчанию 90).</param>
        /// <param name="fontSize">Размер шрифта заголовков (по умолчанию 10).</param>
        private static void ConfigureWorksheetHeaderInternal(IXLWorksheet ws, List<(int index, string header)> columns, HeaderRotateOption headerRotateOption, int height, int fontSize)
        {
            foreach (var (index, header) in columns)
            {
                ws.Cell(2, index).Value = header;
            }

            var headerRange = ws.Range(2, 1, 2, columns.Count);
            if (headerRotateOption == HeaderRotateOption.Vertical)
                headerRange.Style.Alignment.TextRotation = 90;

            headerRange.Style.Font.FontName = "Segoe UI Semibold";
            headerRange.Style.Font.FontSize = fontSize;
            headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            headerRange.Style.Alignment.WrapText = true;
            ws.Row(2).Height = height;
        }

        /// <summary>
        /// Устанавливает заголовок в первую строку рабочего листа, растягивая его на указанные столбцы.
        /// </summary>
        /// <param name="ws">Рабочий лист, на котором устанавливается заголовок.</param>
        /// <param name="columnsCount">Количество столбцов, на которые будет растянут заголовок (для объединения ячеек).</param>
        /// <param name="title">Текст заголовка, который будет выведен в первой ячейке.</param>
        /// <param name="fontSize">Размер шрифта для заголовка. По умолчанию 14.</param>
        /// <param name="bold">Указывает, будет ли заголовок полужирным. По умолчанию true.</param>
        /// <param name="merge">Указывает, нужно ли объединять ячейки заголовка на весь диапазон столбцов. По умолчанию true.</param>
        /// <param name="alignment">Выравнивание текста в ячейке. По умолчанию выравнивание по левому краю.</param>
        private static void SetTitle(IXLWorksheet ws, int columnsCount, string title, int fontSize = 14, bool bold = true, bool merge = true, XLAlignmentHorizontalValues alignment = XLAlignmentHorizontalValues.Left)
        {
            var cell = ws.Cell(1, 1).SetValue(title)
                .Style.Font.SetFontSize(fontSize)
                .Font.SetBold(bold)
                .Alignment.SetHorizontal(alignment)
                .Alignment.SetWrapText(false);

            if (merge)
            {
                ws.Range(1, 1, 1, columnsCount).Merge();
            }
        }

        /// <summary>
        /// Устанавливает два заголовка: один слева, другой справа. Каждый заголовок может быть выделен полужирным.
        /// </summary>
        /// <param name="ws">Рабочий лист, на котором устанавливаются заголовки.</param>
        /// <param name="columnsCount">Количество столбцов, на которые будет растянут второй заголовок (справа).</param>
        /// <param name="left">Текст заголовка, который будет выведен слева в первой ячейке.</param>
        /// <param name="right">Текст заголовка, который будет выведен справа в последней ячейке первой строки.</param>
        /// <param name="fontSize">Размер шрифта для обоих заголовков. По умолчанию 14.</param>
        /// <param name="bold">Указывает, будет ли левый и правый заголовок полужирным. 
        /// Определяется с помощью перечисления <see cref="BoldOption">BoldOption</see>. По умолчанию оба заголовка будут полужирными.</param>
        private static void SetTitle(IXLWorksheet ws, int columnsCount, string left, string right, int fontSize = 14, BoldOption bold = BoldOption.Both)
        {
            var leftCell = ws.Cell(1, 1).SetValue(left)
                .Style.Font.SetFontSize(fontSize)
                .Font.SetBold(bold == BoldOption.Left || bold == BoldOption.Both)
                .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Left)
                .Alignment.SetWrapText(false);

            var rightCell = ws.Cell(1, columnsCount).SetValue(right)
                .Style.Font.SetFontSize(fontSize)
                .Font.SetBold(bold == BoldOption.Right || bold == BoldOption.Both)
                .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right)
                .Alignment.SetWrapText(false);
        }
    }
}
