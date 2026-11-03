using AiService.Models;
using System.Text;
using System.Text.Json;

namespace AiService.Services;

/// <summary>
/// Инструменты агентского контура дневного анализа (стадия 2).
///
/// Источник фактологии — IAgentDataSource: SqlAgentDataSource (прямые read-only
/// запросы в SQL, та же машина) либо RequestAgentDataSource (данные из запроса:
/// без БД, тесты, replay). Fallback-цепочка внутри каждого инструмента:
/// БД → данные запроса → честный текст «нет данных». Цикл агента от источника
/// не падает никогда (см. AGENTS.md «Требование к серверу»).
/// Правило обрезки: каждый результат ≤ MaxResultChars, иначе контекст разъезжается.
/// </summary>
public static class AgentTools
{
    public const int MaxResultChars = 1500;

    /// <summary>
    /// Сериализация данных для модели: кириллица как есть (\u-последовательности
    /// жрут токены), без отступов. Данные — JSON, а не проза: модель не должна
    /// выпарсивать «Без М/Л», «КПД=б/н» и минуты из буллетов (кейс QTS350 2026-09-24).
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Есть ли заказ у строки: непустой и не «Без М/Л». Зеркало hasOrder из
    /// HardRuleEvaluator: единственное место решения, модель строку не разбирает
    /// (флаг естьЗаказ в JSON), промпт запрещает ей это делать.
    /// </summary>
    public static bool HasOrder(string? order) =>
        !string.IsNullOrWhiteSpace(order)
        && !order.Trim().Equals("Без М/Л", StringComparison.OrdinalIgnoreCase);

    // ── Описания для поля tools запроса /api/chat ──

    public static readonly IReadOnlyList<ChatTool> Definitions =
    [
        new ChatTool
        {
            Name = "get_reason_semantics",
            Description = "Семантика одной причины из комбобокса (что объясняет, нужна ли детализация). "
                + "Вызывай, когда на детали есть сигнал и ты решаешь, объясняет ли его причина отклонения. "
                + "Не вызывай без сигналов и когда день уже решён HARD. "
                + "Не выдумывай смысл причин — только этот источник.",
            ParametersJson = """{"type":"object","properties":{"reason":{"type":"string","description":"Текст причины (причинаОтклоненияВНаладке/причинаОтклоненияВИзготовлении из JSON дня)"},"category":{"type":"string","description":"setup (наладка) или machining (изготовление)"}},"required":["reason","category"]}""",
        },
        new ChatTool
        {
            Name = "get_part_history",
            Description = "История детали (прошлые смены под ДРУГИМИ заказами, JSON) + решения аналитиков по ним. "
                + "Нужна ТОЛЬКО для проверки заявлений «Освоение» / «Некорректные нормативы» / смена УП. "
                + "Для остальных причин историю НЕ запрашивай — она ничего не решает.",
            ParametersJson = """{"type":"object","properties":{"partName":{"type":"string"},"setup":{"type":"integer"}},"required":["partName"]}""",
        },
        new ChatTool
        {
            Name = "get_past_decisions",
            Description = "Последние решения аналитиков по этому станку (JSON, как они закрывали спорное). "
                + "Ориентир стиля, а не основание для эскалации: прошлое не создаёт текущей аномалии "
                + "и не упоминается в signals вообще.",
            ParametersJson = """{"type":"object","properties":{"limit":{"type":"integer","description":"Сколько записей, 1-10"}},"required":[]}""",
        },
        new ChatTool
        {
            Name = "get_operator_info",
            Description = "Оператор по фамилии/инициалам («Соколов А.С.», JSON: ФИО, разряд, активен). "
                + "ТОЛЬКО для имён из комментарийОператора и заявлений про ученика/неопытного "
                + "(кто именно работал). НЕ вызывай для значений мастер/комментарийМастера/"
                + "мастерНаладка/мастерИзготовление: мастера нет в cnc_operators, ответ "
                + "«не найден» про мастера — не сигнал и не означает «мастер не указан».",
            ParametersJson = """{"type":"object","properties":{"query":{"type":"string","description":"Фамилия, можно с инициалами"}},"required":["query"]}""",
        },
        new ChatTool
        {
            Name = "get_normatives",
            Description = "Числа дня по детали из данных (JSON: планНаладки, норматив, машВремя). "
                + "Не вызывай: те же числа уже в JSON деталей сообщения. "
                + "СТАДИЯ 1: читает значения из запроса; прямое чтение cnc_normatives — стадия 2 (нужен доступ AiService в БД).",
            ParametersJson = """{"type":"object","properties":{"partName":{"type":"string"}},"required":["partName"]}""",
        },
        new ChatTool
        {
            Name = "get_shift_report",
            Description = "Суточные отчёты мастера (день/ночь, JSON): мастер, простой minutes, причина, комментарий. "
                + "Для проверки релевантности пары (причина + комментарий) простою.",
            ParametersJson = """{"type":"object","properties":{},"required":[]}""",
        },
    ];

