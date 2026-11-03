using AiService.Models;
using AiService.Services;

namespace AiService.Tests;

/// <summary> P2.2: кэш подсказок мастеру не должен менять ответы и не должен кэшировать сбои. </summary>
public class VerifyCacheTests
{
    private DateTime _now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private VerifyCache Cache(bool enabled = true, int ttlMinutes = 60, int max = 100) =>
        new(enabled, TimeSpan.FromMinutes(ttlMinutes), max, () => _now);

    private static Task<VerifyPartResponse> Ok(string remark = "") =>
        Task.FromResult(new VerifyPartResponse { Ok = remark.Length == 0, Remark = remark });

    [Fact]
    public async Task Key_DependsOnModelVersionAndPrompt()
    {
        var a = VerifyCache.Key("m1", "v1", "prompt");

        Assert.Equal(a, VerifyCache.Key("m1", "v1", "prompt"));
        Assert.NotEqual(a, VerifyCache.Key("m2", "v1", "prompt"));
        Assert.NotEqual(a, VerifyCache.Key("m1", "v2", "prompt"));
        Assert.NotEqual(a, VerifyCache.Key("m1", "v1", "prompt2"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SecondIdenticalRequest_IsServedFromCache_WithoutCallingFactory()
    {
        var cache = Cache();
        var calls = 0;
        Task<VerifyPartResponse> Factory() { calls++; return Ok("замечание"); }

        var (r1, c1) = await cache.GetOrCreateAsync("k", Factory, CancellationToken.None);
        var (r2, c2) = await cache.GetOrCreateAsync("k", Factory, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.False(c1);
        Assert.True(c2);
        Assert.Equal("замечание", r2.Remark);
        Assert.Same(r1, r2);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public async Task DifferentKeys_AreIndependent()
    {
        var cache = Cache();
        var calls = 0;
        Task<VerifyPartResponse> Factory() { calls++; return Ok(); }

        await cache.GetOrCreateAsync("a", Factory, CancellationToken.None);
        await cache.GetOrCreateAsync("b", Factory, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequests_ShareOneCall()
    {
        var cache = Cache();
        var calls = 0;
        var gate = new TaskCompletionSource<VerifyPartResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<VerifyPartResponse> Factory() { Interlocked.Increment(ref calls); return gate.Task; }

        var first = cache.GetOrCreateAsync("k", Factory, CancellationToken.None);
        var second = cache.GetOrCreateAsync("k", Factory, CancellationToken.None);
        var third = cache.GetOrCreateAsync("k", Factory, CancellationToken.None);
        gate.SetResult(new VerifyPartResponse { Ok = false, Remark = "r" });
        var results = await Task.WhenAll(first, second, third);

        Assert.Equal(1, calls);
        Assert.Equal(2, results.Count(r => r.Cached));
        Assert.All(results, r => Assert.Equal("r", r.Response.Remark));
    }

    [Fact]
    public async Task ResponseWithError_IsNotCached()
    {
        var cache = Cache();
        var calls = 0;
        Task<VerifyPartResponse> Factory()
        {
            calls++;
            return Task.FromResult(new VerifyPartResponse { Ok = true, Error = "Ответ модели без поля ok" });
        }

        await cache.GetOrCreateAsync("k", Factory, CancellationToken.None);
        await cache.GetOrCreateAsync("k", Factory, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedCall_IsNotCached_AndPropagates()
    {
        var cache = Cache();
        var calls = 0;
        Task<VerifyPartResponse> Failing()
        {
            calls++;
            return Task.FromException<VerifyPartResponse>(new InvalidOperationException("Ollama недоступна"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync("k", Failing, CancellationToken.None));
        var (r, cached) = await cache.GetOrCreateAsync("k", () => Ok("после сбоя"), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.False(cached);
        Assert.Equal("после сбоя", r.Remark);
    }

    [Fact]
    public async Task Entry_ExpiresAfterTtl()
    {
        var cache = Cache(ttlMinutes: 60);
        var calls = 0;
        Task<VerifyPartResponse> Factory() { calls++; return Ok(); }

        await cache.GetOrCreateAsync("k", Factory, CancellationToken.None);
        _now += TimeSpan.FromMinutes(61);
        var (_, cached) = await cache.GetOrCreateAsync("k", Factory, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.False(cached);
    }

    [Fact]
    public async Task Disabled_AlwaysCallsFactory()
    {
        var cache = Cache(enabled: false);
        var calls = 0;
        Task<VerifyPartResponse> Factory() { calls++; return Ok(); }

        await cache.GetOrCreateAsync("k", Factory, CancellationToken.None);
        await cache.GetOrCreateAsync("k", Factory, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Size_IsBounded()
    {
        var cache = Cache(max: 20);
        for (var i = 0; i < 100; i++)
        {
            _now += TimeSpan.FromSeconds(1);
            await cache.GetOrCreateAsync($"k{i}", () => Ok(), CancellationToken.None);
        }

        Assert.InRange(cache.Count, 1, 20);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotPoisonSharedCall()
    {
        var cache = Cache();
        var gate = new TaskCompletionSource<VerifyPartResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var impatient = cache.GetOrCreateAsync("k", () => gate.Task, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);

        gate.SetResult(new VerifyPartResponse { Ok = true });
        var (r, cached) = await cache.GetOrCreateAsync("k", () => Ok("не должен вызываться"), CancellationToken.None);

        Assert.True(cached);
        Assert.True(r.Ok);
    }
}
