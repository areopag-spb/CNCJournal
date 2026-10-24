using AiService.Services;

namespace AiService.Tests;

public class PromptVersionTests
{
    [Fact]
    public void WithContentHash_ChangesWhenContentChanges()
    {
        var a = PromptBuilder.WithContentHash("2026-10-24-v1", "правила");
        var b = PromptBuilder.WithContentHash("2026-10-24-v1", "правила!");

        Assert.NotEqual(a, b);
        Assert.StartsWith("2026-10-24-v1+", a);
    }

    [Fact]
    public void ShortHash_IgnoresLineEndings()
    {
        Assert.Equal(
            PromptBuilder.ShortHash("a\r\nb\r\n", 8),
            PromptBuilder.ShortHash("a\nb\n", 8));
    }

    [Fact]
    public void WithExtraBlock_KeepsProfileSuffixLast()
    {
        var v = PromptBuilder.WithExtraBlock("2026-09-25-agent-v28+abcd1234@agent", "soft");

        Assert.EndsWith("@agent", v);
        Assert.Contains("+s", v);
    }

    [Fact]
    public void WithExtraBlock_FitsDbColumn()
    {
        var v = PromptBuilder.WithExtraBlock(
            PromptBuilder.WithContentHash("2026-09-25-agent-v28", "x") + "@agent", "soft");

        Assert.True(v.Length <= 50, v);
    }
}