    // ── Исполнение (асинхронное: Sql-источник ходит в БД) ──

    /// <summary>
    /// Синхронная обёртка ТОЛЬКО для request-источника (тесты): там нет I/O,
    /// задачи всегда завершены синхронно — дедлока нет. БД-путь — через async.
    /// </summary>
    public static string Execute(AnalyzeRequest req, string name, string argsJson) =>
        Execute(req, RequestAgentDataSource.Instance, name, argsJson).GetAwaiter().GetResult();

    public static async Task<string> Execute(
        AnalyzeRequest req, IAgentDataSource source, string name, string argsJson, CancellationToken ct = default)
    {
        using var doc = TryParse(argsJson);
        var args = doc?.RootElement ?? default;

        var result = name switch
        {
            "get_reason_semantics" => ReasonSemantics(
                req, Get(args, "reason"), Get(args, "category")),
            "get_part_history" => await PartHistoryAsync(
                req, source, Get(args, "partName"), GetInt(args, "setup"), ct),
            "get_past_decisions" => PastDecisions(req, GetInt(args, "limit") ?? 5),
            "get_normatives" => Normatives(req, Get(args, "partName")),
            "get_shift_report" => ShiftReport(req),
            "get_operator_info" => await OperatorInfoAsync(
                req, source, Get(args, "query"), ct),
            _ => $"Неизвестный инструмент «{name}». Доступны: {string.Join(", ", Definitions.Select(d => d.Name))}.",
        };

        if (result.Length > MaxResultChars)
            result = result[..MaxResultChars] + "… [обрезано]";
        return result;
    }

    // ── Каталог причин: единственное место семантики (зеркало cnc_deviation_reasons + регламент) ──
    // Формат: причина → (категории, требует ли комментария, однострочное правило).
    // Держится в синхроне с таблицей cnc_deviation_reasons (RequireComment) вручную до стадии 2 (БД).

