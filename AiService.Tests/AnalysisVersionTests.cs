using AiService.Services;
using Microsoft.Extensions.Configuration;

namespace AiService.Tests;

public class AnalysisVersionTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void SameInputs_SameVersion_AndShortHex()
    {
        var a = AnalysisVersion.Compute("build1", "prompts1", Config(("Agent:Enabled", "true")));
        var b = AnalysisVersion.Compute("build1", "prompts1", Config(("Agent:Enabled", "true")));

        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{12}$", a);
    }

    [Fact]
    public void ChangesWithBuildPromptsAndAgentSettings()
    {
        var config = Config(("Agent:Enabled", "true"), ("Agent:Temperature", "0"));
        var baseline = AnalysisVersion.Compute("build1", "prompts1", config);

        Assert.NotEqual(baseline, AnalysisVersion.Compute("build2", "prompts1", config));
        Assert.NotEqual(baseline, AnalysisVersion.Compute("build1", "prompts2", config));
        Assert.NotEqual(baseline, AnalysisVersion.Compute("build1", "prompts1", Config(("Agent:Enabled", "true"), ("Agent:Temperature", "0.7"))));
        Assert.NotEqual(baseline, AnalysisVersion.Compute("build1", "prompts1", Config(("Agent:Enabled", "false"), ("Agent:Temperature", "0"))));
        Assert.NotEqual(baseline, AnalysisVersion.Compute("build1", "prompts1", Config(("Agent:Enabled", "true"), ("Agent:Temperature", "0"), ("Ollama:Model", "gemma4:12b"))));
    }

    [Fact]
    public void CurrentBuild_IsStableWithinProcess()
    {
        var config = Config();

        Assert.Equal(AnalysisVersion.Compute("p", config), AnalysisVersion.Compute("p", config));
    }
}
