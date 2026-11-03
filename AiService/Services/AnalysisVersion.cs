using System.Security.Cryptography;
using System.Text;

namespace AiService.Services;

/// <summary>
/// «Версия анализа»: короткий отпечаток всего, от чего зависит вердикт дневного анализа: сборка службы
/// (правила, фильтры, политика закрытия), файлы промптов и настройки агента. Клиент хранит её рядом с
/// сохранённым вердиктом и повторно использует вердикт, только если версия и данные суток не изменились
/// (так вердикт переживает перезапуск службы, но не деплой с изменившимися правилами или промптом).
/// Отдаётся в /api/analysis/health и в каждом ответе анализа.
/// </summary>
public static class AnalysisVersion
{
    private static readonly string BuildId =
        typeof(AnalysisVersion).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    public static string Compute(string promptsFingerprint, IConfiguration configuration) =>
        Compute(BuildId, promptsFingerprint, configuration);

    internal static string Compute(string buildId, string promptsFingerprint, IConfiguration configuration)
    {
        var settings = string.Join('|',
            configuration.GetValue("Agent:Enabled", false),
            configuration.GetValue("Agent:Temperature", 0.0),
            configuration.GetValue("Agent:Thinking", "auto"),
            configuration.GetValue("Agent:MaxIterations", 4),
            configuration.GetValue("Agent:VerifyFalse", false),
            configuration.GetValue<string>("Ollama:Model"));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{buildId}\n{promptsFingerprint}\n{settings}"));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
