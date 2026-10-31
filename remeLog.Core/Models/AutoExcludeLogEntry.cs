using System;

namespace remeLog.Models
{
    /// <summary>
    /// Запись журнала исключений из К1, которые ИИ применил сам при автозакрытии суток
    /// (таблица ai_auto_exclude_log). Нужна, чтобы любое авто-исключение можно было
    /// увидеть и отменить. PartGuid без FK: журнал переживает удаление деталей.
    /// </summary>
    public class AutoExcludeLogEntry
    {
        public int Id { get; set; }
        public string Machine { get; set; } = string.Empty;
        public DateTime ShiftDate { get; set; }
        public Guid PartGuid { get; set; }
        public string PartName { get; set; } = string.Empty;
        public int Setup { get; set; }
        public string Order { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string? AiPromptVersion { get; set; }
        public string ExcludedBy { get; set; } = DayReview.AutoReviewer;
        public DateTime ExcludedAt { get; set; } = DateTime.Now;
        public string? RevertedBy { get; set; }
        public DateTime? RevertedAt { get; set; }

        public bool IsReverted => RevertedAt.HasValue;
    }
}
