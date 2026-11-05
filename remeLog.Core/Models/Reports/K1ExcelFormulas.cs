using System;
using System.Collections.Generic;
using System.Globalization;

namespace remeLog.Models.Reports
{
    /// <summary>
    /// Формулы Excel-отчёта по операторам, построенные по тем же правилам, что и <see cref="K1Calculator"/>.
    /// Структура ступеней (направление сравнения, число ступеней, граница станка, допуски) описана здесь один раз;
    /// лист «Коэффициенты» записывается по <see cref="CoefficientRow"/>, поэтому раскладка листа и ссылки в формулах
    /// не расходятся. Живые формулы позволяют править пороги прямо в файле, колонка сверки показывает расхождение
    /// с расчётом приложения.
    /// </summary>
    public static class K1ExcelFormulas
    {
        public const string CoefficientsSheet = "Коэффициенты";

        // Позиции колонок листа «Коэффициенты» (1 = A): разряд, затем по шесть пороговых значений и коэффициентов.
        private const int EffValues = 2;
        private const int EffCoeffs = 8;
        private const int DownValues = 14;
        private const int DownCoeffs = 20;
        private const int NonSerialValues = 26;
        private const int NonSerialCoeffs = 32;

        /// <summary>Ссылки на ячейки строки отчёта (относительные, например «S5»).</summary>
        public sealed record RowRefs(
            string Qualification,
            string AverageSetupRatio,
            string ProductionRatio,
            string SetupsCount,
            string ProductionsCount,
            string DowntimeEx,
            string General,
            string Shifts,
            string IncludedHours,
            string TotalHours,
            string EfficiencyCoefficient,
            string DowntimeCoefficient,
            string K1,
            string K1App);

        /// <summary>Ячейки параметров отчёта (абсолютные, например «$AE$2»).</summary>
        public sealed record ParamRefs(string MinIncludedTimeRatio, string RequiredShifts, string? MinMarkedHoursPerShift);

        /// <summary>Заголовки листа «Коэффициенты» в порядке колонок.</summary>
        public static IReadOnlyList<string> CoefficientHeaders { get; } = BuildHeaders();

        /// <summary>Значения строки листа «Коэффициенты» для разряда в порядке колонок (первая — сам разряд).</summary>
        public static IReadOnlyList<double> CoefficientRow(Qualification q)
        {
            var e = q.Efficiency; var d = q.DownTimes; var n = q.NonSerialEfficiency;
            var e6 = new[] { e.HH, e.H, e.N, e.L, e.LL, e.LLL };
            var d6 = new[] { d.HH, d.H, d.N, d.L, d.LL, d.LLL };
            var n6 = new[] { n.HH, n.H, n.N, n.L, n.LL, n.LLL };
            var row = new List<double> { q.Value };
            foreach (var group in new[] { e6, d6, n6 })
            {
                foreach (var l in group) row.Add(l.Value);
                foreach (var l in group) row.Add(l.Coefficient);
            }
            return row;
        }

        /// <summary>Общий процент («Эффективность»).</summary>
        public static string General(bool isSerial, RowRefs r) => isSerial
            ? $"=IF({r.Qualification}=1,{r.ProductionRatio},IF({r.SetupsCount}+{r.ProductionsCount}=0,0,({r.SetupsCount}*{r.AverageSetupRatio}+{r.ProductionsCount}*{r.ProductionRatio})/({r.SetupsCount}+{r.ProductionsCount})))"
            : $"={r.AverageSetupRatio}";

        /// <summary>Допуск строки к расчёту К1 (условия из <see cref="K1Calculator.Calculate"/>).</summary>
        public static string Eligibility(RowRefs r, ParamRefs p, K1RuleSet rules)
        {
            var conditions = $"{r.Shifts}>={p.RequiredShifts},{r.IncludedHours}>={r.TotalHours}*{p.MinIncludedTimeRatio}";
            if (rules.MinimumMarkedHoursPerShift != null && p.MinMarkedHoursPerShift != null)
                conditions += $",{r.Shifts}>0,IF({r.Shifts}>0,{r.TotalHours}/{r.Shifts},0)>={p.MinMarkedHoursPerShift}";
            return $"AND({conditions})";
        }

