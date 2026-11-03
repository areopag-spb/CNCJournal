using System.Text.Json.Serialization;

namespace AiService.Models;

public class AnalyzeRequest
{
    public string Machine { get; set; } = "";
    public string ShiftDate { get; set; } = "";
    public List<string> Signals { get; set; } = [];
    public List<PartContext> Parts { get; set; } = [];

    /// <summary>
    /// Суточные отчёты мастера (день + ночь, cnc_shifts) для проверки их содержимого:
    /// свежий пересчёт простоя делает клиент, сервер применяет ShiftReportRuleEvaluator.
    /// Пусто (старый клиент) — проверка отчёта пропускается.
    /// </summary>
    public List<ShiftReportContext> ShiftReports { get; set; } = [];

    /// <summary>
    /// Закрытый список причин простоя (cnc_downtime_reasons) с клиента —
    /// сервер сверяет с ним DowntimeReason (правило R6), свой справочник не держит.
    /// </summary>
    public List<string> DowntimeReasons { get; set; } = [];
    public bool EnableThinking { get; set; } = false;
    public string? Model { get; set; }

    /// <summary>
    /// Клиент просит агентский контур (чекбокс «Агент» в remeLog).
    /// Действует только вместе с серверным Agent:Enabled (см. AnalysisController);
    /// старые клиенты поле не шлют → false → штатный путь. Обратная совместимость
    /// в обе стороны: неизвестное поле игнорируется.
    /// </summary>
    public bool UseAgent { get; set; } = false;

    /// <summary>
    /// Станок серийный (cnc_machines.IsSerial) — null/true = серийный (проверяем всё).
    /// false = несерийный: КПД изготовления не оценивается НИ В ОДНОМ контуре
    /// (HardRule, фильтры, оба промпта). Остальное (наладка, нормативы, отчёты) — в силе.
    /// Старые клиенты поле не шлют → null → старое поведение.
    /// </summary>
    public bool? IsSerialMachine { get; set; }

    /// <summary>
    /// Профиль промпта: выбирает prompts/system_prompt.{profile}.txt.
    /// Определяется клиентом remeLog из cnc_machines.AiPromptProfile.
    /// Пусто/null или отсутствующий файл — используется system_prompt.txt.
    /// </summary>
    public string? PromptProfile { get; set; }
}

public class PartContext
{
    public string PartName { get; set; } = "";
    public string Order { get; set; } = "";
    public int Setup { get; set; }

    public double? SetupRatio { get; set; }
    public double? ProductionRatio { get; set; }
    public double FinishedCount { get; set; }
    public double SetupTimePlan { get; set; }
    public double SetupTimeFact { get; set; }
    public double SingleProductionTimePlan { get; set; }
    public double ProductionTimeFact { get; set; } 
    public double PartialSetup { get; set; } 
    public double MachiningTime { get; set; }
    public double? DowntimeRatio { get; set; }
    public string OperatorComment { get; set; } = "";
    /// <summary>
    /// ЭФФЕКТИВНАЯ причина отклонения наладки: переопределение аналитика (СГТ 1), если оно
    /// есть, иначе отметка мастера. Все правила и промпты работают с этим значением —
    /// исходная отметка мастера лежит в <see cref="SetupReasonOverride"/> рядом.
    /// </summary>
    public string MasterSetupComment { get; set; } = "";
    /// <summary> ЭФФЕКТИВНАЯ причина отклонения изготовления. См. <see cref="MasterSetupComment"/>. </summary>
    public string MasterMachiningComment { get; set; } = "";
    public string MasterComment { get; set; } = "";
    public string MasterSetupDetail { get; set; } = "";
    public string MasterMachiningDetail { get; set; } = "";
    public string SpecifiedDowntimesList { get; set; } = "";
    public string SpecifiedDowntimesComment { get; set; } = "";

    // Переопределение причин аналитиком (СГТ 1). Непустое означает, что причина выше —
    // решение СГТ, а не мастера. Контекст для объяснений, в правилах не участвует.
    public string SetupReasonOverride { get; set; } = "";
    public string SetupReasonOverrideComment { get; set; } = "";
    public string MachiningReasonOverride { get; set; } = "";
    public string MachiningReasonOverrideComment { get; set; } = "";
    public string ReasonOverrideBy { get; set; } = "";

    public bool NoSetupHappened { get; set; }
    public bool NoProductionHappened { get; set; }
    public bool NoManualOperatorComment { get; set; }

    // Детерминированные сигналы от remeLog
    public List<string> Signals { get; set; } = [];
    public PartsHistoryDto? PartsHistory { get; set; }

    /// <summary>
    /// Штучная партия по регламенту «Требования к заполнению и контролю»:
    /// м/в &lt; 3 мин и изготовлено ≤ 10 деталей, либо м/в ≥ 3 мин и ≤ 5 деталей.
    /// Штучные партии не участвуют в отчётах, данные на малой партии не показательны.
    /// </summary>
    public bool IsSmallBatch =>
        (MachiningTime < 3 && FinishedCount <= 10)
        || (MachiningTime >= 3 && FinishedCount <= 5);
}

public class PartsHistoryDto
{
    public int RecordsFound { get; set; }
    public List<PartsHistoryLineDto> Lines { get; set; } = new();
}

