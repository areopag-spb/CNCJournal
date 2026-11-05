using AiService.Models;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace AiService.Services;

public record PromptBuildResult(string Prompt, string Version);

public class PromptBuilder
{
    private const string PromptsSubfolder = "prompts";
    private const string DefaultSystemPromptFile = "system_prompt.txt";
    private const string SoftSignalPromptFile = "soft_signal_explanation.txt";
    private const string MasterCheckPromptFile = "master_check.txt";

    private static readonly string AssemblyLocation = Assembly.GetExecutingAssembly().Location;
    private static readonly string? AssemblyDir = Path.GetDirectoryName(AssemblyLocation);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<PromptBuilder> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, CachedPrompt> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CachedPrompt
    {
        public string Content = "";
        public string Version = "unknown";
        public bool Exists;
        public DateTime WriteTimeUtc;
        public DateTime LastCheckUtc = DateTime.MinValue;
    }

    public PromptBuilder(ILogger<PromptBuilder> logger)
    {
        _logger = logger;
        // Прогрев: базовые промпты обязаны существовать (файл или embedded resource),
        // иначе падаем при старте, а не на первом запросе.
        var (_, _, defaultExists) = GetPrompt(DefaultSystemPromptFile, allowEmbeddedFallback: true);
        if (!defaultExists)
            throw new FileNotFoundException($"Не найден базовый промпт '{DefaultSystemPromptFile}' (ни внешний файл, ни embedded resource).");
        GetPrompt(SoftSignalPromptFile, allowEmbeddedFallback: true);
        GetPrompt(MasterCheckPromptFile, allowEmbeddedFallback: true);
    }

