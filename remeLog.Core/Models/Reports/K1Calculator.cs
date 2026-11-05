using System;
using System.Collections.Generic;
using System.Globalization;

namespace remeLog.Models.Reports
{
    /// <summary>
    /// Расчёт К1 = коэффициент эффективности × коэффициент простоев по агрегатам строки оператор × станок.
    /// Единый источник логики: по нему же строятся формулы Excel-отчёта и окно К1 в eLog.
    /// </summary>
    public static class K1Calculator
    {
        /// <summary>Порог «К1 больше 1» с запасом на погрешность произведения коэффициентов (то же число в формулах Excel).</summary>
        public const double AboveOne = 1.000000001;

        public static K1Result Calculate(K1Input input, K1RuleSet rules)
        {
            var general = GeneralRatio(input);
            var reasons = new List<string>();

            var shiftsOk = input.WorkedShifts >= input.RequiredShifts;
            if (!shiftsOk)
                reasons.Add($"Недостаточно смен (минимум {input.RequiredShifts})");

            var timeOk = input.IncludedHours >= input.TotalHours * rules.MinimumIncludedTimeRatio;
            if (!timeOk)
            {
                var included = input.TotalHours > 0 ? input.IncludedHours / input.TotalHours : 0;
                reasons.Add($"Учтено менее {Percent(rules.MinimumIncludedTimeRatio)} отмеченного времени (учтено {Percent(included)})");
            }

            var hoursOk = true;
            if (rules.MinimumMarkedHoursPerShift is double minHours)
            {
                var perShift = input.WorkedShifts > 0 ? input.TotalHours / input.WorkedShifts : 0;
                hoursOk = input.WorkedShifts > 0 && perShift >= minHours;
                if (!hoursOk)
                    reasons.Add($"Среднее отмеченное время на смену {Number(perShift)} ч (минимум {Number(minHours)} ч)");
            }

            if (reasons.Count > 0)
                return new K1Result(general, null, null, null, reasons, rules.Version);

            var efficiency = EfficiencyCoefficient(general, EfficiencyLevels(input));
            var downtime = DowntimeCoefficient(input.SpecifiedDowntimesRatioEx, DowntimeLevels(input));
            double? k1 = efficiency * downtime;

            // На несерийном станке при низком изготовлении обрубается только К1 выше 1; 1 и ниже остаются.
            if (!input.IsSerialMachine && k1 > AboveOne && input.ProductionRatio < rules.NonSerialMinProductionRatio)
            {
                k1 = null;
                reasons.Add($"Изготовление менее {Percent(rules.NonSerialMinProductionRatio)} на несерийном станке: К1 выше 1 не засчитывается");
            }

            return new K1Result(general, efficiency, downtime, k1, reasons, rules.Version);
        }

        /// <summary>
        /// Общий процент. Серийный станок: у 1 разряда только изготовление, у остальных среднее наладки
        /// и изготовления, взвешенное числом операций. Несерийный: только наладка.
        /// </summary>
        public static double GeneralRatio(K1Input input)
        {
            if (!input.IsSerialMachine)
                return input.AverageSetupRatio;
            if (input.Qualification.Value == 1)
                return input.ProductionRatio;

            var weight = input.SetupsCount + input.ProductionsCount;
            if (weight == 0)
                return 0;
            var value = (input.SetupsCount * input.AverageSetupRatio + input.ProductionsCount * input.ProductionRatio) / weight;
            return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
        }

        /// <summary>Шкала эффективности: ступень берётся по первому порогу, который общий процент превышает.</summary>
        public static double EfficiencyCoefficient(double general, IReadOnlyList<LevelValue> levels)
        {
            for (var i = 0; i < levels.Count - 1; i++)
                if (general > levels[i].Value)
                    return levels[i].Coefficient;
            return levels[levels.Count - 1].Coefficient;
        }

        /// <summary>Шкала простоев: чем меньше доля простоев, тем выше коэффициент; ступень по первому порогу, который доля не достигает.</summary>
        public static double DowntimeCoefficient(double downtimeRatio, IReadOnlyList<LevelValue> levels)
        {
            for (var i = 0; i < levels.Count - 1; i++)
                if (downtimeRatio < levels[i].Value)
                    return levels[i].Coefficient;
            return levels[levels.Count - 1].Coefficient;
        }

        public static LevelValue[] EfficiencyLevels(K1Input input)
        {
            if (input.IsSerialMachine)
            {
                var g = input.Qualification.Efficiency;
                return new[] { g.HH, g.H, g.N, g.L, g.LL, g.LLL };
            }
            var n = input.Qualification.NonSerialEfficiency;
            return new[] { n.HH, n.H, n.N, n.L, n.LL, n.LLL };
        }

        /// <summary>
        /// Шкала простоев с учётом границы станка: ступень N поднимается до DowntimeLimit, если шкала разряда
        /// ниже (у SKT21 15% вместо 10%). Ниже шкалы граница станка не опускает.
        /// </summary>
        public static LevelValue[] DowntimeLevels(K1Input input)
        {
            var g = input.Qualification.DownTimes;
            var n = g.N with { Value = Math.Max(g.N.Value, input.DowntimeLimit) };
            return new[] { g.HH, g.H, n, g.L, g.LL, g.LLL };
        }

        private static string Percent(double ratio) =>
            (ratio * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

        private static string Number(double value) =>
            value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