        /// <summary>Коэффициент эффективности: ступень выбирается по первому порогу, который общий процент превышает.</summary>
        public static string Efficiency(bool isSerial, int coefficientsRow, RowRefs r, ParamRefs p, K1RuleSet rules)
        {
            var values = isSerial ? EffValues : NonSerialValues;
            var coeffs = isSerial ? EffCoeffs : NonSerialCoeffs;
            string V(int i) => Ref(values + i, coefficientsRow);
            string C(int i) => Ref(coeffs + i, coefficientsRow);
            var ladder = $"IF({r.General}>{V(0)},{C(0)},IF({r.General}>{V(1)},{C(1)},IF({r.General}>{V(2)},{C(2)},IF({r.General}>{V(3)},{C(3)},IF({r.General}>{V(4)},{C(4)},{C(5)})))))";
            return $"=IF({Eligibility(r, p, rules)},{ladder},\"\")";
        }

        /// <summary>
        /// Коэффициент простоев: чем меньше доля, тем выше коэффициент. Ступень N не ниже границы станка
        /// (<paramref name="downtimeLimit"/>): у SKT21 15% вместо 10%.
        /// </summary>
        public static string Downtime(int coefficientsRow, double downtimeLimit, RowRefs r, ParamRefs p, K1RuleSet rules)
        {
            string V(int i) => Ref(DownValues + i, coefficientsRow);
            string C(int i) => Ref(DownCoeffs + i, coefficientsRow);
            var limit = downtimeLimit.ToString("0.####", CultureInfo.InvariantCulture);
            var n = $"MAX({V(2)},{limit})";
            var ladder = $"IF({r.DowntimeEx}<{V(0)},{C(0)},IF({r.DowntimeEx}<{V(1)},{C(1)},IF({r.DowntimeEx}<{n},{C(2)},IF({r.DowntimeEx}<{V(3)},{C(3)},IF({r.DowntimeEx}<{V(4)},{C(4)},{C(5)})))))";
            return $"=IF({Eligibility(r, p, rules)},{ladder},\"\")";
        }

        /// <summary>
        /// Итоговый К1. На несерийном станке при изготовлении ниже порога К1 выше 1 не засчитывается (пусто),
        /// К1, равный 1 или ниже, остаётся.
        /// </summary>
        public static string K1(bool isSerial, RowRefs r, K1RuleSet rules)
        {
            var product = $"{r.EfficiencyCoefficient}*{r.DowntimeCoefficient}";
            var numbers = $"AND(ISNUMBER({r.EfficiencyCoefficient}),ISNUMBER({r.DowntimeCoefficient}))";
            if (isSerial)
                return $"=IF({numbers},{product},\"\")";
            var min = rules.NonSerialMinProductionRatio.ToString("0.####", CultureInfo.InvariantCulture);
            var above = K1Calculator.AboveOne.ToString("0.#########", CultureInfo.InvariantCulture);
            return $"=IF({numbers},IF(AND({product}>{above},{r.ProductionRatio}<{min}),\"\",{product}),\"\")";
        }

        /// <summary>Сверка живой формулы К1 с расчётом приложения: «✓» — совпало (или оба пусты), «≠» — разошлись.</summary>
        public static string Check(RowRefs r) =>
            $"=IF(ISNUMBER({r.K1})=ISNUMBER({r.K1App}),IF(ISNUMBER({r.K1}),IF(AND({r.K1}>{r.K1App}-0.000001,{r.K1}<{r.K1App}+0.000001),\"✓\",\"≠\"),\"✓\"),\"≠\")";

        private static string Ref(int column, int row) => $"{CoefficientsSheet}!{ColumnLetter(column)}{row}";

        public static string ColumnLetter(int column)
        {
            var s = "";
            while (column > 0)
            {
                column--;
                s = (char)('A' + column % 26) + s;
                column /= 26;
            }
            return s;
        }

        private static IReadOnlyList<string> BuildHeaders()
        {
            var levels = new[] { "HH", "H", "N", "L", "LL", "LLL" };
            var headers = new List<string> { "Квалификация" };
            foreach (var prefix in new[] { "Eff", "Down", "NEff" })
            {
                foreach (var l in levels) headers.Add($"{prefix}{l}_Value");
                foreach (var l in levels) headers.Add($"{prefix}{l}_Coeff");
            }
            return headers;
        }
    }
}
