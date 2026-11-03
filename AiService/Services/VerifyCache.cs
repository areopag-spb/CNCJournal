using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AiService.Models;

namespace AiService.Services;

/// <summary>
/// Кэш подсказок мастеру (verify-part, P2.2 плана). Клиент шлёт запрос на каждое изменение поля, и в
/// октябре 74% обращений к модели (905 из 1224) были повторами идентичного входа. Ключ — хэш итогового
/// промпта вместе с моделью и версией: идентичный промпт при температуре 0.05 даёт тот же ответ, поэтому
/// кэш не меняет поведение, а только убирает повторный расход GPU. Одновременные одинаковые запросы
/// объединяются в один вызов. Сбои и ответы с ошибкой не кэшируются.
/// Настройки: VerifyCache:Enabled (true), TtlMinutes (720), MaxEntries (5000).
/// </summary>
public sealed class VerifyCache
{
    private sealed record Entry(Lazy<Task<VerifyPartResponse>> Task, DateTime At);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly bool _enabled;
    private long _hits;
    private long _misses;

    public VerifyCache(IConfiguration configuration)
        : this(
            configuration.GetValue("VerifyCache:Enabled", true),
            TimeSpan.FromMinutes(configuration.GetValue("VerifyCache:TtlMinutes", 720)),
            configuration.GetValue("VerifyCache:MaxEntries", 5000),
            () => DateTime.UtcNow)
    {
    }

    public VerifyCache(bool enabled, TimeSpan ttl, int maxEntries, Func<DateTime> clock)
    {
        _enabled = enabled;
        _ttl = ttl;
        _maxEntries = Math.Max(10, maxEntries);
        _clock = clock;
    }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public int Count => _entries.Count;

    /// <summary> Хэш итогового промпта, модели и версии промпта. </summary>
    public static string Key(string? model, string version, string prompt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{model}\n{version}\n{prompt}"));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Возвращает ответ из кэша или вызывает factory. cached = true, если ответ взят из кэша (или у
    /// уже выполняющегося такого же запроса). Отмена ct прерывает только ожидание вызывающего:
    /// сам вызов модели продолжается и результат достаётся следующему такому же запросу.
    /// </summary>
    public async Task<(VerifyPartResponse Response, bool Cached)> GetOrCreateAsync(
        string key, Func<Task<VerifyPartResponse>> factory, CancellationToken ct)
    {
        if (!_enabled)
            return (await factory().WaitAsync(ct), false);

        var now = _clock();
        if (_entries.TryGetValue(key, out var existing) && now - existing.At <= _ttl)
        {
            Interlocked.Increment(ref _hits);
            return (await Observe(key, existing, ct), true);
        }

        var entry = new Entry(new Lazy<Task<VerifyPartResponse>>(factory), now);
        var stored = _entries.AddOrUpdate(key, entry, (_, old) => now - old.At <= _ttl ? old : entry);
        var isOwner = ReferenceEquals(stored, entry);
        if (isOwner)
        {
            Interlocked.Increment(ref _misses);
            Trim(now);
        }
        else
        {
            Interlocked.Increment(ref _hits);
        }
        return (await Observe(key, stored, ct), !isOwner);
    }

    private async Task<VerifyPartResponse> Observe(string key, Entry entry, CancellationToken ct)
    {
        try
        {
            var response = await entry.Task.Value.WaitAsync(ct);
            if (!string.IsNullOrEmpty(response.Error)) Forget(key, entry);
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Forget(key, entry);
            throw;
        }
    }

    private void Forget(string key, Entry entry) =>
        _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));

    /// <summary> Держит размер в пределах MaxEntries: сначала просроченные, затем самые старые. </summary>
    private void Trim(DateTime now)
    {
        if (_entries.Count <= _maxEntries) return;
        foreach (var kv in _entries.Where(kv => now - kv.Value.At > _ttl).ToList())
            _entries.TryRemove(kv);
        if (_entries.Count <= _maxEntries) return;
        foreach (var kv in _entries.OrderBy(kv => kv.Value.At).Take(_entries.Count - _maxEntries + _maxEntries / 10).ToList())
            _entries.TryRemove(kv);
    }
}
