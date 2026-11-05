using ClosedXML.Excel;
using remeLog.Models.Reports;

namespace remeLog.Core.Tests;

/// <summary>
/// Живые формулы отчёта (K1ExcelFormulas), вычисленные движком ClosedXML, обязаны совпадать с расчётом
/// приложения (K1Calculator) на всех строках эталона и на граничных случаях.
/// </summary>
public class K1ExcelFormulasTests
{
    private sealed record Evaluated(double General, double? Efficiency, double? Downtime, double? K1, string Check);

    // Колонки тестового листа.
    private const int ColQual = 2, ColSetup = 5, ColProd = 6, ColDown = 18, ColGeneral = 19, ColSetups = 20, ColProds = 21,
        ColShifts = 24, ColIncluded = 25, ColTotal = 26, ColEff = 27, ColDownCoeff = 28, ColK1 = 29, ColK1App = 30, ColCheck = 31,
        ColParams = 34;

    private static string A(int col, int row) => $"{K1ExcelFormulas.ColumnLetter(col)}{row}";

    private static List<Evaluated> Evaluate(IReadOnlyList<K1Input> inputs, K1RuleSet rules, bool fillAppValue = true)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Отчёт");
        var cs = wb.AddWorksheet(K1ExcelFormulas.CoefficientsSheet);

        var headers = K1ExcelFormulas.CoefficientHeaders;
        for (var i = 0; i < headers.Count; i++) cs.Cell(1, i + 1).Value = headers[i];
        var qualifications = Enumerable.Range(1, 6).Select(K1CalculatorTests.Qual).ToList();
        for (var r = 0; r < qualifications.Count; r++)
        {
            var values = K1ExcelFormulas.CoefficientRow(qualifications[r]);
            for (var c = 0; c < values.Count; c++) cs.Cell(r + 2, c + 1).Value = values[c];
        }

        var requiredShifts = inputs[0].RequiredShifts;
        Assert.All(inputs, i => Assert.Equal(requiredShifts, i.RequiredShifts));
        ws.Cell(2, ColParams).Value = rules.MinimumIncludedTimeRatio;
        ws.Cell(3, ColParams).Value = requiredShifts;
        ws.Cell(4, ColParams).Value = rules.MinimumMarkedHoursPerShift ?? 0;
        var p = new K1ExcelFormulas.ParamRefs(
            $"${K1ExcelFormulas.ColumnLetter(ColParams)}$2",
            $"${K1ExcelFormulas.ColumnLetter(ColParams)}$3",
            rules.MinimumMarkedHoursPerShift is null ? null : $"${K1ExcelFormulas.ColumnLetter(ColParams)}$4");

        for (var i = 0; i < inputs.Count; i++)
        {
            var row = 3 + i;
            var input = inputs[i];
            ws.Cell(row, ColQual).Value = input.Qualification.Value;
            ws.Cell(row, ColSetup).Value = input.AverageSetupRatio;
            ws.Cell(row, ColProd).Value = input.ProductionRatio;
            ws.Cell(row, ColDown).Value = input.SpecifiedDowntimesRatioEx;
            ws.Cell(row, ColSetups).Value = input.SetupsCount;
            ws.Cell(row, ColProds).Value = input.ProductionsCount;
            ws.Cell(row, ColShifts).Value = input.WorkedShifts;
            ws.Cell(row, ColIncluded).Value = input.IncludedHours;
            ws.Cell(row, ColTotal).Value = input.TotalHours;

            var calc = K1Calculator.Calculate(input, rules);
            if (fillAppValue && calc.K1 is double app) ws.Cell(row, ColK1App).Value = app;

            var refs = new K1ExcelFormulas.RowRefs(
                A(ColQual, row), A(ColSetup, row), A(ColProd, row), A(ColSetups, row), A(ColProds, row),
                A(ColDown, row), A(ColGeneral, row), A(ColShifts, row), A(ColIncluded, row), A(ColTotal, row),
                A(ColEff, row), A(ColDownCoeff, row), A(ColK1, row), A(ColK1App, row));
            var coefficientsRow = input.Qualification.Value + 1; // разряды 1..6 в строках 2..7

            ws.Cell(row, ColGeneral).FormulaA1 = K1ExcelFormulas.General(input.IsSerialMachine, refs);
            ws.Cell(row, ColEff).FormulaA1 = K1ExcelFormulas.Efficiency(input.IsSerialMachine, coefficientsRow, refs, p, rules);
            ws.Cell(row, ColDownCoeff).FormulaA1 = K1ExcelFormulas.Downtime(coefficientsRow, input.DowntimeLimit, refs, p, rules);
            ws.Cell(row, ColK1).FormulaA1 = K1ExcelFormulas.K1(input.IsSerialMachine, refs, rules);
            ws.Cell(row, ColCheck).FormulaA1 = K1ExcelFormulas.Check(refs);
        }

