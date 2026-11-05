using System;
using System.Collections.Generic;

namespace remeLog.Models
{
    /// <summary>
    /// Решение автозакрытия по суткам станка (таблица ai_auto_close_log): и реальное, и теневое.
    /// Основа сводки автозакрытия и выборочного контроля закрытых ИИ суток.
    /// </summary>
    public class AutoCloseLogEntry
    {
        public const string OutcomeClosed = "Closed";
        public const string OutcomeWouldClose = "WouldClose";
        public const string OutcomeDeferred = "Deferred";
        public const string OutcomeManual = "Manual";
        public const string OutcomeError = "Error";
        /// <summary> Перепроверка закрытых ИИ суток: отметка снята, сутки возвращены человеку. </summary>
        public const string OutcomeReopened = "Reopened";

        public const string ControlAgree = "agree";
        public const string ControlMiss = "miss";
        /// <summary> Пропуск hard-класса: критерий остановки автозакрытия (план, P1.4). </summary>
        public const string ControlMissHard = "miss_hard";

        public int Id { get; set; }
        public string Machine { get; set; } = string.Empty;
        public DateTime ShiftDate { get; set; }
        public DateTime RunAt { get; set; } = DateTime.Now;
        public string RunBy { get; set; } = string.Empty;
        public bool DryRun { get; set; }
        public string Outcome { get; set; } = string.Empty;
        public IReadOnlyList<string> Reasons { get; set; } = Array.Empty<string>();
        public bool? AiRequiresReview { get; set; }
        public double? AiConfidence { get; set; }
        public string? AiPromptVersion { get; set; }
        public int ExclusionsApplied { get; set; }

        public string? ControlVerdict { get; set; }
        public string? ControlBy { get; set; }
        public DateTime? ControlAt { get; set; }
        public string? ControlComment { get; set; }

        public bool IsControlled => !string.IsNullOrEmpty(ControlVerdict);
        public bool IsMiss => ControlVerdict is ControlMiss or ControlMissHard;
    }
}
