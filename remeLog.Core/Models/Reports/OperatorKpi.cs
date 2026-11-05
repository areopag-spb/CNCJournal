using remeLog.Core;
using remeLog.Core.Extensions;
using remeLog.Infrastructure.Extensions;
using remeLog.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace remeLog.Models.Reports
{
    /// <summary>Свойства станка, влияющие на К1 (cnc_machines).</summary>
    /// <param name="IsSerial">Станок серийный.</param>
    /// <param name="DowntimeLimit">Граница простоев, доля (см. <see cref="K1Input.DowntimeLimit"/>).</param>
    public sealed record MachineProfile(bool IsSerial, double DowntimeLimit)
    {
        /// <summary>Станок не найден в справочнике: как и раньше в отчётах — несерийный, общая граница простоев.</summary>
        public static readonly MachineProfile Unknown = new(false, 0.10);
    }

    /// <summary>Свёрнутые по деталям значения строки оператор × станок — входные данные К1 плюс счётчики для отчёта.</summary>
    public sealed record K1Aggregates(
        double AverageSetupRatio,
        double ProductionRatio,
        int SetupsCount,
        int ProductionsCount,
        int TotalSetupsCount,
        int TotalProductionsCount,
        double SpecifiedDowntimesRatioEx,
        int WorkedShifts,
        double IncludedHours,
        double TotalHours);

    /// <summary>Исходные данные расчёта К1 за период.</summary>
    /// <param name="Parts">Все отметки за период (без фильтров): по ним считаются отработанные смены.</param>
    /// <param name="IncludeSmallBatch">Изготовление считать вместе со штучными партиями.</param>
    /// <param name="SerialParts">Только эти детали (нормализованные названия); null или пусто — все.</param>
    /// <param name="IncludeExcludedParts">Учитывать отметки, исключённые из отчётов.</param>
    public sealed record OperatorKpiRequest(
        IEnumerable<Part> Parts,
        DateTime From,
        DateTime To,
        bool IncludeSmallBatch,
        IReadOnlyCollection<string>? SerialParts,
        bool IncludeExcludedParts,
        IEnumerable<OperatorInfo> Operators,
        IEnumerable<Qualification> Qualifications,
        IReadOnlyDictionary<string, MachineProfile> Machines,
        K1RuleSet Rules);

    /// <summary>Строка К1 по оператору на станке за период.</summary>
    public sealed record OperatorKpiRow(
        string Operator,
        string Machine,
        Qualification Qualification,
        MachineProfile Profile,
        K1Aggregates Aggregates,
        K1Input Input,
        K1Result Result,
        IReadOnlyList<Part> Parts);

    /// <summary>Свёртка деталей в агрегаты К1.</summary>
    public static class K1Aggregator
    {
        public static bool IsInvalidRatio(double ratio) =>
            ratio == 0 || double.IsInfinity(ratio) || double.IsNaN(ratio);

        /// <summary>Время детали, вошедшее в расчёт К1 (наладка с простоями и изготовление с простоями).</summary>
        public static TimeSpan IncludedTime(Part part, bool includeSmallBatch)
        {
            var span = TimeSpan.Zero;
            if (!part.ExcludeFromReports)
            {
                if (part.SetupRatio is not (0 or double.NaN or double.NegativeInfinity or double.PositiveInfinity))
                {
                    span = span.Add(TimeSpan.FromMinutes(part.SetupTimeFact)).Add(TimeSpan.FromMinutes(part.SetupDowntimes));
                }
                if (!IsInvalidRatio(part.ProductionRatio) && (includeSmallBatch || !part.IsSmallBatch))
                {
                    span = span.Add(TimeSpan.FromMinutes(part.ProductionTimeFact)).Add(TimeSpan.FromMinutes(part.MachiningDowntimes));
                }
            }
            return span;
        }

        /// <param name="groupParts">Отметки оператора на станке после фильтров отчёта.</param>
        /// <param name="shiftDates">Даты всех смен оператора на этом станке (до фильтров).</param>
        /// <param name="machine">Станок (для лимита КПД наладки).</param>
        public static K1Aggregates Aggregate(IReadOnlyList<Part> groupParts, IEnumerable<DateTime> shiftDates, string machine, bool includeSmallBatch)
        {
            var productionRatio = groupParts
                .Where(p => includeSmallBatch || !p.IsSmallBatch)
                .ProductionRatio();

            var setupsCount = groupParts.Count(p =>
                p.SetupRatio is not (0 or double.NaN or double.NegativeInfinity or double.PositiveInfinity));
            var productionsCount = groupParts.Count(p =>
                !IsInvalidRatio(p.ProductionRatio) && (includeSmallBatch || !p.IsSmallBatch));

            // Знаменатели: сколько наладок/изготовлений было предпринято вообще (факт есть),
            // а не только сколько из них засчиталось. Разница — записи без норматива, б/н, б/и и т.п.
            var totalSetupsCount = groupParts.Count(p => p.SetupTimeFact > 0);
            var totalProductionsCount = groupParts.Count(p => p.FinishedCount > 0 || p.ProductionTimeFact > 0);

            var downtimesEx = groupParts.SpecifiedDowntimesRatioExcluding(new[] { Downtime.HardwareFailure, Downtime.Mentoring, Downtime.Special });

            var total = TimeSpan.Zero;
            var included = TimeSpan.Zero;
            foreach (var p in groupParts)
            {
                total += p.FullWorkedTime();
                included += IncludedTime(p, includeSmallBatch);
            }

            return new K1Aggregates(
                groupParts.AverageSetupRatio(machine),
                productionRatio,
                setupsCount,
                productionsCount,
                totalSetupsCount,
                totalProductionsCount,
                downtimesEx,
                shiftDates.Distinct().Count(),
                included.TotalHours,
                total.TotalHours);
        }
    }

    /// <summary>К1 операторов за период по отметкам: единая точка расчёта для отчёта и окна в eLog.</summary>
    public static class OperatorKpiService
    {
        /// <summary>Минимум смен за период по регламенту: рабочие дни периода / 6.</summary>
        public static int RequiredShifts(DateTime from, DateTime to) => DomainSettings.GetWorkDaysBetween(from, to) / 6;

        public static IReadOnlyList<OperatorKpiRow> Calculate(OperatorKpiRequest request)
        {
            if (request.From > request.To)
                throw new ArgumentException("Начальная дата не может быть позже конечной");

            var parts = request.Parts as IReadOnlyCollection<Part> ?? request.Parts.ToList();
            var operators = request.Operators as IReadOnlyCollection<OperatorInfo> ?? request.Operators.ToList();
            var qualifications = request.Qualifications.OrderBy(q => q.Value).ToList();
            var requiredShifts = RequiredShifts(request.From, request.To);

            IEnumerable<Part> filtered = parts;
            if (!request.IncludeExcludedParts)
                filtered = filtered.Where(p => !p.ExcludeFromReports);
            if (request.SerialParts?.Count > 0)
            {
                var serialParts = new HashSet<string>(request.SerialParts);
                filtered = filtered.Where(p => serialParts.Contains(p.PartName.NormalizedPartNameWithoutComments()));
            }

            var shiftDates = parts
                .GroupBy(p => (p.Operator, p.Machine))
                .ToDictionary(g => g.Key, g => g.Select(p => p.ShiftDate).ToList());

            var rows = new List<OperatorKpiRow>();
            foreach (var group in filtered
                .GroupBy(p => (p.Operator, p.Machine))
                .OrderBy(g => g.Key.Machine)
                .ThenBy(g => g.Key.Operator))
            {
                var (operatorName, machine) = group.Key;
                if (operatorName.ToLower() == "ученик") continue;

                var qualificationNumber = operators.FirstOrDefault(o => o.FullName == operatorName)?.Qualification ?? 0;
                if (qualificationNumber == 0) continue;
                var qualification = qualifications.FirstOrDefault(q => q.Value == qualificationNumber);
                if (qualification is null) continue;

                var profile = request.Machines.TryGetValue(machine, out var p) ? p : MachineProfile.Unknown;
                var groupParts = group.ToList();
                var aggregates = K1Aggregator.Aggregate(groupParts, shiftDates[group.Key], machine, request.IncludeSmallBatch);

                var input = new K1Input(
                    qualification, profile.IsSerial, profile.DowntimeLimit,
                    aggregates.AverageSetupRatio, aggregates.ProductionRatio,
                    aggregates.SetupsCount, aggregates.ProductionsCount,
                    aggregates.SpecifiedDowntimesRatioEx, aggregates.WorkedShifts,
                    aggregates.IncludedHours, aggregates.TotalHours, requiredShifts);

                rows.Add(new OperatorKpiRow(operatorName, machine, qualification, profile, aggregates, input,
                    K1Calculator.Calculate(input, request.Rules), groupParts));
            }
            return rows;
        }
    }
}
