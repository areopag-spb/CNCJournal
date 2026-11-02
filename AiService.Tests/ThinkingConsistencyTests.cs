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

    // Живые концовки рассуждений (октябрь 2026): модель оборвала мысль на условной
    // фразе и дала JSON «без эскалации». Это не вывод об эскалации.
    [Theory]
    [InlineData("Однако, если есть необъясненные аномалии (например, низкий КПД без причин), тогда требуется эскалация.")]
    [InlineData("Если это допустимо, то эскалация не требуется. В противном случае — нужно вызвать функцию для проверки.")]
    [InlineData("Однако отсутствие наладки при наличии плана и заказа требует проверки. Возможно, это требует эскалации для ручного разбора.")]
    [InlineData("Если после проверок окажется, что наладка не была выполнена и это необъяснимо, то потребуется эскалация.")]
    [InlineData("В итоге, если все причины объяснены, то requires_review будет false. В противном случае требуется эскалация.")]
    [InlineData("Если все проверки показывают, что отсутствие наладки не объясняется, то это требует эскалации.")]
    [InlineData("Вероятно, это требует эскалации, но данных мало.")]
    public void Hedged_Or_Conditional_Phrase_IsNotAConclusion(string tail) =>
        Assert.False(ThinkingConsistency.HasEscalationConclusion(Padded(tail)));

    // Безусловный вывод, в том числе после условного абзаца, по-прежнему ловится.
    [Theory]
    [InlineData("Если бы КПД был в норме, вопросов не было. Но причина не объясняет простой. Итог: это требует эскалации.")]
    [InlineData("Условий для закрытия нет.\nВывод: необходимо эскалировать сутки.")]
    public void Unconditional_Conclusion_AfterConditions_StillDetected(string tail) =>
        Assert.True(ThinkingConsistency.HasEscalationConclusion(Padded(tail)));
}
