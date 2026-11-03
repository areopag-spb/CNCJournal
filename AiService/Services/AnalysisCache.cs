using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiService.Models;

namespace AiService.Services;

/// <summary>
/// Кэш вердиктов дневного анализа. Пакетное автозакрытие оставляет человеку 55–65% станко-суток и не
/// помечает их, поэтому при каждом следующем запуске они анализируются заново (~20 с на GPU каждые),
/// хотя данные не менялись. Ключ — хэш данных запроса после обработки (RequestShaper и подтверждённое
/// «Освоение»), версий промптов, модели и режима агента: изменилась хоть одна строка, отчёт мастера,
/// история детали или версия промпта — ключ другой, анализ идёт заново. Сутки с ошибкой, запасным путём
/// агента или рассинхроном рассуждения не кэшируются. Хранится в памяти (перезапуск службы очищает),
/// срок короткий, потому что tools агента читают БД (история детали, решения аналитиков).
/// Настройки: AnalysisCache:Enabled (true), TtlMinutes (360), MaxEntries (2000).
/// </summary>
public sealed class AnalysisCache
{
    private sealed record Entry(AnalyzeResponse Response, DateTime At);

    private static readonly JsonSerializerOptions KeyJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly bool _enabled;
    private long _hits;
    private long _misses;

    public AnalysisCache(IConfiguration configuration)
        : this(
            configuration.GetValue("AnalysisCache:Enabled", true),
            TimeSpan.FromMinutes(configuration.GetValue("AnalysisCache:TtlMinutes", 360)),
            configuration.GetValue("AnalysisCache:MaxEntries", 2000),
            () => DateTime.UtcNow)
    {
    }

    public AnalysisCache(bool enabled, TimeSpan ttl, int maxEntries, Func<DateTime> clock)
    {
        _enabled = enabled;
        _ttl = ttl;
        _maxEntries = Math.Max(10, maxEntries);
        _clock = clock;
    }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public int Count => _entries.Count;

    /// <summary>
    /// Ключ по данным запроса (после RequestShaper и MasteringAutoApprover.Apply), версии штатного и
    /// агентского промптов, модели и режимам. Версии промптов содержат хэш содержимого файла.
    /// </summary>
    public static string Key(AnalyzeRequest request, string promptVersion, string? agentPromptVersion, bool useAgent)
    {
        var data = JsonSerializer.Serialize(request, KeyJson);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{promptVersion}\n{(useAgent ? agentPromptVersion : "-")}\n{useAgent}\n{request.Model}\n{request.EnableThinking}\n{data}"));
        return Convert.ToHexString(bytes);
    }

    /// <summary> Можно ли сохранять ответ: нет ошибки, нет запасного пути агента, нет рассинхрона рассуждения. </summary>
    public static bool IsCacheable(AnalyzeResponse response) =>
        string.IsNullOrEmpty(response.Error)
        && !response.AutoCloseBlockers.Contains(AutoClosePolicy.AgentFellBack)
        && !response.Signals.Any(s => s.Contains("Нестабильный вывод", StringComparison.Ordinal));

    public AnalyzeResponse? TryGet(string key)
    {
        if (!_enabled) return null;
        if (_entries.TryGetValue(key, out var entry) && _clock() - entry.At <= _ttl)
        {
            Interlocked.Increment(ref _hits);
            return entry.Response;
        }
        Interlocked.Increment(ref _misses);
        return null;
    }

    public void Set(string key, AnalyzeResponse response)
    {
        if (!_enabled || !IsCacheable(response)) return;
        var now = _clock();
        _entries[key] = new Entry(response, now);
        if (_entries.Count <= _maxEntries) return;

        foreach (var kv in _entries.Where(kv => now - kv.Value.At > _ttl).ToList())
            _entries.TryRemove(kv);
        if (_entries.Count <= _maxEntries) return;
        foreach (var kv in _entries.OrderBy(kv => kv.Value.At).Take(_entries.Count - _maxEntries + _maxEntries / 10).ToList())
            _entries.TryRemove(kv);
    }
}
