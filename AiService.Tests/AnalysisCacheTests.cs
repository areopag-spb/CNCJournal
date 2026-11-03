using AiService.Models;
using AiService.Services;

namespace AiService.Tests;

/// <summary> Кэш вердиктов дневного анализа: повтор тех же данных мгновенно, любое изменение — новый анализ. </summary>
public class AnalysisCacheTests
{
    private DateTime _now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private AnalysisCache Cache(bool enabled = true, int ttlMinutes = 360, int max = 100) =>
        new(enabled, TimeSpan.FromMinutes(ttlMinutes), max, () => _now);

    private static AnalyzeRequest Request(string comment = "ок", double setupRatio = 0.9)
    {
        var r = new AnalyzeRequest { Machine = "Станок1", ShiftDate = "2026-10-28", UseAgent = true };
        r.Parts.Add(new PartContext { PartName = "Корпус", Setup = 1, Order = "З1", SetupRatio = setupRatio, OperatorComment = comment });
        return r;
    }

    private static AnalyzeResponse Verdict(bool review = false) => new() { RequiresReview = review, Confidence = 0.95, Explanation = "x" };

    [Fact]
    public void Key_SameData_SameKey_ChangedData_NewKey()
    {
        var a = AnalysisCache.Key(Request(), "v29", "agent-v29", true);

        Assert.Equal(a, AnalysisCache.Key(Request(), "v29", "agent-v29", true));
        Assert.NotEqual(a, AnalysisCache.Key(Request(comment: "поправили отчёт"), "v29", "agent-v29", true));
        Assert.NotEqual(a, AnalysisCache.Key(Request(setupRatio: 0.5), "v29", "agent-v29", true));
    }

    [Fact]
    public void Key_ChangesWithPromptVersionsModelAndMode()
    {
        var a = AnalysisCache.Key(Request(), "v29", "agent-v29", true);

        Assert.NotEqual(a, AnalysisCache.Key(Request(), "v30", "agent-v29", true));
        Assert.NotEqual(a, AnalysisCache.Key(Request(), "v29", "agent-v30", true));
        Assert.NotEqual(a, AnalysisCache.Key(Request(), "v29", null, false));

        var other = Request();
        other.Model = "gemma4:12b";
        Assert.NotEqual(a, AnalysisCache.Key(other, "v29", "agent-v29", true));
    }

    [Fact]
    public void Key_AgentVersionIgnoredWhenAgentNotUsed()
    {
        // Без агента версия агентского промпта не влияет на вердикт — ключ не должен зависеть от неё.
        Assert.Equal(
            AnalysisCache.Key(Request(), "v29", "agent-v29", false),
            AnalysisCache.Key(Request(), "v29", "agent-v30", false));
    }

    [Fact]
    public void SetThenGet_Hits_AndCountsHitsAndMisses()
    {
        var cache = Cache();
        var response = Verdict();

        Assert.Null(cache.TryGet("k"));
        cache.Set("k", response);

        Assert.Same(response, cache.TryGet("k"));
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void Entry_ExpiresAfterTtl()
    {
        var cache = Cache(ttlMinutes: 360);
        cache.Set("k", Verdict());
        _now += TimeSpan.FromMinutes(361);

        Assert.Null(cache.TryGet("k"));
    }

    [Fact]
    public void ErrorOrFallbackOrConflict_AreNotCached()
    {
        var cache = Cache();
        cache.Set("err", new AnalyzeResponse { RequiresReview = true, Error = "Ollama упала" });
        cache.Set("fb", new AnalyzeResponse { AutoCloseBlockers = [AutoClosePolicy.AgentFellBack] });
        cache.Set("conflict", new AnalyzeResponse { Signals = ["[СИСТЕМА] Нестабильный вывод модели: в рассуждении предложена эскалация"] });
        cache.Set("good", Verdict());

        Assert.Null(cache.TryGet("err"));
        Assert.Null(cache.TryGet("fb"));
        Assert.Null(cache.TryGet("conflict"));
        Assert.NotNull(cache.TryGet("good"));
    }

    [Fact]
    public void Disabled_NeverCaches()
    {
        var cache = Cache(enabled: false);
        cache.Set("k", Verdict());

        Assert.Null(cache.TryGet("k"));
    }

    [Fact]
    public void Size_IsBounded()
    {
        var cache = Cache(max: 20);
        for (var i = 0; i < 100; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            cache.Set($"k{i}", Verdict());
        }

        Assert.InRange(cache.Count, 1, 20);
        Assert.NotNull(cache.TryGet("k99"));
    }
}
