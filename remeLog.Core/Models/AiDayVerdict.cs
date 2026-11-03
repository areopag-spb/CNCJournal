using remeLog.Infrastructure;
using System;

namespace remeLog.Models
{
    /// <summary>
    /// Последний вердикт ИИ по станко-суткам (таблица ai_day_verdicts). В отличие от ai_day_reviews это не
    /// решение по суткам, а только результат анализа: хранится и для суток, которые ИИ оставил человеку,
    /// и переживает перезапуск службы и срок хранения request_logs. Одна строка на станок и дату.
    /// </summary>
    public class AiDayVerdict
    {
        public const string SourceBatch = "batch";
        public const string SourceManual = "manual";

        public int Id { get; set; }
        public string Machine { get; set; } = string.Empty;
        public DateTime ShiftDate { get; set; }
        public DateTime AnalyzedAt { get; set; } = DateTime.Now;
        public string AnalyzedBy { get; set; } = string.Empty;
        public string Source { get; set; } = SourceBatch;

        /// <summary> Отпечаток данных, отправленных на анализ (запрос целиком). </summary>
        public string InputFingerprint { get; set; } = string.Empty;

        /// <summary> Версия анализа службы на момент вердикта (AnalysisVersion из ответа). </summary>
        public string? AnalysisVersion { get; set; }

        public string? AiModelVersion { get; set; }
        public bool ThinkingEnabled { get; set; }

        public AiAnalysisResult Result { get; set; } = new();
    }

    /// <summary> Когда сохранённым вердиктом можно воспользоваться вместо повторного анализа. </summary>
    public static class AiVerdictReuse
    {
        /// <summary>
        /// Вердикт годен, если он есть, без ошибки и совпали все четыре признака: данные суток (отпечаток),
        /// версия анализа службы (сборка, промпты, настройки), модель и режим (агент/thinking).
        /// Пустая версия службы (старая служба, нет связи) — не годен: сверить нечем.
        /// </summary>
        public static bool CanReuse(
            AiDayVerdict? stored, string currentFingerprint, string? currentAnalysisVersion,
            string? model, bool thinkingEnabled)
        {
            if (stored is null || stored.Result.HasError) return false;
            if (string.IsNullOrEmpty(currentFingerprint) || string.IsNullOrEmpty(currentAnalysisVersion)) return false;
            if (string.IsNullOrEmpty(stored.AnalysisVersion)) return false;
            return stored.InputFingerprint == currentFingerprint
                && stored.AnalysisVersion == currentAnalysisVersion
                && string.Equals(stored.AiModelVersion, model, StringComparison.Ordinal)
                && stored.ThinkingEnabled == thinkingEnabled;
        }
    }
}
