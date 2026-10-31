using remeLog.Infrastructure;
using remeLog.Infrastructure.Types;
using System;
using System.Collections.Generic;

namespace remeLog.Models
{
    /// <summary>
    /// Решение аналитика по суткам (станок + дата).
    /// Это главная единица первичной проверки.
    ///
    /// Если день однозначен  → Decision = Ok,        IsFullyReviewed = true
    /// Если есть сомнения    → Decision = Escalated, IsFullyReviewed = false
    ///   В этом случае однозначные записи уже отмечены через PartFlags,
    ///   неоднозначные остаются без флагов для экспертов.
    /// </summary>
    public class DayReview
    {
        public DayReview() { }

        public DayReview(string machine, DateTime shiftDate, string reviewedBy,
                         AnalystDecision decision, bool isFullyReviewed, string? comment = null)
        {
            Machine = machine;
            ShiftDate = shiftDate.Date;
            ReviewedBy = reviewedBy;
            ReviewedAt = DateTime.Now;
            Decision = decision;
            IsFullyReviewed = isFullyReviewed;
            Comment = comment ?? string.Empty;
        }

        // Ключ

        public int Id { get; set; }
        public string Machine { get; set; } = string.Empty;
        public DateTime ShiftDate { get; set; }

        // Решение аналитика

        public string ReviewedBy { get; set; } = string.Empty;
        public DateTime ReviewedAt { get; set; }
        public AnalystDecision Decision { get; set; }

        /// <summary>
        /// Обновлять ли ReviewedBy/ReviewedAt при сохранении существующей записи.
        /// false — повторное сохранение без явного решения аналитика (например, кнопка
        /// "Записать" после правки строк) не должно выглядеть как новая проверка.
        /// На INSERT не влияет — новой записи ReviewedBy/ReviewedAt нужны всегда.
        /// </summary>
        public bool TouchReviewMeta { get; set; } = true;

        /// <summary>
        /// true  — аналитик полностью закрыл день (все записи однозначны).
        /// false — день передан экспертам, часть записей под вопросом.
        /// </summary>
        public bool IsFullyReviewed { get; set; }

        /// <summary>Общий комментарий по суткам.</summary>
        public string Comment { get; set; } = string.Empty;

        // Детализация по конкретным записям

        /// <summary>
        /// Флаги конкретных записей внутри этих суток.
        /// Загружается отдельно — не всегда нужен.
        /// </summary>
        public List<PartFlag> PartFlags { get; set; } = new();

        // AI-поля (заполняются на этапе 3)

        /// <summary>Считает ли AI, что день требует проверки.</summary>
        public bool? AiRequiresReview { get; set; }

        /// <summary>Уверенность AI [0..1].</summary>
        public double? AiConfidence { get; set; }

        /// <summary>Основные сигналы, найденные AI (JSON-массив строк).</summary>
        public string? AiSignals { get; set; }

        /// <summary>Краткое объяснение AI.</summary>
        public string? AiExplanation { get; set; }

        /// <summary>Был ли включён режим рассуждений.</summary>
        public bool? AiThinkingEnabled { get; set; }

        /// <summary>Версия модели.</summary>
        public string? AiModelVersion { get; set; }

        /// <summary>Версия промпта.</summary>
        public string? AiPromptVersion { get; set; }

        /// <summary>Время анализа AI.</summary>
        public DateTime? AiAnalyzedAt { get; set; }

        /// <summary>
        /// Коррекция аналитика на результат ИИ-анализа (free-text).
        /// Записывается аналитиком, прокидывается в промпт для будущих анализов
        /// при AiVerdict != «совпадение».
        /// </summary>
        public string? AiFeedback { get; set; }

        /// <summary>
        /// Вердикт ИИ, который аналитик принял вместе с решением дня. Пишется тем же
        /// запросом, что и решение: раньше ИИ-поля сохранялись отдельным вызовом и ТОЛЬКО
        /// когда ревью дня уже существовало, поэтому на первом разборе суток вердикт
        /// терялся (36 дней у Filippova_AV+ИИ, сентябрь 2026). null — ИИ-поля не трогаем,
        /// обычное сохранение решения их не затирает.
        /// </summary>
        public AiAnalysisResult? AiResult { get; set; }

        /// <summary>
        /// Автоматический вердикт совпадения ИИ и аналитика (computed в БД).
        /// Значения: «совпадение» / «AI пропустил» / «AI лишний флаг» /
        /// «не анализировалось». Read-only — never written by app.
        /// </summary>
        public string? AiVerdict { get; set; }

        // Автозакрытие без человека (этап 2.3)

        /// <summary> Значение ReviewedBy у суток, закрытых ИИ сам, без участия человека. </summary>
        public const string AutoReviewer = "ИИ-авто";

        /// <summary> Начало Comment у автозакрытых суток (по нему же строится история автозакрытия). </summary>
        public const string AutoCloseCommentMarker = "Автозакрытие:";

        /// <summary>
        /// Сутки закрыл ИИ без человека. Сохранение решения аналитиком (TouchReviewMeta)
        /// сбрасывает флаг — день снова «человеческий». false и у БД без столбца AutoClosed.
        /// </summary>
        public bool AutoClosed { get; set; }

        /// <summary> Причины, по которым сутки не закрыты автоматически (JSON-массив строк). </summary>
        public string? AutoCloseBlockers { get; set; }

        /// <summary> Закрыто ИИ: по флагу или по автору (строки до появления столбца). </summary>
        public bool IsAutoClosed => AutoClosed || ReviewedBy == AutoReviewer;

        // Вспомогательные свойства

        public bool HasAiResult => AiAnalyzedAt.HasValue;

        /// <summary>
        /// Совпало ли решение AI с решением аналитика.
        /// null если нет данных AI.
        /// </summary>
        public bool? AiMatchesAnalyst
        {
            get
            {
                if (!HasAiResult || !AiRequiresReview.HasValue) return null;
                return AiRequiresReview.Value == (Decision == AnalystDecision.Escalated);
            }
        }

        public override string ToString() =>
            $"[{Decision.ToDisplayString()}] {Machine} {ShiftDate:dd.MM.yyyy} by {ReviewedBy}";
    }
}