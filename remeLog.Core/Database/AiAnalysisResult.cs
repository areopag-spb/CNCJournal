using System;
using System.Text.Json.Serialization;

namespace remeLog.Infrastructure
{
    public class AiAnalysisResult
    {
        [JsonPropertyName("requiresReview")] public bool RequiresReview { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("signals")] public string[] Signals { get; set; } = Array.Empty<string>();
        [JsonPropertyName("explanation")] public string Explanation { get; set; } = "";
        [JsonPropertyName("suggestedReason")] public string SuggestedReason { get; set; } = "";
        [JsonPropertyName("suggestExcludeFromReports")]
        public string[] SuggestExcludeFromReports { get; set; } = Array.Empty<string>();
        /// <summary> Предложения об исключении, которые ИИ вправе применить сам (null — служба без классификации). </summary>
        [JsonPropertyName("autoExcludeEntries")]
        public string[]? AutoExcludeEntries { get; set; }
        /// <summary> Предложения по строкам с отмеченными простоями: игнорируются, сутки не блокируют. </summary>
        [JsonPropertyName("ignoredExcludeEntries")]
        public string[]? IgnoredExcludeEntries { get; set; }
        [JsonPropertyName("flaggedParts")]
        public string[] FlaggedParts { get; set; } = Array.Empty<string>();
        [JsonPropertyName("shiftReportIssues")]
        public string[] ShiftReportIssues { get; set; } = Array.Empty<string>();
        [JsonPropertyName("shiftReportSummary")]
        public string[] ShiftReportSummary { get; set; } = Array.Empty<string>();
        [JsonPropertyName("escalatedByData")] public bool EscalatedByData { get; set; }
        [JsonPropertyName("escalatedByShiftReport")] public bool EscalatedByShiftReport { get; set; }
        /// <summary> Сервер разрешает закрыть сутки без человека (см. AutoClosePolicy). </summary>
        [JsonPropertyName("autoCloseEligible")] public bool AutoCloseEligible { get; set; }
        /// <summary> Почему сутки без эскалации всё же не закрываются автоматически. </summary>
        [JsonPropertyName("autoCloseBlockers")]
        public string[] AutoCloseBlockers { get; set; } = Array.Empty<string>();
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("promptVersion")] public string? PromptVersion { get; set; }
        public bool HasError => !string.IsNullOrEmpty(Error);
    }
}
