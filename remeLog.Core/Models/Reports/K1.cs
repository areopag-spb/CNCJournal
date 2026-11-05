using System;
using System.Collections.Generic;

namespace remeLog.Models.Reports
{
    /// <summary>
    /// Версионируемый набор правил К1. v1 в точности воспроизводит отчёт по операторам,
    /// выгруженный до выноса расчёта из Excel-экспорта (эталон — сентябрь 2026).
    /// Любое изменение логики расчёта — новая версия, а не правка существующей.
    /// </summary>
    public sealed record K1RuleSet
    {
        /// <summary>Номер версии правил (пишется в заметки к ячейкам отчёта).</summary>
        public int Version { get; init; }

        /// <summary>Допуск: доля учтённого времени от отмеченного, не меньше которой К1 считается.</summary>
        public double MinimumIncludedTimeRatio { get; init; } = 0.3;

        /// <summary>
        /// Допуск (с v2): среднее отмеченное время на смену, часов, не меньше. Смены без содержательной
        /// работы не должны давать К1 за один-два учтённых эпизода. null — условие не применяется.
        /// </summary>
        public double? MinimumMarkedHoursPerShift { get; init; }

        /// <summary>
        /// Несерийный станок: если изготовление ниже этой доли, К1 выше 1 не засчитывается (К1 пуст);
        /// К1, равный 1 или ниже, остаётся. Общее правило для всех программ — действует во всех версиях.
        /// </summary>
        public double NonSerialMinProductionRatio { get; init; } = 0.75;

        public static readonly K1RuleSet V1 = new() { Version = 1 };

        public static readonly K1RuleSet V2 = new() { Version = 2, MinimumMarkedHoursPerShift = 3.0 };

        /// <summary>Правила, по которым работает приложение сейчас.</summary>
        public static K1RuleSet Current => V2;
    }

    /// <summary>
    /// Агрегаты оператора на станке за период — всё, что нужно для К1 после того, как детали свёрнуты.
    /// Свёртка деталей (средние, доли, учтённое время) делается отдельно и здесь не повторяется.
    /// </summary>
    /// <param name="Qualification">Разряд оператора со ступенями коэффициентов.</param>
    /// <param name="IsSerialMachine">Станок серийный (cnc_machines.IsSerial).</param>
    /// <param name="DowntimeLimit">Граница простоев станка (cnc_machines.DowntimeLimit), доля. Ступень N шкалы
    /// простоев не может быть ниже неё: у SKT21 15% вместо общих 10%.</param>
    /// <param name="AverageSetupRatio">Наладка средняя.</param>
    /// <param name="ProductionRatio">Изготовление общее (с учётом режима «штучные партии»).</param>
    /// <param name="SetupsCount">Наладок, вошедших в расчёт.</param>
    /// <param name="ProductionsCount">Изготовлений, вошедших в расчёт.</param>
    /// <param name="SpecifiedDowntimesRatioEx">Простои без отказа оборудования, обучения и спец. простоя, доля.</param>
    /// <param name="WorkedShifts">Отработанные смены (уникальные даты смен) на этом станке.</param>
    /// <param name="IncludedHours">Время учтённых операций, часов.</param>
    /// <param name="TotalHours">Отмеченное общее время, часов.</param>
    /// <param name="RequiredShifts">Минимум смен за период (по регламенту — workDays/6).</param>
    public sealed record K1Input(
        Qualification Qualification,
        bool IsSerialMachine,
        double DowntimeLimit,
        double AverageSetupRatio,
        double ProductionRatio,
        int SetupsCount,
        int ProductionsCount,
        double SpecifiedDowntimesRatioEx,
        int WorkedShifts,
        double IncludedHours,
        double TotalHours,
        int RequiredShifts);

    /// <summary>Результат расчёта К1 по строке оператор × станок.</summary>
    /// <param name="GeneralRatio">Общий процент («Эффективность»), по которому выбирается ступень.</param>
    /// <param name="EfficiencyCoefficient">Коэффициент эффективности; null — допуск не пройден.</param>
    /// <param name="DowntimeCoefficient">Коэффициент простоев; null — допуск не пройден.</param>
    /// <param name="K1">Итоговый коэффициент; null — не считается, причины в <paramref name="Reasons"/>.</param>
    /// <param name="Reasons">Почему К1 не считается (пусто, если считается).</param>
    /// <param name="RuleVersion">Версия правил, по которым считали.</param>
    public sealed record K1Result(
        double GeneralRatio,
        double? EfficiencyCoefficient,
        double? DowntimeCoefficient,
        double? K1,
        IReadOnlyList<string> Reasons,
        int RuleVersion);
}
