namespace AiService.Services;

/// <summary>
/// Срок хранения логов (P3.2 плана): в logs/ и request_logs/ лежат ФИО и комментарии, а размер
/// рос без ограничений. Раз в сутки (и при старте) удаляются файлы старше Retention:Days по дате
/// последней записи; 0 или меньше — очистка выключена. Пустые месячные папки request_logs
/// удаляются. Сбой одной папки не роняет сервис.
/// </summary>
public sealed class LogRetentionService(IConfiguration configuration, ILogger<LogRetentionService> logger)
    : BackgroundService
{
    public const int DefaultDays = 180;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Небольшая задержка: не конкурировать с прогревом службы за диск.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            RunOnce(DateTime.Now);
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal void RunOnce(DateTime now)
    {
        var days = configuration.GetValue("Retention:Days", DefaultDays);
        if (days <= 0) return;
        var cutoff = now.AddDays(-days);

        try
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            var requestDir = configuration.GetValue<string>("RequestLog:Directory") ?? "request_logs";
            if (!Path.IsPathRooted(requestDir))
                requestDir = Path.Combine(AppContext.BaseDirectory, requestDir);

            var removedLogs = DeleteOlderThan(logsDir, "*.log", cutoff, recursive: false);
            var removedRequests = DeleteOlderThan(requestDir, "*.json", cutoff, recursive: true);
            RemoveEmptySubdirectories(requestDir);

            if (removedLogs + removedRequests > 0)
                logger.LogWarning(
                    "Очистка по сроку хранения ({Days} дн.): удалено логов {Logs}, request_logs {Requests}",
                    days, removedLogs, removedRequests);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Очистка логов по сроку хранения не выполнена");
        }
    }

    /// <summary> Удаляет файлы по маске с датой последней записи раньше границы. Возвращает число удалённых. </summary>
    public static int DeleteOlderThan(string directory, string pattern, DateTime cutoff, bool recursive)
    {
        if (!Directory.Exists(directory)) return 0;
        var removed = 0;
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(directory, pattern, option))
        {
            try
            {
                if (File.GetLastWriteTime(file) >= cutoff) continue;
                File.Delete(file);
                removed++;
            }
            catch
            {
                // Файл занят или недоступен — в следующий проход.
            }
        }
        return removed;
    }

    /// <summary> Удаляет пустые подпапки первого уровня (месяцы request_logs). </summary>
    public static void RemoveEmptySubdirectories(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
            }
            catch
            {
                // Папка занята — пропускаем.
            }
        }
    }
}