        double? Num(IXLCell c)
        {
            XLCellValue v;
            try { v = c.Value; }
            catch (Exception e) { throw new InvalidOperationException($"{c.Address}: {c.FormulaA1} -> {e.Message}", e); }
            return v.IsNumber ? v.GetNumber() : null;
        }
        string Text(IXLCell c)
        {
            try { return c.Value.ToString(); }
            catch (Exception e) { throw new InvalidOperationException($"{c.Address}: {c.FormulaA1} -> {e.Message}", e); }
        }
        return Enumerable.Range(0, inputs.Count).Select(i =>
        {
            var row = 3 + i;
            return new Evaluated(
                Num(ws.Cell(row, ColGeneral)) ?? double.NaN,
                Num(ws.Cell(row, ColEff)), Num(ws.Cell(row, ColDownCoeff)), Num(ws.Cell(row, ColK1)),
                Text(ws.Cell(row, ColCheck)));
        }).ToList();
    }

    private static void AssertSame(K1Input input, K1Result calc, Evaluated excel, string who)
    {
        Assert.True(Math.Abs(calc.GeneralRatio - excel.General) < 1e-9, $"{who}: общий процент {calc.GeneralRatio} vs формула {excel.General}");
        Assert.True(calc.EfficiencyCoefficient == excel.Efficiency, $"{who}: коэффициент эффективности {calc.EfficiencyCoefficient} vs формула {excel.Efficiency}");
        Assert.True(calc.DowntimeCoefficient == excel.Downtime, $"{who}: коэффициент простоев {calc.DowntimeCoefficient} vs формула {excel.Downtime}");
        Assert.True(calc.K1 == excel.K1, $"{who}: К1 {calc.K1} vs формула {excel.K1}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Formulas_MatchCalculator_OnSeptemberEtalon(int version)
    {
        var rules = version == 1 ? K1RuleSet.V1 : K1RuleSet.V2;
        var rows = K1CalculatorTests.LoadEtalon();
        var inputs = rows.Select(r => r.ToInput()).ToList();
        var excel = Evaluate(inputs, rules);

        for (var i = 0; i < rows.Count; i++)
        {
            var who = $"{rows[i].Operator} / {rows[i].Machine}";
            AssertSame(inputs[i], K1Calculator.Calculate(inputs[i], rules), excel[i], who);
            Assert.Equal("✓", excel[i].Check);
        }
    }

    [Fact]
    public void Formulas_MatchCalculator_OnBoundaryCases()
    {
        var inputs = new List<K1Input>();
        var baseInput = K1CalculatorTests.SerialInput();
        foreach (var serial in new[] { true, false })
        foreach (var qual in new[] { 1, 3, 5 })
        foreach (var setup in new[] { 0.64, 0.65, 0.76, 0.85, 0.9, 0.95, 1.06 })
        foreach (var production in new[] { 0.7, 0.75, 1.0 })
        foreach (var downtime in new[] { 0.0, 0.049, 0.05, 0.099, 0.1, 0.1499, 0.15, 0.3 })
        foreach (var limit in new[] { 0.10, 0.15 })
            inputs.Add(baseInput with
            {
                Qualification = K1CalculatorTests.Qual(qual), IsSerialMachine = serial, AverageSetupRatio = setup,
                ProductionRatio = production, SpecifiedDowntimesRatioEx = downtime, DowntimeLimit = limit,
            });

        // Допуски: смены, доля учтённого, часы на смену — по обе стороны границ.
        foreach (var (shifts, included, total) in new[]
        {
            (4, 60.0, 70.0), (5, 60.0, 70.0), (5, 21.0, 70.0), (5, 20.9, 70.0), (5, 15.0, 15.0), (5, 14.9, 14.9),
            (6, 18.0, 18.0), (6, 17.9, 17.9), (1, 3.0, 3.0), (0, 0.0, 0.0), (10, 0.0, 14.5),
        })
            inputs.Add(baseInput with { WorkedShifts = shifts, IncludedHours = included, TotalHours = total });

        foreach (var rules in new[] { K1RuleSet.V1, K1RuleSet.V2 })
        {
            var excel = Evaluate(inputs, rules);
            for (var i = 0; i < inputs.Count; i++)
                AssertSame(inputs[i], K1Calculator.Calculate(inputs[i], rules), excel[i], $"v{rules.Version} случай {i}");
        }
    }

    [Fact]
    public void Check_ShowsMismatch_WhenAppValueDiffers_AndWhenMissing()
    {
        var input = K1CalculatorTests.SerialInput();
        // Значение приложения не записано, а формула К1 посчитала число: расхождение.
        var missing = Evaluate(new[] { input }, K1RuleSet.V1, fillAppValue: false);
        Assert.NotNull(missing[0].K1);
        Assert.Equal("≠", missing[0].Check);
    }

    [Fact]
    public void CoefficientRow_HasSameWidthAsHeaders()
    {
        Assert.Equal(K1ExcelFormulas.CoefficientHeaders.Count, K1ExcelFormulas.CoefficientRow(K1CalculatorTests.Qual(3)).Count);
        Assert.Equal(37, K1ExcelFormulas.CoefficientHeaders.Count);
        Assert.Equal("AK", K1ExcelFormulas.ColumnLetter(37));
        Assert.Equal("Z", K1ExcelFormulas.ColumnLetter(26));
        Assert.Equal("AA", K1ExcelFormulas.ColumnLetter(27));
    }
}