/// <summary>
/// Снимок суточного отчёта мастера по одной смене (cnc_shifts) + свежие значения,
/// пересчитанные клиентом по строкам parts на момент анализа. Серверные правила
/// работают по свежим значениям; расхождение со снимком — отдельное нарушение (R4).
/// </summary>
public class ShiftReportContext
{
    /// <summary> Название смены («День»/«Ночь»). </summary>
    public string Shift { get; set; } = "";
    /// <summary> Длительность смены в минутах (660/630). </summary>
    public int ShiftMinutes { get; set; }
    /// <summary> Строка отчёта есть в cnc_shifts. Нет — проверять нечего (hard). </summary>
    public bool ReportExists { get; set; }
    public string Master { get; set; } = "";
    /// <summary> Снимок неотмеченного простоя из БД на момент сохранения отчёта. </summary>
    public double StoredUnspecifiedDowntimes { get; set; }
    /// <summary> Свежий пересчёт неотмеченного простоя по текущим parts. </summary>
    public double FreshUnspecifiedDowntimes { get; set; }
    public string DowntimeReason { get; set; } = "";
    public string MasterComment { get; set; } = "";
    /// <summary> Есть ли строки parts за эту смену. </summary>
    public bool HasParts { get; set; }
    /// <summary> Доля частичной наладки (0..1+) — для рекомендательного S2. </summary>
    public double PartialSetupRatio { get; set; }
}

public class PartsHistoryLineDto
{
    public string ShiftDate { get; set; } = "";
    public string ProductionRatio { get; set; } = "";
    public string SetupRatio { get; set; } = "";
    public int FinishedCount { get; set; }
    public string AnalystDecision { get; set; } = "";
    public string? AnalystComment { get; set; }
    public string? AiExplanation { get; set; }
    public string? AiFeedback { get; set; }
    public bool HasUnexplainedLowEfficiency { get; set; }
}

public class AnalyzeResponse
{
    public bool RequiresReview { get; set; }
    public double Confidence { get; set; }
    public List<string> Signals { get; set; } = [];
    public List<string> DowngradedSignals { get; set; } = [];
    public List<string> SuggestExcludeFromReports { get; set; } = [];

    /// <summary>
    /// Подмножество SuggestExcludeFromReports, которое ИИ вправе применить сам при автозакрытии
    /// (<see cref="Services.AutoExcludePolicy"/>). Клиент: запись вне этого списка и вне
    /// IgnoredExcludeEntries уводит сутки человеку.
    /// </summary>
    public List<string> AutoExcludeEntries { get; set; } = [];

    /// <summary> Предложения по строкам с отмеченными простоями — игнорируются, сутки не блокируют. </summary>
    public List<string> IgnoredExcludeEntries { get; set; } = [];

    /// <summary>
    /// Ключи PartName§Setup§Order деталей с неустранёнными сигналами (hard +
    /// непониженные soft): ИИ предлагает отметить их проблемными строками (флаги СГТ)
    /// при согласии с вердиктом. Пусто — конкретные строки не указаны.
    /// </summary>
    [JsonPropertyName("flaggedParts")]
    public List<string> FlaggedParts { get; set; } = [];

    public string Explanation { get; set; } = "";
    public string ThinkingProcess { get; set; } = "";
    public string SuggestedReason { get; set; } = "";

    /// <summary>
    /// Вопросы к суточному отчёту мастера (детерминированные R1–R6 + рекомендательный
    /// S2 + семантическая релевантность S1 от модели). Дублируются в Signals,
    /// отдельный массив — для обособленного блока в диалоге вердикта.
    /// </summary>
    public List<string> ShiftReportIssues { get; set; } = [];

    /// <summary>
    /// Построчная сводка проверки отчёта мастера (по смене: факты + детерминированный
    /// вердикт). Заполняется всегда, когда клиент прислал ShiftReports, — UI показывает
    /// блок «Отчёт мастера» даже при отсутствии вопросов.
    /// </summary>
    public List<string> ShiftReportSummary { get; set; } = [];

    /// <summary>
    /// Эскалация вызвана данными записей (hard/soft/вердикт модели). UI подсвечивает
    /// блок признаков жёлтым. S2-soft по отчёту сюда не входит.
    /// </summary>
    public bool EscalatedByData { get; set; }

    /// <summary>
    /// Сутки можно закрыть без человека: вердикт «без эскалации» и нет признаков
    /// ненадёжности (см. <c>AutoClosePolicy</c>). Решение о фактическом закрытии принимает клиент.
    /// </summary>
    public bool AutoCloseEligible { get; set; }

    /// <summary>
    /// Версия анализа (сборка службы, промпты, настройки агента; см. AnalysisVersion). Клиент хранит её
    /// рядом с вердиктом и повторно использует вердикт, только если она совпала с текущей из /health.
    /// </summary>
    public string? AnalysisVersion { get; set; }

    /// <summary> Почему сутки без эскалации всё же не закрываются автоматически. </summary>
    public List<string> AutoCloseBlockers { get; set; } = [];

    /// <summary>
    /// Эскалация вызвана суточным отчётом (детерминированные hard или S1 модели).
    /// UI подсвечивает блок «Отчёт мастера» жёлтым.
    /// </summary>
    public bool EscalatedByShiftReport { get; set; }
    public string? Error { get; set; }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string? PromptVersion { get; set; }

}