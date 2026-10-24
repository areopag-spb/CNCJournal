using AiService.Services;

namespace AiService.Tests;

public class ThinkingConsistencyTests
{
    private static string Padded(string tail) => new string('а', 300) + " " + tail;

    [Theory]
    [InlineData("Итого: это требует эскалации.")]
    [InlineData("Вывод: требуется эскалация на СГТ-2.")]
    [InlineData("Следует эскалировать сутки.")]
    public void Affirmative_Conclusion_Detected(string tail) =>
        Assert.True(ThinkingConsistency.HasEscalationConclusion(Padded(tail)));

    [Theory]
    [InlineData("Итого: это не требует эскалации.")]
    [InlineData("Вывод: эскалация не требуется, всё объяснено.")]
    [InlineData("Нет необходимости: не необходимо эскалировать.")]
    public void Negated_Conclusion_Ignored(string tail) =>
        Assert.False(ThinkingConsistency.HasEscalationConclusion(Padded(tail)));

    [Fact]
    public void Marker_In_First_Two_Thirds_Ignored()
    {
        var thinking = "Если бы это требовало эскалации, то да. " + new string('б', 600);
        Assert.False(ThinkingConsistency.HasEscalationConclusion(thinking));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Thinking_NoConflict(string? thinking) =>
        Assert.False(ThinkingConsistency.HasEscalationConclusion(thinking));
}
