using remeLog.Core;
using remeLog.Models;

namespace remeLog.Core.Tests;

/// <summary>
/// Предложение ИИ-проверки сменить причину (замечание «текст про нормативы при „Другое“»):
/// живёт только в памяти, принимается явным действием пользователя и только для причины
/// из справочника своей категории.
/// </summary>
public class PartAiCheckSuggestionTests
{
    public PartAiCheckSuggestionTests()
    {
        DomainSettings.SetupReasons = new List<(string Reason, bool RequireComment)>
        {
            ("Отсутствие нормативов", false),
            ("Другое", true),
        };
        DomainSettings.MachiningReasons = new List<(string Reason, bool RequireComment)>
        {
            ("Некорректные нормативы", false),
            ("Другое", true),
        };
        DomainSettings.MaxSetupLimit = 2;
    }

    private static Part BuildPart(string setupReason = "Другое", string machiningReason = "Другое")
    {
        var now = DateTime.Now;
        return new Part(
            Guid.NewGuid(), "Станок1", "День", DateTime.Today,
            "Иванов", "Деталь 1", "ПР2601-00001.1.1", 1,
            0, 0, 0,
            now, now, 0, now,
            0, 0, 5, 0,
            TimeSpan.Zero,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            "",
            setupReason, machiningReason,
            "", "",
            "", "нет норм", "нет норм");
    }

    [Fact]
    public void Suggestion_KnownReasonOfCategory_IsKeptAndApplied()
    {
        var part = BuildPart();

        part.SetAiCheckSuggestion("Отсутствие нормативов", nameof(Part.MasterSetupComment));
        Assert.Equal("Отсутствие нормативов", part.AiCheckSuggestedReason);

        Assert.True(part.ApplyAiCheckSuggestion());
        Assert.Equal("Отсутствие нормативов", part.MasterSetupComment);
        Assert.Equal("Другое", part.MasterMachiningComment);
        Assert.Equal(string.Empty, part.AiCheckSuggestedReason);
    }

    [Fact]
    public void Suggestion_NormativeReasonMissingInCategory_IsNotReplacedByAnother()
    {
        var part = BuildPart();

        // «Отсутствия нормативов» у изготовления нет, а «Некорректные нормативы» — другая причина
        // (нормативы есть, но неверны): подставлять её вместо отсутствующей нельзя.
        part.SetAiCheckSuggestion("Отсутствие нормативов", nameof(Part.MasterMachiningComment));

        Assert.Equal(string.Empty, part.AiCheckSuggestedReason);
        Assert.Equal("Другое", part.MasterMachiningComment);
    }

    [Fact]
    public void Suggestion_ReasonMissingInCategoryList_IsDropped()
    {
        var part = BuildPart();

        part.SetAiCheckSuggestion("Освоение", nameof(Part.MasterMachiningComment));

        Assert.Equal(string.Empty, part.AiCheckSuggestedReason);
        Assert.False(part.ApplyAiCheckSuggestion());
        Assert.Equal("Другое", part.MasterMachiningComment);
    }

    [Fact]
    public void Suggestion_AlreadySelectedOrUnknownField_IsDropped()
    {
        var part = BuildPart(setupReason: "Отсутствие нормативов");

        part.SetAiCheckSuggestion("Отсутствие нормативов", nameof(Part.MasterSetupComment));
        Assert.Equal(string.Empty, part.AiCheckSuggestedReason);

        part.SetAiCheckSuggestion("Некорректные нормативы", "MasterComment");
        Assert.Equal(string.Empty, part.AiCheckSuggestedReason);
    }

    [Fact]
    public void Suggestion_DoesNotMarkRowChanged_UntilApplied()
    {
        var part = BuildPart();
        part.NeedUpdate = false;

        part.SetAiCheckSuggestion("Некорректные нормативы", nameof(Part.MasterMachiningComment));

        Assert.False(part.NeedUpdate);
        Assert.Equal("Некорректные нормативы", part.AiCheckSuggestedReason);
    }
}