    /// <summary>
    /// Возвращает содержимое и версию промпта по имени файла с кэшированием и
    /// hot-reload (проверка изменений не чаще, чем раз в CheckInterval).
    /// </summary>
    private (string Content, string Version, bool Exists) GetPrompt(string fileName, bool allowEmbeddedFallback)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(fileName, out var entry))
            {
                entry = new CachedPrompt();
                _cache[fileName] = entry;
            }

            var now = DateTime.UtcNow;
            if ((now - entry.LastCheckUtc) >= CheckInterval)
            {
                entry.LastCheckUtc = now;
                RefreshEntry(fileName, entry, allowEmbeddedFallback);
            }

            return (entry.Content, entry.Version, entry.Exists);
        }
    }

    private void RefreshEntry(string fileName, CachedPrompt entry, bool allowEmbeddedFallback)
    {
        var externalPath = Path.Combine(AssemblyDir ?? AppContext.BaseDirectory, PromptsSubfolder, fileName);

        if (File.Exists(externalPath))
        {
            var writeTimeUtc = File.GetLastWriteTimeUtc(externalPath);
            if (entry.Exists && writeTimeUtc == entry.WriteTimeUtc) return;

            var raw = File.ReadAllText(externalPath);
            var stripped = StripVersionLine(raw);
            entry.Content = stripped?.content ?? raw;
            entry.Version = WithContentHash(
                stripped?.version ?? writeTimeUtc.ToString("yyyy-MM-dd-HH:mm"), entry.Content);
            entry.WriteTimeUtc = writeTimeUtc;
            entry.Exists = true;
            _logger.LogInformation("Промпт '{FileName}' version {Version} загружен из файла: {Path}",
                fileName, entry.Version, externalPath);
            return;
        }

        if (entry.Exists && !allowEmbeddedFallback)
        {
            // Файл профиля удалили на лету — перестаём его использовать.
            entry.Exists = false;
            entry.Content = "";
            _logger.LogWarning("Промпт '{FileName}' исчез с диска — будет использоваться базовый", fileName);
            return;
        }

        if (entry.Exists || !allowEmbeddedFallback) return;

        var resourceName = $"AiService.prompts.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        var embeddedRaw = reader.ReadToEnd();
        var embeddedStripped = StripVersionLine(embeddedRaw);
        entry.Content = embeddedStripped?.content ?? embeddedRaw;
        entry.Version = embeddedStripped?.version is { } embeddedLabel
            ? WithContentHash(embeddedLabel, entry.Content)
            : "unknown";
        entry.Exists = true;
        _logger.LogInformation("Промпт '{FileName}' version {Version} загружен из embedded resource",
            fileName, entry.Version);
    }

    /// <summary>
    /// Версия = ручная метка из «;; version:» + короткий хэш содержимого: правка
    /// промпта без подъёма метки всё равно меняет версию. CRLF/LF нормализуются,
    /// чтобы хэш репозитория и прода совпадал.
    /// </summary>
    public static string WithContentHash(string label, string content) =>
        $"{label}+{ShortHash(content, 8)}";

    public static string ShortHash(string content, int length)
    {
        var normalized = content.Replace("\r\n", "\n");
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..length].ToLowerInvariant();
    }

    /// <summary>
    /// Добавляет в версию хэш дополнительного блока промпта (soft-сигналы),
    /// сохраняя суффикс профиля «@agent» в конце.
    /// </summary>
    public static string WithExtraBlock(string version, string blockContent)
    {
        var at = version.IndexOf('@');
        var extra = $"+s{ShortHash(blockContent, 4)}";
        return at < 0 ? version + extra : version[..at] + extra + version[at..];
    }

    private static (string content, string version)? StripVersionLine(string text)
    {
        var reader = new StringReader(text);
        var firstLine = reader.ReadLine();
        if (firstLine != null && firstLine.StartsWith(";; version:"))
        {
            var version = firstLine[";; version:".Length..].Trim();
            var rest = reader.ReadToEnd();
            return (rest, version);
        }
        return null;
    }

    /// <summary>
    /// Выбор системного промпта по профилю станка (cnc_machines.AiPromptProfile,
    /// передаётся клиентом в PromptProfile). Пустой/некорректный профиль или
    /// отсутствующий файл — базовый system_prompt.txt. Версия профильного промпта
    /// автоматически получает суффикс @профиль, чтобы в ai_day_reviews точность
    /// считалась раздельно по профилям.
    /// </summary>
    private (string Content, string Version) ResolveSystemPrompt(string? profile)
    {
        var normalized = NormalizeProfile(profile);
        if (normalized != null)
        {
            var (content, version, exists) = GetPrompt($"system_prompt.{normalized}.txt", allowEmbeddedFallback: false);
            if (exists)
                return (content, $"{version}@{normalized}");
            _logger.LogDebug("Файл промпта для профиля '{Profile}' не найден — используется базовый", normalized);
        }

        var (defContent, defVersion, _) = GetPrompt(DefaultSystemPromptFile, allowEmbeddedFallback: true);
        return (defContent, defVersion);
    }

    private static string? NormalizeProfile(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return null;
        var trimmed = profile.Trim().ToLowerInvariant();
        return Regex.IsMatch(trimmed, @"^[a-z0-9_\-]{1,50}$") ? trimmed : null;
    }

    // ═══ История детали: показывается модели только когда есть заявление, которое
    // история может подтвердить или опровергнуть («Требования к заполнению и
    // контролю», п.4.3). Во всех остальных случаях история — источник галлюцинаций
    // («раньше наладка была, а сегодня б/н») и в промпт не попадает.
    //   «Освоение» — деталь впервые на станке; опровергается записью с FinishedCount>0.
    //   «Некорректные нормативы»/«Отсутствие нормативов» — «если деталь уже нормально
    //   выполнялась с этими нормативами — норматив корректен»: история опровергает.
    //   Свободный текст про освоение/УП/нормативы — та же логика по слабой ветке.
    private static readonly string[] HistorySensitiveReasons =
    [
        "Освоение",
        "Некорректные нормативы",
        "Отсутствие нормативов",
    ];

    internal static bool HistoryIsRelevant(PartContext p)
    {
        foreach (var reason in HistorySensitiveReasons)
        {
            if (reason.Equals(p.MasterSetupComment?.Trim(), StringComparison.OrdinalIgnoreCase)
                || reason.Equals(p.MasterMachiningComment?.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var text = $"{p.MasterSetupDetail} {p.MasterMachiningDetail} {p.MasterComment} {p.OperatorComment}".ToLowerInvariant();
        if (text.Contains("осво") || text.Contains("осваив")) return true;   // освоение / освоена / осваивает
        if (text.Contains("вперв") || text.Contains("первый раз")) return true;
        if (text.Contains("норматив") || text.Contains("некорректн")) return true;
        if (text.Contains("управляющ") || Regex.IsMatch(text, @"\bуп\b")) return true; // написание/правка УП
        return false;
    }

    /// <summary>
    /// Отпечаток всех файлов промптов (имя, размер, время записи): меняется при любой правке или замене
    /// файла, в том числе при hot-reload без перезапуска. Нужен для «версии анализа» (AnalysisVersion).
    /// </summary>
    public string PromptsFingerprint()
    {
        var dir = Path.Combine(AssemblyDir ?? AppContext.BaseDirectory, PromptsSubfolder);
        if (!Directory.Exists(dir)) return "no-prompts";
        var parts = new DirectoryInfo(dir).EnumerateFiles("*.txt")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}");
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Системный скелет агентского контура: тощий system_prompt.agent.txt + суффикс @agent
    /// в версии (точность в ai_day_reviews считается раздельно). Вызывается только
    /// из AgentLoopService, штатный Build не затрагивается.
    /// </summary>
    public PromptBuildResult BuildAgentSystem()
    {
        var (content, version, exists) = GetPrompt("system_prompt.agent.txt", allowEmbeddedFallback: true);
        if (!exists)
            throw new FileNotFoundException("Не найден промпт 'system_prompt.agent.txt' (ни файл, ни embedded resource).");
        return new PromptBuildResult(content, $"{version}@agent");
    }

    /// <summary>
    /// Правила понижения soft-сигналов (аудит 25.09: в агентском пути их не было —
    /// модель решала вслепую). Пусто, если soft-сигналов нет.
    /// </summary>
    public string GetSoftPrompt(IReadOnlyList<string> softSignals)
    {
        if (softSignals.Count == 0) return "";
        var (softPrompt, _, softExists) = GetPrompt("soft_signal_explanation.txt", allowEmbeddedFallback: true);
        return softExists ? softPrompt : "";
    }

    /// <summary>
    /// Текст блока «Отчёт мастера за смену» для агентского user-сообщения —
    /// те же строки фактов, что видит обычная модель (см. AppendShiftReports).
    /// Агент судит только релевантность (S1), структуру решают hard-правила.
    /// Только чтение, штатный Build не затрагивается.
    /// </summary>
    public static string RenderShiftReports(AnalyzeRequest req)
    {
        var sb = new StringBuilder();
        AppendShiftReports(sb, req, hiddenShifts: null);
        return sb.ToString();
    }

    /// <summary>
    /// Блоки отчётов смен, решать по которым нечего (превердикт Clear), не рендерятся:
    /// судить нечего — показывать нечего, иначе модель придирается к материалу,
    /// который ей запрещено судить (кейсы Integrex 09/10.2026: флаг полного простоя
    /// с причиной из списка). Остаётся только строка СИСТЕМА в скелете. Смены,
    /// которых нет в hiddenShifts (нужен модели разбор, hard-нарушения), — как раньше.
    /// Пустой итог (все скрыты): заголовок секции тоже не пишется.
    /// </summary>
    public static string RenderShiftReports(AnalyzeRequest req, IReadOnlySet<string> hiddenShifts)
    {
        var sb = new StringBuilder();
        AppendShiftReports(sb, req, hiddenShifts);
        return sb.ToString();
    }

    /// <summary>
    /// Записей за сутки нет: станок не работал. Пустой список модель трактует как «данные потерялись»
    /// и начинает рассуждать, чего не хватает; поэтому говорим прямо, что это норма и что смотреть.
    /// </summary>
    public const string NoPartsNotice =
        "ЗАПИСЕЙ ЗА СУТКИ НЕТ: станок в эти сутки не работал (ни наладки, ни изготовления). " +
        "Это норма, а не потеря данных: КПД, нормативы, заказы и детали оценивать НЕЧЕГО, " +
        "не рассуждай об их отсутствии и не делай из него сигналов. " +
        "Единственное, что проверяется, — отчёт мастера за сутки (раздел ниже).";

    public PromptBuildResult Build(AnalyzeRequest req, HardRuleResult hardRules, ShiftReportRuleResult shiftRules)
    {
        var (systemPrompt, promptVersion) = ResolveSystemPrompt(req.PromptProfile);

        var sb = new StringBuilder();
        sb.AppendLine(systemPrompt);

        if (hardRules.SoftSignals.Count > 0)
        {
            var (softPrompt, _, softExists) = GetPrompt(SoftSignalPromptFile, allowEmbeddedFallback: true);
            if (softExists)
            {
                sb.AppendLine();
                sb.AppendLine(softPrompt);
                promptVersion = WithExtraBlock(promptVersion, softPrompt);
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Станок: {req.Machine}" +
                      (req.IsSerialMachine == false
                      ? " [несерийный — изготовление не оценивается: ни КПД, ни машинное время]"
                      : ""));
        sb.AppendLine($"Дата: {req.ShiftDate}");
        sb.AppendLine($"Записей: {req.Parts.Count}");
        if (req.Parts.Count == 0) sb.AppendLine(NoPartsNotice);

        if (req.Signals.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Сигналы уровня дня:");
            foreach (var sig in req.Signals)
                sb.AppendLine($" ⚠ {sig}");
        }

        AppendShiftReports(sb, req,
            ShiftReportRuleEvaluator.ClearShiftKeys(ShiftReportRuleEvaluator.AgentPreverdicts(req, shiftRules)));

        sb.AppendLine();

        foreach (var p in req.Parts)
            AppendPartBlock(sb, p);

        sb.AppendLine();
        sb.AppendLine("════════════════════════════════════════");

        if (hardRules.HardSignals.Count > 0 || shiftRules.HardSignals.Count > 0)
        {
            sb.AppendLine("СИСТЕМА УЖЕ ОПРЕДЕЛИЛА: requires_review = true (правила ниже неотменяемы).");
            if (hardRules.HardSignals.Count > 0)
            {
                sb.AppendLine("Сработавшие жёсткие правила:");
                foreach (var r in hardRules.HardSignals)
                    sb.AppendLine($" • {r}");
            }
            if (shiftRules.HardSignals.Count > 0)
            {
                sb.AppendLine("Сработавшие жёсткие правила по отчёту мастера:");
                foreach (var r in shiftRules.HardSignals)
                    sb.AppendLine($" • {r}");
            }
            sb.AppendLine("Поле requires_review в ответе ОБЯЗАНО быть true.");
            sb.AppendLine("Поле confidence ОБЯЗАНО быть в диапазоне 0.85-1.0.");
        }
        else
        {
            sb.AppendLine("Жёсткие неотменяемые правила НЕ сработали. Реши требуется ли эскалация,");
            sb.AppendLine("опираясь на DECISION ALGORITHM и ESCALATION RULES выше и контекст комментариев.");
        }

        if (hardRules.SoftSignals.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Soft-сигналы для этого дня (правила понижения — см. выше):");
            foreach (var s in hardRules.SoftSignals)
                sb.AppendLine($" • {s}");
        }

        sb.AppendLine();
        sb.AppendLine("Ответь СТРОГО в этом формате, без markdown, без преамбулы:");
        sb.AppendLine("""
        {
          "requires_review": true или false,
          "confidence": число от 0.0 до 1.0,
          "signals": ["краткие описания необъяснённых проблем, если есть"],
          "downgraded_signals": ["soft-сигналы из списка выше, которые ты решил НЕ эскалировать — если таких нет, пустой массив"],
          "suggest_exclude_from_reports": ["PartName§SetupNumber§Order§Причина для деталей с адекватно объяснённой разовой проблемой ИЛИ подтверждённым «Освоением» с КПД наладки < 100% — если таких нет, пустой массив. Причина — 3-10 слов КОНКРЕТНО про эту деталь, НЕ общий вывод по суткам; для освоения укажи, что КПД ниже 100% может негативно повлиять на К1 оператора"],
          "shift_report_issues": ["Смена День/Ночь: в чём несоответствие отчёта мастера (см. 2.8) — если таких нет, пустой массив"],
          "explanation": "1-2 предложения, ОБЩИЙ вывод без перечисления проблем из списков — ОБЯЗАТЕЛЬНОЕ непустое поле",
          "suggested_reason": "краткая ОБЩАЯ причина в 3-7 слов без деталей — ОБЯЗАТЕЛЬНОЕ непустое поле"
        }
        """);

        return new PromptBuildResult(sb.ToString(), promptVersion);
    }

    /// <summary>
    /// Компактный промпт проверки ОДНОЙ записи: релевантно ли комментарии мастера
    /// объясняют присланные клиентом аномалии. Использует тот же формат блока
    /// детали, что и полный анализ (AppendPartBlock), — модели знаком формат,
    /// а форматы не разъезжаются.
    /// </summary>
    public PromptBuildResult BuildMasterCheck(VerifyPartRequest req)
    {
        var (systemPrompt, promptVersion, _) = GetPrompt(MasterCheckPromptFile, allowEmbeddedFallback: true);

        var sb = new StringBuilder();
        sb.AppendLine(systemPrompt);

        sb.AppendLine();
        sb.AppendLine($"Станок: {req.Machine}");
        sb.AppendLine($"Дата: {req.ShiftDate}");
        sb.AppendLine();

        AppendPartBlock(sb, req.Part);

        sb.AppendLine("Проверяемые аномалии:");
        foreach (var a in req.Anomalies)
        {
            var comment = FieldValue(req.Part, a.Field);
            sb.AppendLine($" • [{FieldTitle(a.Field)}] {a.Description} — объясняется комментарием: «{comment.Trim()}»");
        }

        sb.AppendLine();
        sb.AppendLine("Ответь СТРОГО в этом формате, без markdown, без преамбулы:");
        sb.AppendLine("""
        {
          "ok": true или false,
          "remark": "при ok=false — 1-2 предложения, ЧТО именно не объяснено и по какой аномалии; при ok=true пустая строка"
        }
        """);

        return new PromptBuildResult(sb.ToString(), promptVersion);
    }

    private static string FieldValue(PartContext p, string field) => field switch
    {
        "MasterSetupComment" => p.MasterSetupComment,
        "MasterMachiningComment" => p.MasterMachiningComment,
        "MasterComment" => p.MasterComment,
        "MasterSetupDetail" => !string.IsNullOrWhiteSpace(p.MasterSetupDetail) ? p.MasterSetupDetail : p.MasterComment,
        "MasterMachiningDetail" => !string.IsNullOrWhiteSpace(p.MasterMachiningDetail) ? p.MasterMachiningDetail : p.MasterComment,
        "SpecifiedDowntimesComment" => p.SpecifiedDowntimesComment,
        _ => "",
    };

    private static string FieldTitle(string field) => field switch
    {
        "MasterSetupComment" => "причина отклонения в наладке",
        "MasterMachiningComment" => "причина отклонения в изготовлении",
        "MasterComment" => "комментарий мастера",
        "MasterSetupDetail" => "комментарий мастера (наладка)",
        "MasterMachiningDetail" => "комментарий мастера (изготовление)",
        "SpecifiedDowntimesComment" => "комментарий к простоям",
        _ => field,
    };

    private const string ChatSystemPromptFile = "chat_system.txt";

    private static string OverrideBy(PartContext p) =>
        string.IsNullOrWhiteSpace(p.ReasonOverrideBy) ? "" : $" ({p.ReasonOverrideBy.Trim()})";

    public record ChatBuildResult(string System, string Version, List<ChatMessage> Messages);

    /// <summary>
    /// Сообщения чата-ДОЗОРа для агентского цикла: системный chat_system.txt (роль,
    /// БЕЗ данных смены — факты модель берёт инструментами ChatTools) + история
    /// диалога. Сниппеты веб-поиска приклеиваются к последнему вопросу пользователя,
    /// а не отдельным сообщением — иначе диалог выглядит чужеродно.
    /// Историю режем со старых сообщений, пока не влезем в maxChars; системный
    /// промпт не режется никогда (иначе модель теряет роль).
    /// </summary>
    public ChatBuildResult BuildChatMessages(
        ChatRequest req, IReadOnlyList<WebSearchResult> webResults,
        int maxHistoryMessages, int maxChars)
    {
        var (systemPrompt, promptVersion, _) = GetPrompt(ChatSystemPromptFile, allowEmbeddedFallback: true);

        var history = (req.Messages ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => new ChatMessage
            {
                Role = (m.Role ?? "").Trim().Equals("assistant", StringComparison.OrdinalIgnoreCase)
                    ? "assistant" : "user",
                Content = m.Content.Trim(),
            })
            .ToList();
        if (history.Count > maxHistoryMessages)
            history = history[^maxHistoryMessages..];

        var webBlock = RenderWebResults(webResults);
        if (webBlock.Length > 0)
        {
            for (var i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].Role != "user") continue;
                history[i].Content += "\n\n" + webBlock;
                break;
            }
        }

        static int HistoryLen(List<ChatMessage> hist)
        {
            var len = 0;
            foreach (var m in hist) len += (m.Content?.Length ?? 0) + 16;
            return len;
        }

        // Бюджет символов относится к ИСТОРИИ, а не к «промпту + истории».
        // Иначе растущий системный промпт съедал весь диалог: он уже 27 890
        // символов (15 этапов правил), а Chat:MaxContextChars = 12000, и цикл
        // вырезал сообщения одно за другим, пока не оставалось одно —
        // последнее. Модель работала как статeless: не помнила, что говорила
        // сама, и плохо реагировала на поправки оператора.
        while (HistoryLen(history) > maxChars && history.Count > 1)
            history.RemoveAt(0);

        return new ChatBuildResult(systemPrompt, promptVersion, history);
    }

    /// <summary>
    /// Снимок смены из eLog одной строкой фактов: станок, смена, оператор,
    /// активная деталь + простои. Пустые поля пропускаются, переносы строк
    /// в комментариях сплющиваются — иначе разъезжается формат диалога.
    /// </summary>
    public static string RenderElogContext(ElogChatContext? ctx)
    {
        static string Safe(string? s) => (s ?? "").Trim().Replace("\r", "").Replace("\n", " / ");
        var sb = new StringBuilder();
        sb.AppendLine("Контекст смены (eLog):");
        if (ctx == null)
        {
            sb.AppendLine("Контекст не прислан — отвечай в общем виде, уточняй детали у собеседника.");
            return sb.ToString();
        }

        // Выбрана ≠ активна: значение из комбобокса — не работа. Формулировка
        // однозначная, чтобы модель не говорила «вы работаете в смену День»,
        // когда смена только выбрана, но не начата.
        sb.Append($"Станок: {Safe(ctx.Machine)} | Смена: {Safe(ctx.Shift)} " +
                  (ctx.ShiftStarted
                      ? "(активна — оператор работает)"
                      : "(только ВЫБРАНА в журнале, НЕ начата — оператор вне смены)") +
                  $" | Оператор: {Safe(ctx.OperatorName)}");
        sb.AppendLine();
        sb.AppendLine($"Записей деталей: {ctx.PartsCount}");

        var part = ctx.ActivePart;
        if (part == null || string.IsNullOrWhiteSpace(part.Name))
        {
            sb.AppendLine("Активной детали нет.");
            return sb.ToString();
        }

        sb.Append($"Активная деталь: «{Safe(part.Name)}»");
        if (!string.IsNullOrWhiteSpace(part.Number)) sb.Append($" ({Safe(part.Number)})");
        if (!string.IsNullOrWhiteSpace(part.Order)) sb.Append($" | М/Л «{Safe(part.Order)}»");
        sb.Append($" | уст. №{part.Setup}");
        if (!string.IsNullOrWhiteSpace(part.StartSetupTime)) sb.Append($", наладка с {Safe(part.StartSetupTime)}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(part.OperatorComment))
            sb.AppendLine($"Комментарий оператора: «{Safe(part.OperatorComment)}»");

        if (part.Downtimes.Count > 0)
        {
            sb.AppendLine("Простои по детали:");
            foreach (var d in part.Downtimes)
            {
                var span = $"{Safe(d.Start)} → {(string.IsNullOrWhiteSpace(d.End) ? "идёт сейчас" : Safe(d.End))}";
                var comment = string.IsNullOrWhiteSpace(d.Comment) ? "" : $" — «{Safe(d.Comment)}»";
                sb.AppendLine($" • {Safe(d.Name)} ({span}){comment}");
            }
        }
        else
        {
            sb.AppendLine("Простоев по детали нет.");
        }

        return sb.ToString();
    }

    private static string RenderWebResults(IReadOnlyList<WebSearchResult> webResults)
    {
        if (webResults.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"Результаты поиска в сети ({webResults.Count}, отвечай СТРОГО по ним, прикладывай ссылки [n]):");
        var n = 0;
        foreach (var r in webResults)
        {
            n++;
            sb.AppendLine($"[{n}] {r.Title} — {r.Url}");
            sb.AppendLine($"    {r.Snippet}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Блоки суточных отчётов мастера (2.8 промпта): свежие значения простоя,
    /// причина и комментарий — материал для семантической проверки релевантности (S1).
    /// Структурные нарушения модель не решает (см. hard-блок выше), ей нужны только факты.
    /// </summary>
    private static void AppendShiftReports(StringBuilder sb, AnalyzeRequest req, IReadOnlySet<string>? hiddenShifts = null)
    {
        if (req.ShiftReports.Count == 0) return;

        static string Safe(string s) => (s ?? "").Trim().Replace("\r", "").Replace("\n", " / ");
        static string Key(ShiftReportContext r) => (r.Shift ?? "").Trim().ToLowerInvariant();

        var visible = req.ShiftReports
            .Where(r => hiddenShifts == null || !hiddenShifts.Contains(Key(r)))
            .ToList();
        if (visible.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("Отчёт мастера за смену:");
        foreach (var r in visible)
        {
            if (!r.ReportExists || r.ShiftMinutes <= 0)
            {
                // Отсутствие и устаревание — разные вещи: строки в cnc_shifts нет,
                // поэтому «устарел» и «мастер не указан» про эту смену писать нечего.
                sb.AppendLine($" ▸ Смена: {r.Shift} — отчёта нет, проверять нечего");
                continue;
            }

            var ratio = r.FreshUnspecifiedDowntimes / r.ShiftMinutes;
            var idle = r.FreshUnspecifiedDowntimes >= r.ShiftMinutes ? " [простой целиком]" : "";
            var master = string.IsNullOrWhiteSpace(r.Master) ? "не указан" : $"«{Safe(r.Master)}»";
            var reason = string.IsNullOrWhiteSpace(r.DowntimeReason) ? "—" : $"«{Safe(r.DowntimeReason)}»";
            var comment = string.IsNullOrWhiteSpace(r.MasterComment) ? "—" : $"«{Safe(r.MasterComment)}»";
            sb.AppendLine($" ▸ Смена: {r.Shift} | Мастер: {master}{idle}");
            sb.AppendLine($"   Простой: {r.FreshUnspecifiedDowntimes:0}мин ({ratio:0%} смены {r.ShiftMinutes}мин) | " +
                          $"Причина: {reason} | Комментарий: {comment}");
        }
    }

    private static void AppendPartBlock(StringBuilder sb, PartContext p)
    {
        var setupR = p.SetupRatio.HasValue ? $"{p.SetupRatio.Value:0%}" : "б/н";
        var prodR = p.ProductionRatio.HasValue ? $"{p.ProductionRatio.Value:0%}" : "б/и";
        var dtR = p.DowntimeRatio.HasValue ? $"{p.DowntimeRatio.Value:0%}" : "—";

        sb.Append($"▸ Деталь: «{p.PartName}» | М/Л: «{p.Order}» | Уст. №{p.Setup}");
        if (p.NoSetupHappened) sb.Append(" [наладки не было]");
        if (p.NoProductionHappened) sb.Append(" [изготовления не было]");
        sb.AppendLine();

        sb.AppendLine($"Наладка: план={p.SetupTimePlan:0}мин, факт={p.SetupTimeFact:0}мин, КПД={setupR}" +
                      (p.PartialSetup > 0 ? $", частичная={p.PartialSetup:0}мин" : ""));
        sb.AppendLine($"Изготовление: норматив={p.SingleProductionTimePlan:0.#}мин/дет, " +
                      $"выполнено={p.FinishedCount:0}шт, КПД={prodR}");

        if (p.MachiningTime > 0 || p.SingleProductionTimePlan > 0)
            sb.AppendLine($"Машинное время/дет: {p.MachiningTime:0.#}мин | Простои: {dtR}");

        var comments = new List<string>();
        static string Safe(string s) => s.Trim().Replace("\r", "").Replace("\n", " / ");
        if (!string.IsNullOrWhiteSpace(p.OperatorComment)) comments.Add($"оператор: «{Safe(p.OperatorComment)}»");
        if (!string.IsNullOrWhiteSpace(p.MasterSetupComment)) comments.Add($"причина отклонения в наладке: «{Safe(p.MasterSetupComment)}»");
        if (!string.IsNullOrWhiteSpace(p.MasterMachiningComment)) comments.Add($"причина отклонения в изготовлении: «{Safe(p.MasterMachiningComment)}»");
        // MasterComment — архивное поле (до разделения на наладку/изготовление): показываем его
        // ТОЛЬКО когда оба новых поля пусты (старая запись, без backfill) — иначе новые поля сами
        // по себе уже однозначно относят детализацию к своей категории, дублировать нечего.
        if (!string.IsNullOrWhiteSpace(p.MasterSetupDetail)) comments.Add($"мастер (наладка): «{Safe(p.MasterSetupDetail)}»");
        if (!string.IsNullOrWhiteSpace(p.MasterMachiningDetail)) comments.Add($"мастер (изгот.): «{Safe(p.MasterMachiningDetail)}»");
        if (string.IsNullOrWhiteSpace(p.MasterSetupDetail) && string.IsNullOrWhiteSpace(p.MasterMachiningDetail)
            && !string.IsNullOrWhiteSpace(p.MasterComment)) comments.Add($"мастер: «{Safe(p.MasterComment)}»");
        if (comments.Count > 0)
            sb.AppendLine($"  Комментарии: {string.Join("; ", comments)}");

        // Переопределение причины аналитиком (СГТ 1). Непустое означает, что причина выше —
        // решение СГТ, а не мастера; детализация мастера при этом осталась от его версии и
        // новой причине может не соответствовать (см. блок ЧЬЯ ЭТО ПРИЧИНА в system_prompt).
        // Для verify-part клиент шлёт эти поля пустыми: там проверяется именно мастер.
        if (!string.IsNullOrWhiteSpace(p.SetupReasonOverride))
        {
            sb.AppendLine($"  ⇄ Причину наладки переопределил СГТ{OverrideBy(p)}: «{Safe(p.SetupReasonOverride)}»" +
                          (string.IsNullOrWhiteSpace(p.SetupReasonOverrideComment)
                              ? "" : $" — обоснование: «{Safe(p.SetupReasonOverrideComment)}»"));
        }
        if (!string.IsNullOrWhiteSpace(p.MachiningReasonOverride))
        {
            sb.AppendLine($"  ⇄ Причину изготовления переопределил СГТ{OverrideBy(p)}: «{Safe(p.MachiningReasonOverride)}»" +
                          (string.IsNullOrWhiteSpace(p.MachiningReasonOverrideComment)
                              ? "" : $" — обоснование: «{Safe(p.MachiningReasonOverrideComment)}»"));
        }

        if (!string.IsNullOrWhiteSpace(p.SpecifiedDowntimesList))
            sb.AppendLine($"  {p.SpecifiedDowntimesList.Trim()}");
        if (!string.IsNullOrWhiteSpace(p.SpecifiedDowntimesComment))
            sb.AppendLine($"  Комментарий к простоям (мастер): «{p.SpecifiedDowntimesComment.Trim()}»");

        if (!string.IsNullOrWhiteSpace(p.SpecifiedDowntimesList) ||
            !string.IsNullOrWhiteSpace(p.SpecifiedDowntimesComment))
            sb.AppendLine();

        if (p.Signals is { Count: > 0 })
        {
            sb.AppendLine("  Сигналы системы (числовые факты):");
            foreach (var sig in p.Signals)
                sb.AppendLine($"    ⚠ {sig}");
        }
        if (p.PartsHistory is { RecordsFound: > 0 } ph && HistoryIsRelevant(p))
        {
            sb.AppendLine($"  История этой детали ({ph.RecordsFound} прошлых смен, от новых к старым):");
            foreach (var line in ph.Lines)
            {
                var flag = line.HasUnexplainedLowEfficiency ? " ⚠низкий КПД" : "";

                var decision = line.AnalystDecision switch
                {
                    "escalated" => "эскалация",
                    "ok" => "ок",
                    _ => "не проверено",
                };

                sb.Append($"    {line.ShiftDate}");
                sb.Append($" | КПД изгот.={line.ProductionRatio}");
                sb.Append($" | КПД нал.={line.SetupRatio}");
                sb.Append($" | изгот.={line.FinishedCount}шт");
                sb.Append($" | аналитик: {decision}{flag}");

                if (!string.IsNullOrWhiteSpace(line.AnalystComment))
                    sb.Append($" — «{line.AnalystComment}»");

                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(line.AiExplanation)
                    && line.AnalystDecision == "escalated")
                {
                    sb.AppendLine($"      AI тогда: {line.AiExplanation}");
                }

                if (!string.IsNullOrWhiteSpace(line.AiFeedback)
                    && line.AnalystDecision == "escalated")
                {
                    sb.AppendLine($"      Коррекция аналитика: {line.AiFeedback}");
                }
            }
        }

        sb.AppendLine();
    }
}
