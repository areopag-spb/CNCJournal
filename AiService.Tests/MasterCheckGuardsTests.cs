using AiService.Models;
using AiService.Services;

namespace AiService.Tests;

/// <summary>
/// Детерминированные гарды verify-part: известные пустые формулировки.
/// Живые кейсы 10.2026: SKT21 «нет норм», HTC650M «ТП на 230» и «работа с технологом».
/// </summary>
public class MasterCheckGuardsTests
{
    private static PartContext Part(string setupReason, string setupDetail, string machiningReason, string machiningDetail) =>
        new() { MasterSetupComment = setupReason, MasterSetupDetail = setupDetail, MasterMachiningComment = machiningReason, MasterMachiningDetail = machiningDetail };

    private static List<VerifyAnomaly> Anomaly(string field) =>
        [new VerifyAnomaly { Field = field }];

    [Fact]
    public void NormativeAtDrugoe_LiveCase_Fires()
    {
        // SKT21 №104 20.09: изготовление «Другое» + «нет норм». Норматив изготовления
        // отсутствовать не может — замечание не отсылает к причинам про нормативы.
        var part = Part("Отсутствие нормативов", "", "Другое", "нет норм");
        Assert.Equal(MasterCheckGuards.MachiningNoNormRemark,
            MasterCheckGuards.CheckKnownFalsePattern(part, Anomaly(nameof(PartContext.MasterMachiningDetail))));
    }

    [Fact]
    public void NormativeAtDrugoe_SetupSide_Fires()
    {
        var part = Part("Другое", "норматива нет", "", "");
        Assert.Equal(MasterCheckGuards.NormativeRemark,
            MasterCheckGuards.CheckKnownFalsePattern(part, Anomaly(nameof(PartContext.MasterSetupDetail))));
    }

    [Fact]
    public void NormativeAtDrugoe_Variants_Fire()
    {
        foreach (var detail in new[] { "Нет норм ", "норм нет", "отсутствие нармативов", "нет  нормативов" })
        {
            var part = Part("Другое", detail, "", "");
            Assert.Equal(MasterCheckGuards.NormativeRemark,
                MasterCheckGuards.CheckKnownFalsePattern(part, Anomaly(nameof(PartContext.MasterSetupDetail))));
        }
    }