    private static readonly Dictionary<string, (string Cats, bool ReqComment, string Rule)> ReasonCatalog =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Освоение"] = ("наладка", false, "Объясняет ЛЮБУЮ аномалию наладки без детализации. Проверка только историей (get_part_history): запись с FinishedCount>0 под другим заказом без смены КД/технологии/УП = опровержение. Истории нет = подтверждено. Изменение УП/КД как причина освоения требует конкретики (что изменено и почему)."),
            ["Изготовление типовой детали"] = ("наладка", false, "Объясняет ТОЛЬКО КПД наладки >200%. Низкий КПД и превышение частичной наладки НЕ объясняет."),
            ["Доработка"] = ("любая", true, "plan=0 и КПД=0 — норма. Обязательна конкретика: что дорабатывается и почему. Пустое «доработка» = не объяснено."),
            ["Другое"] = ("любая", true, "Только если ничего из списка не подходит. Нужна конкретная детализация. Ожидание с другого станка («ждал деталь») — самодостаточно. Штатная операция из норматива (контроль, чистка) — НЕ объяснение, кроме «заложенного времени недостаточно». Вина оператора — объяснено, но не кандидат в exclude."),
            ["Изготовление не по техпроцессу"] = ("любая", true, "ПОДТВЕРЖДЕНИЕ эскалации, не «всё в порядке». В комментарии должно быть названо отклонение: пропущенная/лишняя/заменённая операция, другой станок («деталь с других станков», «заложена N установок», «с универсального участка»)."),
            ["Некорректные нормативы"] = ("любая", false, "ПОДТВЕРЖДЕНИЕ эскалации. Если деталь уже нормально шла с этими нормативами (см. историю) — причина сомнительна, зафиксируй отдельно."),
            ["Отсутствие нормативов"] = ("любая", false, "С заказом — ПОДТВЕРЖДЕНИЕ эскалации. Без заказа («Без М/Л») — норма, объяснений не требует."),
            ["Неопытный оператор"] = ("любая", false, "Про ЧЕЛОВЕКА, не про деталь: история не проверяется. Объясняет наладку И изготовление сразу, без конкретики. В exclude НЕ попадает."),
            ["Работа ученика"] = ("любая", false, "Как «Неопытный оператор», плюс: КПД<100% в категории причины = кандидат в exclude (ученик не должен терять К1)."),
            ["Несоответствующие заготовки"] = ("изготовление", true, "Конкретные числа в комментарии (диаметр, размер) = техническое подтверждение, дальше проверять нечего. Пограничный КПД (1-3 пункта от порога) причину не дисквалифицирует. Кандидат в exclude."),
            ["Разовое изменение времени из-за проблем с инструментом/оборудованием"] = ("изготовление", true, "Нужен конкретный инструмент/событие (не простой, не повтор оператора) + непротиворечивый КПД. Кандидат в exclude. Пороги партий сюда неприменимы."),
            ["Штучная/длительная работа"] = ("изготовление", false, "Пороги: м/в<3мин и ≤10 дет ИЛИ м/в≥3мин и ≤5 дет (проверены кодом, не пересчитывай). В пороге объясняет ЛЮБОЙ КПД кроме ровно 0%. Детализация не требуется."),
            ["Особенности изготовления"] = ("любая", true, "Требует конкретики (напр. деталь по двум установкам на двух паллетах)."),
            ["Небрежное отношение к работе"] = ("любая", true, "С конкретным комментарием — объяснено, без эскалации, в exclude НЕ попадает."),
            ["Некорректное заполнение"] = ("любая", false, "Данные недостоверны → эскалация."),
            ["Двустаночное обслуживание"] = ("любая", false, "Объясняет низкий КПД и увеличенное машинное время, ТОЛЬКО если оператор ДЕЛИЛ время между станками (станок ждал оператора). Перевод на другое рабочее место («перешёл на др. станок», «с др. станка», «помощь на других станках» без совмещения — отправили по острой нужде или нет работы на своём) — ДРУГАЯ причина: объясняет отсутствие оператора, но НЕ КПД и НЕ машинное время этого станка. Простои смены этим не объясняются, нормативы не подтверждаются. История не требуется."),
            ["Частичная наладка"] = ("наладка", false, "Переходная наладка: куски одной наладки идут через механизм простоев (указанныеПростои), в факт НЕ входят — умышленно, чтобы недоделанная работа не портила КПД. Сама по себе НЕ проблема и НЕ аномалия: проблема — ТОЛЬКО системный сигнал клиента «КПД частичной наладки … < 70%» (частичная вышла за норматив). Без этого сигнала — не эскалируй, tools по ней больше не вызывай. С ним — проверяй объяснение мастера, а не сам факт кусков."),
        };

    /// <summary>
    /// Точное совпадение с названием причины из каталога (для IsWellFormedExcludeEntry:
    /// голое название в 4-м сегменте exclude-записи — нарушение формата).
    /// </summary>
    internal static bool IsKnownReasonName(string s) =>
        !string.IsNullOrWhiteSpace(s) && ReasonCatalog.ContainsKey(s.Trim());

    private static string ReasonSemantics(AnalyzeRequest req, string reason, string category)
    {
        // Не-аномалии — единый короткий ответ, tools по ним вызывать больше не нужно.
        // (Это и есть «одно место» вместо троекратных повторов в system_prompt.txt:
        // б/н, б/и, plan=0 без заказа, частичная наладка без системного сигнала,
        // числовой КПД без системного сигнала, сам процент простоев — НЕ аномалии,
        // никогда не эскалируй.)
        if (string.IsNullOrWhiteSpace(reason))
            return "Причина не выбрана. Без причины аномалия объясняется только: б/н/б/и (НЕ аномалии всегда), числовой КПД без системного сигнала, plan=0 без заказа, частичная наладка без системного сигнала. Всё остальное без причины = не объяснено.";

        var trimmed = reason.Trim();

        // Несерийный станок: изготовление не оценивается — не заводим сигналы и
        // exclude. Разбираются только те причины, что в HardRule проверяются на
        // любом станке: «не по техпроцессу» (отклонение от технологии) и
        // «Некорректное заполнение» (достоверность данных). Причины про нормативы
        // не разбираются — спор о времени, которое здесь не учитывается.
        if (req.IsSerialMachine == false
            && category.Trim().Equals("machining", StringComparison.OrdinalIgnoreCase)
            && !HardRuleEvaluator.JudgedOnNonSerialMachining(trimmed))
            return $"«{trimmed}» — изготовление на этом станке НЕ оценивается (несерийный): "
                + "аномалии изготовления не проверяй, в signals не пиши, в exclude не предлагай.";

        // Каталог — ПЕРЕД справочником простоев: модель цитирует причины с довесками
        // («частичная наладка: 38 мин», «Другое: ждал деталь»), точное совпадение
        // не срабатывает и «неизвестна каталогу» читается как «подозрительна»
        // (кейс QTS350 2026-09-24: «Необъяснённая частичная наладка» именно отсюда).
        // Нормализация — префиксная, по самому длинному ключу; в ответе помечается.
        var key = ReasonCatalog.ContainsKey(trimmed)
            ? trimmed
            : ReasonCatalog.Keys.OrderByDescending(k => k.Length)
                .FirstOrDefault(k => trimmed.StartsWith(k, StringComparison.OrdinalIgnoreCase));
        var normalizedNote = key != null && !key.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            ? $" [запрос нормализован к «{key}»]"
            : "";

        // Причина простоя («Отсутствие оператора» и т.п.) — ДРУГОЙ справочник
        // (cnc_downtime_reasons, суточный отчёт), в каталоге отклонений детали её нет
        // и быть не должно. Отвечаем явно, иначе модель читает «неизвестна каталогу»
        // как «подозрительна» и эскалирует (пилот 24.09.2026, SKT21 22.09).
        if (key == null
            && req.DowntimeReasons.Any(r => r.Trim().Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            return $"«{trimmed}» — причина ПРОСТОЯ из закрытого списка (суточный отчёт), "
                + "а не причина отклонения детали: к наладке/изготовлению неприменима, аномалии деталей "
                + "ею не объясняй. В отчёте мастера самодостаточна: комментарий сверх неё требует "
                + "только причина «Другое».";

        if (key == null || !ReasonCatalog.TryGetValue(key, out var entry))
            return $"Причина «{trimmed}» неизвестна каталогу. Известные: {string.Join("; ", ReasonCatalog.Keys)}. Свободный комментарий без причины из каталога аномалию НЕ закрывает (кроме б/н/б/и — это состояния, а не причины).";

        var catOk = entry.Cats == "любая"
            || (category.Trim().Equals("setup", StringComparison.OrdinalIgnoreCase) && entry.Cats.Contains("налад"))
            || (category.Trim().Equals("machining", StringComparison.OrdinalIgnoreCase) && entry.Cats.Contains("изготов"));
        var binding = catOk ? "" : " ВНИМАНИЕ: причина чужой категории — наладку объясняет только причина наладки, изготовление — только причина изготовления. ";
        return $"«{reason.Trim()}» [{entry.Cats}{(entry.ReqComment ? ", требует комментария" : "")}].{normalizedNote}{binding} {entry.Rule}";
    }

    private static async Task<string> PartHistoryAsync(
        AnalyzeRequest req, IAgentDataSource source, string partName, int? setup, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(partName))
            return "Укажи partName.";

        var matches = req.Parts
            .Where(p => p.PartName.Trim().Equals(partName.Trim(), StringComparison.OrdinalIgnoreCase)
                && (!setup.HasValue || p.Setup == setup.Value))
            .ToList();
        if (matches.Count == 0)
            return $"Детали «{partName}» нет в данных дня.";

        // Стадия 2: прямой источник (БД) первичен — формат наружу тот же JSON,
        // модель разницы не видит. Пусто в БД → построчный legacy-путь по запросу.
        var fromDb = await source.GetPartHistoryAsync(req, partName.Trim(), setup, ct);
        if (fromDb != null)
            return RenderHistoryDto(matches[0], fromDb);

        var sb = new StringBuilder();
        foreach (var p in matches.Take(3))
        {
            var h = p.PartsHistory;
            // Истории нет = заявление «впервые» ПОДТВЕРЖДЕНО (проверять нечего).
            if (h == null || h.RecordsFound == 0 || h.Lines.Count == 0)
            {
                sb.Append(JsonSerializer.Serialize(new
                {
                    деталь = p.PartName,
                    уст = p.Setup,
                    заказ = p.Order,
                    прошлыхСмен = 0,
                    вывод = "под другими заказами не выполнялась = заявление «впервые» ПОДТВЕРЖДЕНО",
                }, JsonOptions));
            }
            else
            {
            sb.Append(JsonSerializer.Serialize(new
            {
                деталь = p.PartName,
                уст = p.Setup,
                заказ = p.Order,
                прошлыхСмен = h.RecordsFound,
                строки = h.Lines.Take(5).Select(l => new
                {
                    дата = l.ShiftDate,
                    готовоШт = l.FinishedCount,
                    кпдИзготовления = l.ProductionRatio,
                    кпдНаладки = l.SetupRatio,
                    решение = l.AnalystDecision,
                }),
            }, JsonOptions));
            }
            sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Один JSON-объект истории из готового DTO (БД-путь): те же ключи, что
    /// в построчном пути, заказ — из строки дня.
    /// </summary>
    internal static string RenderHistoryDto(PartContext part, PartsHistoryDto dto)
    {
        var d = new Dictionary<string, object?>
        {
            ["деталь"] = part.PartName,
            ["уст"] = part.Setup,
            ["заказ"] = part.Order,
        };
        foreach (var kv in HistoryBody(dto)) d[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(d, JsonOptions);
    }

    /// <summary>
    /// Тело истории детали: прошлыхСмен + строки (или «впервые подтверждено» при пустой
    /// истории). Общее для tool get_part_history и поля историяДетали в JSON деталей дня.
    /// </summary>
    internal static Dictionary<string, object?> HistoryBody(PartsHistoryDto dto)
    {
        if (dto.RecordsFound == 0 || dto.Lines.Count == 0)
        {
            return new Dictionary<string, object?>
            {
                ["прошлыхСмен"] = 0,
                ["вывод"] = "под другими заказами не выполнялась = заявление «впервые» ПОДТВЕРЖДЕНО",
            };
        }
        return new Dictionary<string, object?>
        {
            ["прошлыхСмен"] = dto.RecordsFound,
            ["строки"] = dto.Lines.Take(5).Select(l => new
            {
                дата = l.ShiftDate,
                готовоШт = l.FinishedCount,
                кпдИзготовления = l.ProductionRatio,
                кпдНаладки = l.SetupRatio,
                решение = l.AnalystDecision,
            }).ToList(),
        };
    }

    private static string PastDecisions(AnalyzeRequest req, int limit)
    {
        limit = Math.Clamp(limit, 1, 10);
        var rows = req.Parts
            .SelectMany(p => p.PartsHistory?.Lines ?? [])
            .GroupBy(l => l.ShiftDate)
            .OrderByDescending(g => g.Key)
            .Take(limit)
            .Select(g =>
            {
                var first = g.First();
                return new
                {
                    дата = g.Key,
                    решение = first.AnalystDecision,
                    комментарий = string.IsNullOrWhiteSpace(first.AnalystComment) ? null : first.AnalystComment.Trim(),
                    отзыв = string.IsNullOrWhiteSpace(first.AiFeedback) ? null : first.AiFeedback.Trim(),
                };
            })
            .ToList();
        return rows.Count == 0
            ? "Прошлых решений аналитика по этому станку в данных нет."
            : JsonSerializer.Serialize(rows, JsonOptions);
    }

    /// <summary>
    /// Мастер смены, чьё имя модель ошибочно ищет среди операторов: полное ФИО
    /// из запроса совпало с полем «мастер» суточного отчёта, либо совпали фамилия
    /// и хотя бы один инициал. Возвращает мастера и смены, где он указан.
    /// </summary>
    private static (string Master, string Shifts)? ShiftMasterForOperatorQuery(
        AnalyzeRequest req, string query)
    {
        var parsed = OperatorQueryParser.Parse(query);
        if (parsed == null) return null;
        foreach (var group in req.ShiftReports
            .Where(r => !string.IsNullOrWhiteSpace(r.Master))
            .GroupBy(r => r.Master.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var masterParsed = OperatorQueryParser.Parse(group.Key);
            if (masterParsed == null) continue;
            if (!masterParsed.LastName.Equals(parsed.LastName, StringComparison.OrdinalIgnoreCase))
                continue;
            var fullMatch = group.Key.Trim().Equals(query.Trim(), StringComparison.OrdinalIgnoreCase);
            var initialsMatch =
                (masterParsed.FirstInitial != null && masterParsed.FirstInitial.Equals(parsed.FirstInitial, StringComparison.OrdinalIgnoreCase))
                || (masterParsed.PatronymicInitial != null && masterParsed.PatronymicInitial.Equals(parsed.PatronymicInitial, StringComparison.OrdinalIgnoreCase));
            if (!fullMatch && !initialsMatch) continue;
            var shifts = string.Join(", ", group.Select(r => $"«{r.Shift.Trim()}»").Distinct(StringComparer.OrdinalIgnoreCase));
            return (group.Key, shifts);
        }
        return null;
    }

    private static async Task<string> OperatorInfoAsync(
        AnalyzeRequest req, IAgentDataSource source, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Укажи фамилию (можно с инициалами): например «Соколов А.С.».";
        // Граница ролей (кейс 06.10.2026, Rontek HTC420 №1 28.09): модель взяла
        // мастера из отчёта смены («Зайцев Максим Борисович»), получила «не найден»
        // и записала «Оператор Зайцев отсутствует в системе» + «мастер не указан».
        // Мастера нет в cnc_operators по построению — такой запрос не идёт в БД,
        // ответ сразу объясняет роль. Однофамилец без совпадающих инициалов
        // (например, «Зайцев А.С.» при мастере «Зайцев М.Б.») — другой человек,
        // идёт в БД как раньше.
        if (ShiftMasterForOperatorQuery(req, query) is { } master)
            return $"«{query.Trim()}» в этом дне указан как мастер смены " +
                $"({master.Shifts}), а не оператор. cnc_operators содержит только " +
                "операторов: отсутствие этой фамилии там — норма, а не сигнал; " +
                "мастера по операторам не проверяй и в signals/shift_report_issues не пиши.";
        var found = await source.FindOperatorsAsync(query.Trim(), ct);
        if (found.Count == 0)
            return $"Оператор «{query.Trim()}» не найден в cnc_operators.";
        return JsonSerializer.Serialize(found.Take(10).Select(o => new
        {
            фамилия = o.LastName,
            имя = o.FirstName,
            отчество = o.Patronymic,
            разряд = o.Qualification,
            активен = o.IsActive,
        }), JsonOptions);
    }

    private static string Normatives(AnalyzeRequest req, string partName)
    {
        if (string.IsNullOrWhiteSpace(partName))
            return "Укажи partName.";
        var matches = req.Parts
            .Where(p => p.PartName.Trim().Equals(partName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(p =>
            {
                var d = new Dictionary<string, object?>
                {
                    ["деталь"] = p.PartName,
                    ["уст"] = p.Setup,
                    ["заказ"] = p.Order,
                    ["естьЗаказ"] = HasOrder(p.Order),
                };
                // Как в RenderPartsJson: при б/н с планом>0 факт наладки не показываем.
                d["планНаладки"] = p.SetupTimePlan;
                if (!AgentLoopService.HidesSetupFact(p, p.NoSetupHappened || !p.SetupRatio.HasValue))
                    d["фактНаладки"] = p.SetupTimeFact;
                d["норматив"] = p.SingleProductionTimePlan;
                d["машВремя"] = p.MachiningTime;
                // 0 = норматив НЕ УСТАНОВЛЕН (решает модель по естьЗаказ, не по строке).
                d["готовоШт"] = p.FinishedCount;
                return d;
            })
            .ToList();
        if (matches.Count == 0)
            return $"Детали «{partName}» нет в данных дня.";
        // Источник — данные дня; прямое чтение cnc_normatives — стадия 2.
        return string.Join(" ", matches.Select(m => JsonSerializer.Serialize(m, JsonOptions)));
    }

    private static string ShiftReport(AnalyzeRequest req)
    {
        if (req.ShiftReports.Count == 0)
            return "Отчётов мастера в данных нет — проверять нечего.";
        return string.Join(" ", req.ShiftReports.Select(r =>
        {
            if (!r.ReportExists)
                return JsonSerializer.Serialize(new
                {
                    смена = r.Shift,
                    естьОтчёт = false,
                }, JsonOptions);
            // Простой — НЕотмеченное время (смена минус все отмеченные записи),
            // в КПД не входит: простой объясняет только простой, никогда — КПД.
            return JsonSerializer.Serialize(new
            {
                смена = r.Shift,
                естьОтчёт = true,
                минут = r.ShiftMinutes,
                мастер = string.IsNullOrWhiteSpace(r.Master) ? null : r.Master.Trim(),
                мастерНеУказан = string.IsNullOrWhiteSpace(r.Master),
                простойМин = r.FreshUnspecifiedDowntimes,
                простойДоля = r.ShiftMinutes > 0
                    ? Math.Round(r.FreshUnspecifiedDowntimes / r.ShiftMinutes, 4)
                    : 0,
                простойЦеликом = r.FreshUnspecifiedDowntimes >= r.ShiftMinutes && r.ShiftMinutes > 0,
                причинаПростоя = string.IsNullOrWhiteSpace(r.DowntimeReason) ? null : r.DowntimeReason.Trim(),
                комментарийМастера = string.IsNullOrWhiteSpace(r.MasterComment) ? null : r.MasterComment.Trim(),
                естьСтроки = r.HasParts,
            }, JsonOptions);
        }));
    }

    // ── Разбор аргументов ──

    private static JsonDocument? TryParse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    private static string Get(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int? GetInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var n)
            ? n
            : null;
}
