using System.Globalization;
using remeLog.Models.Reports;

namespace remeLog.Core.Tests;

/// <summary>
/// Расчёт К1. Эталон — отчёт по операторам за сентябрь 2026 (17 станков, «расчёт от исправленного»),
/// выгруженный до выноса расчёта из Excel-экспорта: ФИО заменены на «Оператор NN».
/// v1 обязан воспроизвести его построчно, v2 отличается ровно тремя строками.
/// </summary>
public class K1CalculatorTests
{
    private const double Tolerance = 1e-9;

    // Таблица коэффициентов, как на листе «Коэффициенты» эталонного файла.
    private static readonly double[] EffCoeffs = { 1.4, 1.2, 1, 0.8, 0.6, 0.5 };
    private static readonly double[] DownValues = { 0.05, 0.1, 0.1, 1, 1, 1 };
    private static readonly double[] DownCoeffs = { 1, 1, 1, 0.8, 0.8, 0.8 };
    private static readonly double[] NonSerialValues = { 1.05, 0.95, 0.85, 0.75, 0.65, 0 };

    private static readonly Dictionary<int, double[]> EffValues = new()
    {
        [1] = new[] { 1.05, 0.95, 0.85, 0.75, 0.65, 0 },
        [2] = new[] { 1.05, 0.95, 0.85, 0.75, 0.65, 0 },
        [3] = new[] { 1.1, 1, 0.9, 0.8, 0.7, 0 },
        [4] = new[] { 1.1, 1, 0.9, 0.8, 0.7, 0 },
        [5] = new[] { 1.15, 1.05, 0.95, 0.85, 0.75, 0 },
        [6] = new[] { 1.15, 1.05, 0.95, 0.85, 0.75, 0 },
    };

    internal static Qualification Qual(int value) => new()
    {
        Value = value,
        Efficiency = Eff(EffValues[value]),
        DownTimes = Down(),
        NonSerialEfficiency = NonSerial(),
    };

    private static EfficiencyGroup Eff(double[] v) =>
        new(L(v, EffCoeffs, 0), L(v, EffCoeffs, 1), L(v, EffCoeffs, 2), L(v, EffCoeffs, 3), L(v, EffCoeffs, 4), L(v, EffCoeffs, 5));

    private static DownTimesGroup Down() =>
        new(L(DownValues, DownCoeffs, 0), L(DownValues, DownCoeffs, 1), L(DownValues, DownCoeffs, 2),
            L(DownValues, DownCoeffs, 3), L(DownValues, DownCoeffs, 4), L(DownValues, DownCoeffs, 5));

    private static NonSerialEfficiencyGroup NonSerial() =>
        new(L(NonSerialValues, EffCoeffs, 0), L(NonSerialValues, EffCoeffs, 1), L(NonSerialValues, EffCoeffs, 2),
            L(NonSerialValues, EffCoeffs, 3), L(NonSerialValues, EffCoeffs, 4), L(NonSerialValues, EffCoeffs, 5));

    private static LevelValue L(double[] values, double[] coeffs, int i) => new(values[i], coeffs[i]);

    internal sealed record EtalonRow(
        string Operator, int Qualification, string Machine, bool Serial,
        double AverageSetupRatio, double ProductionRatio, double DowntimeEx,
        int SetupsCount, int ProductionsCount, int Shifts, double IncludedHours, double TotalHours,
        double General, double? Efficiency, double? Downtime, double? K1)
    {
        public K1Input ToInput() => new(
            Qual(Qualification),
            Serial,
            // Граница простоев SKT21 — 15%, у остальных станков 10% (cnc_machines.DowntimeLimit).
            Machine.Contains("SKT21") ? 0.15 : 0.10,
            AverageSetupRatio, ProductionRatio, SetupsCount, ProductionsCount,
            DowntimeEx, Shifts, IncludedHours, TotalHours,
            RequiredShifts: 5);
    }

    private static double? Opt(string s) => s == "" ? null : double.Parse(s, CultureInfo.InvariantCulture);