    [Fact]
    public void NormativeAtDrugoe_Negatives_Null()
    {
        // Не «Другое» — не наша юрисдикция.
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(
            Part("Освоение", "нет норм", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail))));
        // «Нормально» — другое слово, не стем «норм».
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(
            Part("Другое", "всё нормально", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail))));
        // M5-кейс решает модель, не гард.
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(
            Part("Другое", "изготовление оправки", "Другое", "изготовление оправки"),
            Anomaly(nameof(PartContext.MasterSetupDetail))));
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(
            Part("Другое", "", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail))));
    }

    [Fact]
    public void Cryptogram_Tp_LiveCase_Fires()
    {
        // HTC650M 03.09: «Другое» + «ТП на 230» с обеих сторон.
        var part = Part("Другое", "ТП на 230", "Другое", "ТП на 230");
        Assert.Equal(MasterCheckGuards.TpRemark,
            MasterCheckGuards.CheckKnownFalsePattern(part, Anomaly(nameof(PartContext.MasterSetupDetail))));
    }

    [Fact]
    public void Cryptogram_Tp_NormalizedVariants_Fire()
    {
        foreach (var detail in new[] { "  тп  на  230 ", "ТП на 230.", "тп на 230" })
        {
            var part = Part("Другое", detail, "", "");
            Assert.Equal(MasterCheckGuards.TpRemark,
                MasterCheckGuards.CheckKnownFalsePattern(part, Anomaly(nameof(PartContext.MasterSetupDetail))));
        }
    }

    [Fact]
    public void Cryptogram_Tp_NearbyText_Null()
    {
        // Другой номер / продолжение фразы — решает модель.
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(
            Part("Другое", "ТП на 231", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail))));
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(
            Part("Другое", "ТП на 230 выполнен", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail))));
    }

    [Fact]
    public void Cryptogram_Technologist_LiveCase_Fires()
    {
        // HTC650M 25.09: «Другое» + «работа с технологом».
        var part = Part("Другое", "работа с технологом", "", "");
        Assert.Equal(MasterCheckGuards.TechnologistRemark,
            MasterCheckGuards.CheckKnownFalsePattern(part, Anomaly(nameof(PartContext.MasterSetupDetail))));
    }

    [Fact]
    public void Cryptogram_Technologist_NearbyText_Null()
    {
        // Другой глагол/число и M-пример с подгонкой — решает модель.
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(
            Part("Другое", "работал с технологом", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail))));
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(
            Part("Другое", "Работа с технологами, подгон размеров", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail))));
    }

    [Fact]
    public void UnknownFieldOrEmpty_Null()
    {
        var part = Part("Другое", "ТП на 230", "Другое", "нет норм");
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(part, Anomaly("MasterComment")));
        Assert.Null(MasterCheckGuards.CheckKnownFalsePattern(part, []));
    }

    [Fact]
    public void NormativeAtDrugoe_SuggestsReasonForItsOwnField()
    {
        var missing = MasterCheckGuards.Check(
            Part("Другое", "нет норм", "", ""),
            Anomaly(nameof(PartContext.MasterSetupDetail)));
        Assert.Equal(MasterCheckGuards.NormativeRemark, missing!.Remark);
        Assert.Equal(MasterCheckGuards.MissingNormativesReason, missing.SuggestedReason);
        Assert.Equal(nameof(PartContext.MasterSetupComment), missing.SuggestedField);

        var machiningIncorrect = MasterCheckGuards.Check(
            Part("", "", "Другое", "нормативы завышены"),
            Anomaly(nameof(PartContext.MasterMachiningDetail)));
        Assert.Equal(MasterCheckGuards.IncorrectNormativesReason, machiningIncorrect!.SuggestedReason);
        Assert.Equal(nameof(PartContext.MasterMachiningComment), machiningIncorrect.SuggestedField);

        var setup = MasterCheckGuards.Check(
            Part("Другое", "нормативы занижены", "", ""),
            Anomaly(nameof(PartContext.MasterSetupDetail)));
        Assert.Equal(MasterCheckGuards.IncorrectNormativesReason, setup!.SuggestedReason);
        Assert.Equal(nameof(PartContext.MasterSetupComment), setup.SuggestedField);
    }

    [Fact]
    public void MachiningNoNorm_NeverSuggestsNormativeReason()
    {
        // Норматив изготовления отсутствовать не может: без других слов мастера предлагать нечего.
        var bare = MasterCheckGuards.Check(
            Part("Отсутствие нормативов", "", "Другое", "нет норм"),
            Anomaly(nameof(PartContext.MasterMachiningDetail)));
        Assert.Equal(MasterCheckGuards.MachiningNoNormRemark, bare!.Remark);
        Assert.Null(bare.SuggestedReason);
    }

    [Fact]
    public void MachiningNoNorm_RowNamesProcessDeviation_SuggestsIt()
    {
        // VMC40C 08.11.2026: наладка «Отсутствие нормативов» + «деталь с универсального участка».
        var byText = MasterCheckGuards.Check(
            Part("Отсутствие нормативов", "деталь с универсального участка", "Другое", "нет норм"),
            Anomaly(nameof(PartContext.MasterMachiningDetail)));
        Assert.Equal(MasterCheckGuards.MachiningNoNormRemark, byText!.Remark);
        Assert.Equal(MasterCheckGuards.ProcessDeviationReason, byText.SuggestedReason);
        Assert.Equal(nameof(PartContext.MasterMachiningComment), byText.SuggestedField);

        // HTC420 №1 07.11.2026: причина наладки уже «не по техпроцессу» («деталь с гудвея»).
        var byReason = MasterCheckGuards.Check(
            Part("Изготовление не по техпроцессу", "деталь с гудвея", "Другое", "нет норм"),
            Anomaly(nameof(PartContext.MasterMachiningDetail)));
        Assert.Equal(MasterCheckGuards.ProcessDeviationReason, byReason!.SuggestedReason);
    }

    [Fact]
    public void SetupNoNorm_RowNamesProcessDeviation_SuggestsItInsteadOfMissingNormatives()
    {
        var hit = MasterCheckGuards.Check(
            Part("Другое", "нет норм", "Изготовление не по техпроцессу", "заложена токарная операция"),
            Anomaly(nameof(PartContext.MasterSetupDetail)));
        Assert.Equal(MasterCheckGuards.ProcessDeviationReason, hit!.SuggestedReason);
        Assert.Equal(nameof(PartContext.MasterSetupComment), hit.SuggestedField);
    }

    [Fact]
    public void NormativeReason_WithDeviationInItsComment_Contradiction()
    {
        // VMC40C 08.11.2026: наладка «Отсутствие нормативов» + «деталь с универсального участка».
        var part = Part("Отсутствие нормативов", "деталь с универсального участка", "Другое", "нет норм");
        var hit = MasterCheckGuards.CheckNormativeReasonContradiction(
            part, Anomaly(nameof(PartContext.MasterSetupDetail)));
        Assert.Equal(MasterCheckGuards.DeviationUnderNormativeReasonRemark, hit!.Remark);
        Assert.Equal(MasterCheckGuards.ProcessDeviationReason, hit.SuggestedReason);
        Assert.Equal(nameof(PartContext.MasterSetupComment), hit.SuggestedField);

        // Причина про нормативы без слов об отклонении — не трогаем (решает прежний порядок).
        Assert.Null(MasterCheckGuards.CheckNormativeReasonContradiction(
            Part("Отсутствие нормативов", "", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail))));
        Assert.Null(MasterCheckGuards.CheckNormativeReasonContradiction(
            Part("Отсутствие нормативов", "новая деталь, техкарты ещё нет", "", ""),
            Anomaly(nameof(PartContext.MasterSetupDetail))));
        // Отклонение названо при другой причине — этот гард не про неё.
        Assert.Null(MasterCheckGuards.CheckNormativeReasonContradiction(
            Part("Освоение", "деталь с других станков", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail))));
    }

    [Fact]
    public void RowNamesProcessDeviation_Phrases()
    {
        Assert.True(MasterCheckGuards.RowNamesProcessDeviation(Part("Другое", "Заложена универсальная операция", "", "")));
        Assert.True(MasterCheckGuards.RowNamesProcessDeviation(Part("Другое", "заготовка с ручного участка", "", "")));
        Assert.True(MasterCheckGuards.RowNamesProcessDeviation(Part("", "", "Другое", "деталь с других станков")));
        Assert.False(MasterCheckGuards.RowNamesProcessDeviation(Part("Отсутствие нормативов", "", "Другое", "нет норм")));
        Assert.False(MasterCheckGuards.RowNamesProcessDeviation(Part("Другое", "универсальный ключ сломан", "", "")));
    }

    [Fact]
    public void OtherGuards_GiveNoSuggestion()
    {
        var cryptogram = MasterCheckGuards.Check(
            Part("Другое", "ТП на 230", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail)));
        Assert.Equal(MasterCheckGuards.TpRemark, cryptogram!.Remark);
        Assert.Null(cryptogram.SuggestedReason);

        var empty = MasterCheckGuards.Check(
            Part("", "", "", ""), Anomaly(nameof(PartContext.MasterSetupDetail)));
        Assert.Equal(MasterCheckGuards.EmptySetupRemark, empty!.Remark);
        Assert.Null(empty.SuggestedReason);
    }
}
