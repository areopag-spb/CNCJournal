namespace AiService.Models;

// ═══ Агентный контур дневного анализа (стадия 1: каркас) ═══
// Идея: вместо «толстого» system_prompt.txt (405 строк) — тощий скелет +
// tools, которыми модель сама таскает фактологию из данных запроса (позже — из БД).
// Всё детерминированное (hard-правила, пороги, фильтры) остаётся в коде и выполняется
// до/после модели как раньше — модель отвечает только за связку «цифры + слова».

// ── Chat-поверх /api/chat Ollama ─────────────────────────────────────────────

/// <summary> Одно сообщение диалога с моделью. </summary>
public class ChatMessage
{
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    public List<AgentToolCall>? ToolCalls { get; set; }
}

/// <summary> Запрос модели на вызов инструмента. </summary>
public class AgentToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary> Описание инструмента для поля tools запроса /api/chat. </summary>
public class ChatTool
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary> JSON-схема параметров (объект вида {"type":"object","properties":{...}}). </summary>
    public string ParametersJson { get; set; } = """{"type":"object","properties":{}}""";
}

/// <summary> Ответ модели на один раунд: текст и/или запросы tools. </summary>
public class ChatResult
{
    public string Content { get; set; } = "";
    public List<AgentToolCall> ToolCalls { get; set; } = [];
    /// <summary> Рассуждение модели (только при think:true, может быть пустым). </summary>
    public string? Thinking { get; set; }
    /// <summary> prompt_eval_count Ollama: сколько токенов промпта обработано (0 — не сообщено). </summary>
    public int PromptTokens { get; set; }
    /// <summary> eval_count Ollama: сколько токенов сгенерировано (0 — не сообщено). </summary>
    public int CompletionTokens { get; set; }
    /// <summary> done_reason Ollama: stop штатно, length — оборвано по лимиту токенов. </summary>
    public string? DoneReason { get; set; }
}

/// <summary>
/// Абстракция chat-клиента для агентского цикла. OllamaService её реализует;
/// в тестах подменяется заглушкой. Очередью к GPU владеет реализация:
/// каждый раунд цикла — отдельное занятие очереди (см. AgentLoopService).
/// thinkingProgress — живые дельты рассуждений (только при think:true),
/// для панели хода в UI; null — не слать.
/// </summary>
public interface IAgentChatClient
{
    Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        string? format,
        bool think,
        CancellationToken ct,
        string? model = null,
        double? temperature = null,
        IProgress<string>? thinkingProgress = null,
        IProgress<string>? tokenProgress = null);
}

// ── Трассировка агентского цикла (пишется в request_logs) ────────────────────

/// <summary> Один выполненный вызов tool внутри итерации. </summary>
public record AgentToolRound(
    string Name,
    string Arguments,
    int ResultChars);

/// <summary> Трасса агентского цикла одного дня: что модель запрашивала.
/// Без неё расхождения агента необъяснимы (какой факт модель видела, какой выдумала).
/// В ответ клиенту НЕ входит — только в request_logs (см. RequestLog.WriteAsync).
/// </summary>
public class AgentTrace
{
    public string? PromptVersion { get; set; }
    public List<AgentToolRound> Rounds { get; set; } = [];
    public int IterationsUsed { get; set; }
    /// <summary> Суммарный объём рассуждений модели за цикл (think:true). </summary>
    public int ThinkingChars { get; set; }
    /// <summary>
    /// Начало рассуждений (первые символы, ≤ ExcerptCap): что модель думала —
    /// видно не только живьём в панели, но и задним числом в request_logs.
    /// Полный текст не храним: раздувает логи в разы (7+ КБ на раунд).
    /// </summary>
    public string? ThinkingExcerpt { get; set; }
    /// <summary>
    /// Конец рассуждений последнего раунда (≤ <c>ThinkingTailCap</c>). Проверка рассинхрона
    /// «в мыслях эскалация, в JSON нет» смотрит ИМЕННО в конец, а ThinkingExcerpt — это
    /// начало (обрезка по 3000): у 35 из 164 прогонов с рассуждением (октябрь 2026) вывод
    /// лежал за обрезкой и проверка его не видела.
    /// </summary>
    public string? ThinkingTail { get; set; }
    /// <summary> true — цикл не дал финала (лимит/таймаут/ошибка): контроллер идёт старым путём. </summary>
    public bool FellBack { get; set; }
    public string? FallbackReason { get; set; }
    /// <summary> Сколько раз цикл перезапускали после кратковременного сбоя (0 или 1; P2.3). </summary>
    public int Retries { get; set; }
    /// <summary> Наибольший промпт среди раундов цикла, токенов (prompt_eval_count). </summary>
    public int PeakPromptTokens { get; set; }
    /// <summary> Сумма сгенерированных токенов по раундам (eval_count). </summary>
    public int TotalCompletionTokens { get; set; }
    /// <summary>
    /// true — любой раунд упёрся в контекст: промпт+ответ не меньше 95% num_ctx либо done_reason=length.
    /// Ollama при переполнении молча режет начало (правила), поэтому такие сутки не закрываются
    /// автоматически (P2.1, AutoClosePolicy).
    /// </summary>
    public bool ContextOverflow { get; set; }
    /// <summary>
    /// true — модель выдала вердикт, не запросив смысл причин при сигналах дня, и цикл
    /// один раз попросил запросить его (см. AgentLoopService.ContextNudgeReasons).
    /// </summary>
    public bool ContextNudged { get; set; }
    /// <summary>
    /// Сырой вердикт базы (до верифаеров и downstream-фильтров). Нужен, чтобы
    /// задним числом отличить «база сказала False» от «база сказала True,
    /// фильтры снесли» — иначе непонятно, работали ли вторые мнения.
    /// </summary>
    public bool? BaseRequiresReview { get; set; }
    /// <summary>
    /// Сигналы сырого вердикта базы (до верифаеров и downstream-фильтров).
    /// Без них непонятно, ЧТО именно срезали фильтры при base=True → final=False
    /// (кейс QTS350 2026-09-24: база True, итог False — что она флагнула?).
    /// Заполняет контроллер сразу после ParseResponse, до MergeVerifierOpinions.
    /// </summary>
    public List<string> BaseSignals { get; set; } = [];
}

/// <summary> Итог агентского цикла: сырой финальный текст модели (парсится штатным ParseResponse) + трасса. </summary>
public record AgentLoopOutcome(string? FinalJson, AgentTrace Trace)
{
    /// <summary>
    /// Сырые JSON вторых мнений (верифаеров). Контроллер парсит их тем же ParseResponse
    /// и сливает в общий downstream (фильтры, reset, формула) — выдуманные сигналы
    /// режутся там же, где у базового вердикта.
    /// </summary>
    public List<string> VerifierJsons { get; init; } = [];
}