    internal static List<EtalonRow> LoadEtalon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "k1_etalon_2026-09.csv");
        return File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0).Select(l =>
        {
            var c = l.Split(';');
            double D(int i) => double.Parse(c[i], CultureInfo.InvariantCulture);
            return new EtalonRow(c[0], int.Parse(c[1]), c[2], c[3] == "1",
                D(4), D(5), D(17), int.Parse(c[19]), int.Parse(c[20]), int.Parse(c[23]), D(24), D(25),
                D(18), Opt(c[26]), Opt(c[27]), Opt(c[28]));
        }).ToList();
    }

    private static void AssertNear(double? expected, double? actual, string what)
    {
        Assert.True(expected.HasValue == actual.HasValue, $"{what}: ожидалось {expected?.ToString() ?? "пусто"}, получено {actual?.ToString() ?? "пусто"}");
        if (expected.HasValue)
            Assert.True(Math.Abs(expected.Value - actual!.Value) < Tolerance, $"{what}: ожидалось {expected}, получено {actual}");
    }

    [Fact]
    public void Etalon_HasAllRows()
    {
        Assert.Equal(51, LoadEtalon().Count);
    }

    [Fact]
    public void V1_ReproducesSeptemberReport()
    {
        foreach (var row in LoadEtalon())
        {
            var r = K1Calculator.Calculate(row.ToInput(), K1RuleSet.V1);
            var who = $"{row.Operator} / {row.Machine}";
            Assert.True(Math.Abs(row.General - r.GeneralRatio) < Tolerance, $"{who}: общий процент {row.General} vs {r.GeneralRatio}");
            AssertNear(row.Efficiency, r.EfficiencyCoefficient, who + " коэффициент эффективности");
            AssertNear(row.Downtime, r.DowntimeCoefficient, who + " коэффициент простоев");
            AssertNear(row.K1, r.K1, who + " К1");
        }
    }

    [Fact]
    public void V2_DiffersFromV1_ByExactlyThreeRows()
    {
        // Оператор 08 — Hyundai XH6300 (одна наладка за 6 пустых смен);
        // Оператор 06 — Rontek HTC420 №1 и №2 (по 1.7 и 2.2 ч отмеченного времени на смену).
        var cut = new HashSet<(string, string)>
        {
            ("Оператор 08", "Hyundai XH6300"),
            ("Оператор 06", "Rontek HTC420 №1"),
            ("Оператор 06", "Rontek HTC420 №2"),
        };

        var changed = new List<(string, string)>();
        foreach (var row in LoadEtalon())
        {
            var v1 = K1Calculator.Calculate(row.ToInput(), K1RuleSet.V1);
            var v2 = K1Calculator.Calculate(row.ToInput(), K1RuleSet.V2);
            if (v1.K1 != v2.K1)
            {
                changed.Add((row.Operator, row.Machine));
                Assert.NotNull(v1.K1);
                Assert.Null(v2.K1);
                Assert.Contains(v2.Reasons, s => s.StartsWith("Среднее отмеченное время на смену"));
            }
        }

        Assert.Equal(cut.OrderBy(x => x).ToList(), changed.OrderBy(x => x).ToList());
        Assert.Equal(30, LoadEtalon().Count(r => r.K1.HasValue));
    }

    [Fact]
    public void Reasons_ExplainEveryBlankK1()
    {
        foreach (var row in LoadEtalon().Where(r => !r.K1.HasValue))
        {
            var r = K1Calculator.Calculate(row.ToInput(), K1RuleSet.V1);
            Assert.NotEmpty(r.Reasons);
        }
    }

    internal static K1Input SerialInput(int qualification = 3, double setup = 1, double production = 1,
        int setups = 4, int productions = 4, double downtime = 0.05, double machineLimit = 0.10) => new(
        Qual(qualification), true, machineLimit, setup, production, setups, productions, downtime,
        WorkedShifts: 10, IncludedHours: 60, TotalHours: 70, RequiredShifts: 5);

    [Fact]
    public void Serial_FirstQualification_UsesProductionOnly()
    {
        var r = K1Calculator.Calculate(SerialInput(qualification: 1, setup: 0.5, production: 1.2), K1RuleSet.V1);
        Assert.Equal(1.2, r.GeneralRatio, 12);
    }

    [Fact]
    public void Serial_OtherQualifications_WeightBySetupsAndProductions()
    {
        var r = K1Calculator.Calculate(SerialInput(setup: 1.0, production: 0.8, setups: 1, productions: 3), K1RuleSet.V1);
        Assert.Equal((1.0 * 1 + 0.8 * 3) / 4, r.GeneralRatio, 12);
    }

    [Fact]
    public void Serial_NoOperations_GeneralRatioIsZero_NotNaN()
    {
        var r = K1Calculator.Calculate(SerialInput(setups: 0, productions: 0), K1RuleSet.V1);
        Assert.Equal(0, r.GeneralRatio);
        Assert.Equal(0.5, r.EfficiencyCoefficient);
    }

    [Fact]
    public void NonSerial_UsesSetupRatioAndOwnScale()
    {
        var input = SerialInput(setup: 0.96, production: 0.2) with { IsSerialMachine = false };
        var r = K1Calculator.Calculate(input, K1RuleSet.V1);
        Assert.Equal(0.96, r.GeneralRatio, 12);
        Assert.Equal(1.2, r.EfficiencyCoefficient); // 0.96 > 0.95 (NonSerial H)
    }

    [Fact]
    public void NonSerial_LowProduction_BlanksK1_WhenK1AboveOne()
    {
        var input = SerialInput(setup: 1.06, production: 0.7) with { IsSerialMachine = false };
        var r = K1Calculator.Calculate(input, K1RuleSet.V1);
        Assert.Equal(1.4, r.EfficiencyCoefficient);
        Assert.Null(r.K1);
        Assert.Contains(r.Reasons, s => s.Contains("несерийном"));
    }

    [Fact]
    public void NonSerial_LowProduction_KeepsK1_WhenK1IsOne()
    {
        var input = SerialInput(setup: 0.9, production: 0.5) with { IsSerialMachine = false };
        var r = K1Calculator.Calculate(input, K1RuleSet.V1);
        Assert.Equal(1.0, r.EfficiencyCoefficient);
        Assert.Equal(1.0, r.K1);
    }

    [Fact]
    public void NonSerial_LowProduction_KeepsK1_WhenK1BelowOne_EvenWithEfficiencyAboveOne()
    {
        // 1.2 × 0.8 = 0.96: коэффициент эффективности выше 1, но итоговый К1 ниже — остаётся.
        var input = SerialInput(setup: 0.96, production: 0.5, downtime: 0.12) with { IsSerialMachine = false };
        var r = K1Calculator.Calculate(input, K1RuleSet.V1);
        Assert.Equal(1.2, r.EfficiencyCoefficient);
        Assert.Equal(0.8, r.DowntimeCoefficient);
        Assert.Equal(0.96, r.K1!.Value, 12);
        Assert.Empty(r.Reasons);
    }

    [Fact]
    public void NonSerial_NormalProduction_KeepsK1AboveOne()
    {
        var input = SerialInput(setup: 1.06, production: 0.8) with { IsSerialMachine = false };
        Assert.Equal(1.4, K1Calculator.Calculate(input, K1RuleSet.V1).K1);
    }

    [Fact]
    public void Serial_LowProduction_DoesNotCutK1()
    {
        var input = SerialInput(setup: 1.2, production: 0.5);
        Assert.NotNull(K1Calculator.Calculate(input, K1RuleSet.V1).K1);
    }

    [Theory]
    [InlineData(0.04, 1.0)]
    [InlineData(0.099, 1.0)]
    [InlineData(0.10, 0.8)]   // граница строгая: ровно 10% — уже не «меньше»
    [InlineData(0.12, 0.8)]
    public void Downtime_Ladder_StrictBoundary(double downtime, double expected)
    {
        var r = K1Calculator.Calculate(SerialInput(downtime: downtime), K1RuleSet.V1);
        Assert.Equal(expected, r.DowntimeCoefficient);
    }

    [Theory]
    [InlineData(0.12, 0.10, 0.8)]
    [InlineData(0.12, 0.15, 1.0)]  // SKT21: до 15% — коэффициент 1
    [InlineData(0.15, 0.15, 0.8)]
    [InlineData(0.12, 0.05, 0.8)]  // граница станка ниже шкалы разряда шкалу не опускает
    public void Downtime_MachineLimit_RaisesStepN(double downtime, double machineLimit, double expected)
    {
        var r = K1Calculator.Calculate(SerialInput(downtime: downtime, machineLimit: machineLimit), K1RuleSet.V1);
        Assert.Equal(expected, r.DowntimeCoefficient);
    }

    [Fact]
    public void Eligibility_NotEnoughShifts_ExplainedAndBlank()
    {
        var input = SerialInput() with { WorkedShifts = 4 };
        var r = K1Calculator.Calculate(input, K1RuleSet.V1);
        Assert.Null(r.K1);
        Assert.Contains("Недостаточно смен (минимум 5)", r.Reasons);
    }

    [Fact]
    public void Eligibility_IncludedTimeShare_BoundaryInclusive()
    {
        var input = SerialInput() with { IncludedHours = 30, TotalHours = 100 };
        Assert.NotNull(K1Calculator.Calculate(input, K1RuleSet.V1).K1);

        var below = input with { IncludedHours = 29.9 };
        var r = K1Calculator.Calculate(below, K1RuleSet.V1);
        Assert.Null(r.K1);
        Assert.Contains(r.Reasons, s => s.StartsWith("Учтено менее 30%"));
    }

    [Fact]
    public void V2_HoursPerShift_BoundaryInclusive_AndAveragedOverShifts()
    {
        // Смены 2 + 8 + 8 часов дают среднее 6 — короткая смена не мешает; считается по всем.
        var ok = SerialInput() with { WorkedShifts = 5, IncludedHours = 14, TotalHours = 15 };
        Assert.NotNull(K1Calculator.Calculate(ok, K1RuleSet.V2).K1);

        var low = ok with { TotalHours = 14.9, IncludedHours = 14 };
        var r = K1Calculator.Calculate(low, K1RuleSet.V2);
        Assert.Null(r.K1);
        Assert.Contains(r.Reasons, s => s.StartsWith("Среднее отмеченное время на смену 2.98 ч"));

        Assert.NotNull(K1Calculator.Calculate(low, K1RuleSet.V1).K1);
    }

    [Fact]
    public void RuleVersion_IsReportedInResult()
    {
        Assert.Equal(1, K1Calculator.Calculate(SerialInput(), K1RuleSet.V1).RuleVersion);
        Assert.Equal(2, K1Calculator.Calculate(SerialInput(), K1RuleSet.V2).RuleVersion);
        Assert.Equal(2, K1RuleSet.Current.Version);
    }
}
