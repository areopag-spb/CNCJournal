using AiService.Models;

namespace AiService.Services;

/// <summary>
/// Результат детерминированной проверки суточных отчётов мастера (cnc_shifts).
/// HardSignals — нарушения, требующие ОБЯЗАТЕЛЬНОЙ эскалации (LLM не отменяет):
/// аналог HardRuleResult для построчных правил. SoftSignals — рекомендательные
/// (сейчас только S2 про частичную наладку): в ответ попадают, эскалацию не форсируют.
/// Семантическая релевантность пары (причина + комментарий), S1, добавится с промптом.
/// </summary>
public record ShiftReportRuleResult(
    List<string> HardSignals,
    List<string> SoftSignals)
{
    public bool MustEscalate => HardSignals.Count > 0;
}

public static class ShiftReportRuleEvaluator
{
    // Порог обязательности причины простоя — зеркалит DailyReportWindowViewModel:
    // UnspecifiedDayDowntimesNeedAttention => ratio is > 0.05 or < -0.05.
    private const double DowntimeReasonRatioThreshold = 0.05;

    // Допуск переполнения журнала сверх смены (ранний приход/задержка).
    // Ниже −5% длительности — hard R3, комментарии не объясняют.
    private const double OverflowToleranceRatio = 0.05;

    // Допуск расхождения снимка простоя из БД со свежим пересчётом (R4, минуты).
    // Формула детерминирована: любое расхождение сверх допуска — правки журнала
    // после сохранения отчёта, отчёт устарел.
    private const double StaleEpsilonMinutes = 1.0;

    // «Другое» (R2) требует детализации по регламенту («Требования к заполнению
    // и контролю», таблица причин). Минимум осмысленности, проверяемый кодом, —
    // длина; релевантность текста позже проверит модель (S1).
    private const int MinOtherCommentLength = 10;
    private const string OtherReason = "Другое";

    // Порог внимания к частичной наладке (S2) — зеркалит
    // DailyReportWindowViewModel.DayPartialSetupNeedAttention (ratio > 0.3).
    private const double PartialSetupAttentionRatio = 0.3;

    public static ShiftReportRuleResult Evaluate(AnalyzeRequest req)
    {
        var hard = new List<string>();
        var soft = new List<string>();

        // Старый клиент отчёты не шлёт — проверка пропускается молча.
        if (req.ShiftReports.Count == 0)
            return new ShiftReportRuleResult(hard, soft);

        var knownReasons = req.DowntimeReasons
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rep in req.ShiftReports)
        {
            var tag = $"[{rep.Shift}]";
            void AddHard(string signal) => hard.Add($"{tag} {signal}");

            if (!rep.ReportExists)
            {
                AddHard("Нет суточного отчёта мастера за смену — день закрывать нельзя");
                continue;
            }

            if (string.IsNullOrWhiteSpace(rep.Master))
                AddHard("В отчёте мастера не указан мастер");

            if (rep.ShiftMinutes <= 0)
            {
                AddHard("Некорректная длительность смены в запросе — проверка отчёта невозможна");
                continue;
            }

            var fresh = rep.FreshUnspecifiedDowntimes;
            var ratio = fresh / rep.ShiftMinutes;
            var reason = rep.DowntimeReason?.Trim() ?? string.Empty;
            var comment = rep.MasterComment?.Trim() ?? string.Empty;

            // R3: невозможный простой. Переполнение сверх допуска — hard безусловно.
            if (fresh < -OverflowToleranceRatio * rep.ShiftMinutes)
                AddHard($"Переполнение журнала {fresh:0}мин сверх смены ({ratio:0%}) — никакие комментарии не объясняют");
            else if (fresh > rep.ShiftMinutes)
                AddHard($"Неотмеченный простой {fresh:0}мин больше длительности смены — противоречие данных");

            // R1: причина обязательна при |доли простоя| свыше порога (зеркало VM).
            if ((ratio > DowntimeReasonRatioThreshold || ratio < -DowntimeReasonRatioThreshold)
                && string.IsNullOrWhiteSpace(reason))
                AddHard($"Неотмеченный простой {fresh:0}мин ({ratio:0%}) без причины из списка");

            // R2: «Другое» только с детализацией (регламент, таблица причин).
            if (OtherReason.Equals(reason, StringComparison.OrdinalIgnoreCase)
                && comment.Length < MinOtherCommentLength)
                AddHard("Причина «Другое» требует детализации — комментарий мастера её не даёт");

            // R4 (вариант A): снимок устарел — журнал правили после сохранения отчёта.
            if (Math.Abs(rep.StoredUnspecifiedDowntimes - fresh) > StaleEpsilonMinutes)
                AddHard($"Отчёт устарел: простой на момент сохранения {rep.StoredUnspecifiedDowntimes:0}мин, " +
                        $"сейчас {fresh:0}мин — мастеру пересохранить отчёт");

            // R5: смена целиком в простое подтверждается отсутствием записей.
            if (fresh >= rep.ShiftMinutes && rep.HasParts)
                AddHard("Смена целиком в простое, но в журнале есть записи — противоречие данных");

            // R6: причина обязана быть из закрытого справочника. Пустой справочник
            // (старый клиент) — судить не по чему, правило пропускается.
            if (!string.IsNullOrWhiteSpace(reason)
                && knownReasons.Count > 0
                && !knownReasons.Contains(reason))
                AddHard($"Причина «{reason}» вне справочника причин простоя");

            // R7: терминал не работал — достоверных данных за смену нет, день
            // закрывать нельзя (решение владельца 07.10.2026, кейс Victor 24.09:
            // «Другое» + «Не работал терминал»). Аналог «Некорректного
            // заполнения»: вопрос к данным, а не к производству. Словарь по
            // выгрузке cnc_shifts: полное отсутствие записи («не работал»,
            // «нет связи», «отсутствие», «не включался», «нет данных»,
            // «не подключен», «не видит»). Глюки («завис», «виснет», «плохо
            // работает») и запись в другом месте («отмечался», «отмечал
            // данные») — не срабатывают: данные есть.
            if (TerminalDownClaim(comment) && !TerminalRecordedElsewhere(comment))
                AddHard("Терминал не работал — достоверных данных за смену нет, день закрывать нельзя");

            // S2: частичная наладка свыше порога — рекомендательно, не блокирует.
            if (rep.PartialSetupRatio > PartialSetupAttentionRatio)
                soft.Add($"{tag} Частичная наладка {rep.PartialSetupRatio:0%} > 30% — " +
                         "рекомендуется отразить в комментарии мастера");
        }

        return new ShiftReportRuleResult([.. hard.Distinct()], [.. soft.Distinct()]);
    }

    /// <summary>
    /// Предрешение по смене для агентского контура: механику, которую модель
    /// стабильно нарушает (кейсы A/B и пилота 24.09.2026: «требует детализации»
    /// при простое целиком + причина, при простое 3% без причины), решает код
    /// и сообщает итог как факт — как HARD-сигналы, которые модель соблюдает
    /// идеально. Модели остаётся только genuine семантика (шаг 4 скелета v5):
    /// релевантность пары (причина + комментарий), где она реально требуется.
    /// Держать в синхроне с секцией ОТЧЁТ МАСТЕРА в system_prompt.agent.txt.
    /// </summary>
    public record ShiftPreverdict(string Shift, bool NeedsModel, string Note);

    /// <summary>
    /// Ключи смен, решать по которым нечего (превердикт Clear): их блоки отчётов
    /// не рендерятся в промпт — судить нечего и не по чему (кейсы Integrex
    /// 09/10.2026: модель флагала полный простой с причиной из списка вопреки
    /// строке СИСТЕМА). Нормализация та же, что у byShift-словарей ниже.
    /// </summary>
    public static HashSet<string> ClearShiftKeys(List<ShiftPreverdict> preverdicts) =>
        [.. preverdicts.Where(p => !p.NeedsModel).Select(p => p.Shift.Trim().ToLowerInvariant())];

    public static List<ShiftPreverdict> AgentPreverdicts(AnalyzeRequest req, ShiftReportRuleResult shiftRules)
    {
        var result = new List<ShiftPreverdict>();
        if (req.ShiftReports.Count == 0) return result;

        var knownReasons = req.DowntimeReasons
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rep in req.ShiftReports)
        {
            ShiftPreverdict Clear(string note) => new(rep.Shift, false, note);

            // Структурное нарушение уже в HARD — модели там делать нечего.
            if (shiftRules.HardSignals.Any(h => h.StartsWith($"[{rep.Shift}]", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(Clear("структурное нарушение — см. HARD выше"));
                continue;
            }

            if (!rep.ReportExists || rep.ShiftMinutes <= 0)
            {
                result.Add(Clear("отчёта нет — проверять нечего"));
                continue;
            }

            var reason = rep.DowntimeReason?.Trim() ?? string.Empty;
            var ratio = (double)rep.FreshUnspecifiedDowntimes / rep.ShiftMinutes;

            // Простой целиком + причина из списка + записей нет: такой причине
            // добавить нечего, пустой комментарий — норма (шаг 2 скелета).
            // Исключение — «Другое» без детализации: R2 требует комментарий всегда,
            // даже при простое целиком, поэтому судить модели, а не закрывать.
            if (rep.FreshUnspecifiedDowntimes >= rep.ShiftMinutes
                && !string.IsNullOrWhiteSpace(reason)
                && knownReasons.Contains(reason)
                && !rep.HasParts)
            {
                if (OtherReason.Equals(reason, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(rep.MasterComment))
                {
                    result.Add(new ShiftPreverdict(rep.Shift, true,
                        "«Другое» без детализации при простое целиком: комментарий обязателен всегда"));
                    continue;
                }
                result.Add(Clear("простой целиком + причина из списка, записей нет"));
                continue;
            }

            // Причина при |доле| ≤5% не требуется вообще — кроме «Другое»,
            // которому детализация нужна всегда (шаг 3 скелета).
            if (Math.Abs(ratio) <= DowntimeReasonRatioThreshold
                && !OtherReason.Equals(reason, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(Clear("простой в пределах 5% — причина не требуется"));
                continue;
            }

            result.Add(new ShiftPreverdict(rep.Shift, true,
                "оцени релевантность пары (причина + комментарий) простою этой смены"));
        }

        return result;
    }

    /// <summary>
    /// Чистит сырые shift_report_issues от модели: trim, пустые выкидываются,
    /// дубли схлопываются. Непустой итог (S1) эскалирует день — см. контроллер.
    /// </summary>
    public static List<string> CleanModelIssues(IEnumerable<string>? issues)
    {
        if (issues == null) return [];
        return [.. issues
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct()];
    }

    /// <summary>
    /// Вердикт «релевантно» — не проблема: модель одобрила отчёт (причина из списка
    /// называет что и почему), но положила одобрение в массив проблем (кейс QTS350
    /// 2026-09-24: «Смена День: Отсутствие оператора — релевантно…» в issues
    /// и дублем в signals). Такие записи режутся детерминированно — в issues им
    /// не место по определению. Отрицание («нерелевантно», «не релевантно») —
    /// настоящая жалоба, сохраняется.
    /// </summary>
    public static (List<string> Kept, List<string> Dropped) DropRelevanceVerdicts(
        List<string> modelIssues)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var issue in modelIssues)
        {
            var lower = issue.ToLowerInvariant();
            if (lower.Contains("релевант")
                && !lower.Contains("нерелевант")
                && !lower.Contains("не релевант"))
                dropped.Add(issue);
            else
                kept.Add(issue);
        }
        return (kept, dropped);
    }

    /// <summary>
    /// Жалоба на связь простоя с КПД («простой не объясняет КПД», «КПД не объяснён
    /// простоем» — кейс QTS350 2026-09-24 v14: «отсутствие оператора (72% простой)
    /// не объясняет КПД наладки >200%»). По механизму учёта простой и КПД не связаны
    /// никогда: простой — неотмеченное время, КПД — по отмеченному. Такая жалоба
    /// неверна по построению, независимо от чисел (там и 123% было названо >200%).
    /// Легитимные жалобы без связки (нет причины, комментарий не о простое,
    /// «нерелевантно») слова «кпд» рядом с простоем не содержат — сохраняются.
    /// </summary>
    public static (List<string> Kept, List<string> Dropped) DropKpdDowntimeLinkComplaints(
        List<string> modelIssues)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var issue in modelIssues)
        {
            var lower = issue.ToLowerInvariant();
            if (lower.Contains("кпд")
                && (lower.Contains("просто") || lower.Contains("смен"))
                && (lower.Contains("объясн") || lower.Contains("связан")
                    || lower.Contains("коррел") || lower.Contains("влия")))
                dropped.Add(issue);
            else
                kept.Add(issue);
        }
        return (kept, dropped);
    }

    /// <summary>
    /// Модель регулярно кладёт проблемы суточного отчёта в общий signals
    /// (другими словами, чем в shift_report_issues) — тогда они дублируются
    /// в блоке «Данные» и ломают раскладку «каждому блоку своё». Такие эхо
    /// детерминированно перекладываются в shift-вопросы: в signals им не место.
    /// Эвристика узкая (требует связки с мастером/отчётом либо дневного простоя
    /// с требованием причины — материала R1-порога), построчные сигналы
    /// про смены без мастера не задевает.
    /// </summary>
    public static (List<string> DataSignals, List<string> ShiftEchoes) SplitShiftEchoes(
        IEnumerable<string>? signals)
    {
        var data = new List<string>();
        var echoes = new List<string>();
        if (signals == null) return (data, echoes);

        foreach (var s in signals)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            (IsShiftReportEcho(s) || IsDayDowntimeReasonDemand(s) ? echoes : data).Add(s.Trim());
        }
        return (data, echoes);
    }

    /// <summary>
    /// Требование причины дневного простоя («Дневной простой 22 мин (3% смены)
    /// без указания причины», кейс 21.09.2026) — вопрос суточного отчёта (R1,
    /// порог 5%), а не данных: судьбу решает DropUnneededReasonDemands
    /// по свежему пересчёту смены, в блоке «Данные» ему не место.
    /// </summary>
    private static bool IsDayDowntimeReasonDemand(string signal)
    {
        var lower = signal.ToLowerInvariant();
        if (!(lower.StartsWith("дневн") || lower.StartsWith("ночн"))) return false;
        if (!lower.Contains("просто") || !lower.Contains("причин")) return false;
        return lower.Contains("без ") || lower.Contains("не указан") || lower.Contains("неуказан")
            || lower.Contains("отсутств") || lower.Contains("нет ");
    }

    private static bool IsShiftReportEcho(string signal)
    {
        var lower = signal.ToLowerInvariant();
        // Префикс «Смена День/Ночь:» — формат shift_report_issues по OUTPUT-контракту:
        // в общем signals такой строке не место, чья бы она ни была (кейс QTS350
        // 2026-09-24 — модель продублировала вопрос из shift_report_issues в signals).
        if (lower.StartsWith("смена день") || lower.StartsWith("смена ночь")
            || lower.StartsWith("смена день/ночь"))
            return true;
        // «Дневной простой…» / «Ночная смена: …» — та же смена-тематика без слова
        // «мастер» (кейс Hyundai L230A 20.06.2026: оба вопроса про отчёт лежали
        // в signals и не доходили до shift-дропов). «Дневной КПД…» без слов
        // смен/просто/отч — данные, не задевается. «Смена инструмента…» —
        // не смена суток, не задевается (требуется designator день/ночь).
        if ((lower.StartsWith("дневн") || lower.StartsWith("ночн"))
            && (lower.Contains("смен") || lower.Contains("просто") || lower.Contains("отч")))
            return true;
        // Прямые маркеры отчёта/мастера/смены (включая косвенные падежи:
        // «некорректного отчёта мастера», кейс пилота 24.09.2026 SKT21 22.09)...
        if (lower.Contains("отчёт мастера") || lower.Contains("отчет мастера")
            || lower.Contains("отчёта мастера") || lower.Contains("отчета мастера")
            || (lower.Contains("смен") && lower.Contains("мастер"))
            || (lower.Contains("суточн") && lower.Contains("отч")))
            return true;
        // ...и косвенные: модель пишет про отчёт без слова «мастер»
        // («Отчёт устарел: простой увеличился…»). Построчные сигналы слова
        // «отчёт» не содержат (exclude-подсказки живут в отдельном поле).
        return (lower.Contains("отчёт") || lower.Contains("отчет"))
            && (lower.Contains("смен") || lower.Contains("суточн") || lower.Contains("прост"));
    }

    /// <summary>
    /// Режет требования комментария, когда причина простоя самодостаточна.
    /// На уровне суточного отчёта комментарий обязателен только при «Другое»
    /// (см. R2 и валидацию DailyReportWindow: мастер и причина; комментарий —
    /// опционален). Модель стабильно требует комментарий и при причине из списка
    /// («Отсутствие оператора» при простое целиком), вняв запрету в промпте дважды
    /// проигнорировала — поэтому правилом, а не текстом. Режутся жалобы
    /// на отсутствие комментария и претензии к релевантности («без релевантного
    /// комментария», кейс 22.09.2026 Mazak) — но последние только при ПУСТОМ
    /// комментарии: релевантным может быть лишь имеющийся текст, а пустому
    /// при самодостаточной причине добавить нечего. Претензии к содержанию
    /// имеющегося комментария («комментарий не о простое») и вопросы без
    /// привязки к смене сохраняются.
    /// </summary>
    public static (List<string> Kept, List<string> Dropped) DropSelfSufficientCommentDemands(
        AnalyzeRequest req, List<string> modelIssues)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        if (modelIssues.Count == 0) return (kept, dropped);

        var byShift = req.ShiftReports
            .Where(r => r.ReportExists && r.ShiftMinutes > 0)
            .ToDictionary(r => r.Shift.Trim().ToLowerInvariant(), r => r);

        foreach (var issue in modelIssues)
        {
            // «Отсутствие оператора» самодостаточно только при простое целиком: при
            // частичном свыше порога без комментария требование комментария легитимно —
            // не резать (решение владельца 19.10.2026, зеркало валидации DailyReportWindow).
            if (ShiftTag(issue) is { } absenceTag
                && byShift.TryGetValue(absenceTag, out var absenceRep)
                && RequiresAbsenceComment(absenceRep))
            {
                kept.Add(issue);
                continue;
            }

            if (ShiftTag(issue) is { } tag
                && byShift.TryGetValue(tag, out var rep)
                && IsSelfSufficientReason(rep.DowntimeReason)
                && (IsCommentDemand(issue)
                    || IsConfirmationDemand(issue)
                    || (IsCommentRelevanceComplaint(issue)
                        && string.IsNullOrWhiteSpace(rep.MasterComment))))
            {
                dropped.Add(issue);
                continue;
            }
            kept.Add(issue);
        }
        return (kept, dropped);
    }

    /// <summary>
    /// Комментарий обязателен, но пуст: причина «Отсутствие оператора» при частичном
    /// простое свыше порога. Полный простой и остальные причины — как раньше
    /// (причина самодостаточна, требования модели режутся).
    /// </summary>
    private static bool RequiresAbsenceComment(ShiftReportContext rep) =>
        string.Equals(rep.DowntimeReason?.Trim(), "Отсутствие оператора", StringComparison.OrdinalIgnoreCase)
        && rep.FreshUnspecifiedDowntimes > 0
        && rep.FreshUnspecifiedDowntimes < rep.ShiftMinutes
        && Math.Abs(rep.FreshUnspecifiedDowntimes / rep.ShiftMinutes) > DowntimeReasonRatioThreshold
        && string.IsNullOrWhiteSpace(rep.MasterComment);

    /// <summary>
    /// Заявление о полном отсутствии терминальной записи за смену: терминал
    /// не работал / связи не было / терминала не было / не включался /
    /// данных нет. Требуется слово «терминал»/«1с»/«журнал» рядом — иначе
    /// «не работал» про станок даст ложное срабатывание.
    /// </summary>
    private static bool TerminalDownClaim(string? comment)
    {
        var lower = (comment ?? "").ToLowerInvariant();
        if (!lower.Contains("терминал") && !lower.Contains("1с") && !lower.Contains("журнал"))
            return false;
        return lower.Contains("не работал") || lower.Contains("не работает")
            || lower.Contains("неработает")
            || lower.Contains("нет связи") || lower.Contains("отсутствие")
            || lower.Contains("не включался") || lower.Contains("нет данных")
            || lower.Contains("не подключен") || lower.Contains("не видит")
            || lower.Contains("не видет");
    }

    /// <summary>
    /// Запись всё же велась — в другом месте: оператор отмечался на другом
    /// терминале/станке. Данные есть, R7 не применяется.
    /// </summary>
    private static bool TerminalRecordedElsewhere(string? comment)
    {
        var lower = (comment ?? "").ToLowerInvariant();
        return lower.Contains("отмечался") || lower.Contains("отмечал данные")
            || lower.Contains("отмечал на");
    }

    /// <summary> Причина, которой по регламенту добавить нечего (всё, кроме «Другое»). </summary>
    private static bool IsSelfSufficientReason(string? reason)
    {
        var r = reason?.Trim() ?? "";
        return r.Length > 0 && !OtherReason.Equals(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary> Жалоба именно на отсутствие комментария (а не на его содержание). </summary>
    private static bool IsCommentDemand(string issue)
    {
        var lower = issue.ToLowerInvariant();
        if (!lower.Contains("комментар")) return false;
        return lower.Contains("отсутств")
            || lower.Contains("нет комментар")
            || lower.Contains("без комментар")
            || lower.Contains("требу")
            || lower.Contains("нуж")
            || lower.Contains("не указан")
            || lower.Contains("пуст")
            || lower.Contains("добавить");
    }

    /// <summary>
    /// Требование подтвердить/уточнить самодостаточную причину («без подтверждения
    /// планового ТО» — кейс Hyundai L230A 20.06.2026): та же жалоба «сверх причины
    /// из списка», только без слова «комментарий». Осторожно: «подтверждено
    /// историей» про освоение — не сюда (там нет «подтвержд» + простой/смена,
    /// эта проверка применяется только к shift-вопросам с тегом смены).
    /// </summary>
    private static bool IsConfirmationDemand(string issue)
    {
        var lower = issue.ToLowerInvariant();
        if (lower.Contains("истори")) return false;
        return lower.Contains("подтвержд") || lower.Contains("подтверж");
    }

    /// <summary>
    /// Претензия к релевантности комментария («без релевантного/детального
    /// комментария»). От жалобы на отсутствие отличается тем, что при НЕПУСТОМ
    /// комментарии это genuine S1-вопрос — вызывающий код режет её, только если
    /// комментировать нечего (комментарий пуст, а причина самодостаточна).
    /// </summary>
    private static bool IsCommentRelevanceComplaint(string issue)
    {
        var lower = issue.ToLowerInvariant();
        if (!lower.Contains("комментар")) return false;
        return lower.Contains("релевант")
            || lower.Contains("детальн")
            || lower.Contains("не о прост")
            || lower.Contains("содержани");
    }

    /// <summary>
    /// Режет требования причины простоя, когда она не требуется: доля простоя
    /// в пределах ±5% смены (зеркало R1 и валидации DailyReportWindow) либо
    /// причина уже указана, а модель галлюцинирует её отсутствие. Релевантность
    /// имеющейся причины («нерелевантна», «не о простое») — другая претензия,
    /// сохраняется. Без привязки к смене — консервативно сохраняется.
    /// </summary>
    public static (List<string> Kept, List<string> Dropped) DropUnneededReasonDemands(
        AnalyzeRequest req, List<string> modelIssues)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        if (modelIssues.Count == 0) return (kept, dropped);

        var byShift = req.ShiftReports
            .Where(r => r.ReportExists && r.ShiftMinutes > 0)
            .ToDictionary(r => r.Shift.Trim().ToLowerInvariant(), r => r);

        foreach (var issue in modelIssues)
        {
            if (IsReasonDemand(issue)
                && ShiftTag(issue) is { } tag
                && byShift.TryGetValue(tag, out var rep))
            {
                var ratio = rep.FreshUnspecifiedDowntimes / rep.ShiftMinutes;
                var belowThreshold = ratio is <= DowntimeReasonRatioThreshold
                    and >= -DowntimeReasonRatioThreshold;
                if (belowThreshold || !string.IsNullOrWhiteSpace(rep.DowntimeReason))
                {
                    dropped.Add(issue);
                    continue;
                }
            }
            kept.Add(issue);
        }
        return (kept, dropped);
    }

    /// <summary> Жалоба именно на отсутствие причины (а не на её содержание). </summary>
    private static bool IsReasonDemand(string issue)
    {
        var lower = issue.ToLowerInvariant();
        if (!lower.Contains("причин")) return false;
        return lower.Contains("не указан")
            || lower.Contains("неуказан") // «причина неуказана» слитно
            || lower.Contains("без указан") // «без указания причины» (кейс 21.09.2026)
            || lower.Contains("отсутств")
            || lower.Contains("без причин")
            || lower.Contains("нет причин")
            || lower.Contains("требу")
            || lower.Contains("нуж")
            || lower.Contains("пуст");
    }

    private static readonly string[] UnknownReasonMarkers =
    [
        "не входит в список",
        "неизвестн",
        "нет в списке",
        "вне списка",
        "не из списка",
        "отсутствует в списке",
    ];

    /// <summary>
    /// Режет фактически неверные жалобы «причина неизвестна/не из списка», когда
    /// названная причина ЕСТЬ в закрытом списке (req.DowntimeReasons) или выбрана
    /// в отчёте какой-либо смены. Модель приходит к такому выводу, спросив
    /// get_reason_semantics про причину ПРОСТОЯ (её нет в каталоге отклонений —
    /// и быть не должно, см. AgentTools); код сверяет факт напрямую.
    /// Пилот 24.09.2026, SKT21 22.09: «Отсутствие оператора» объявлено неизвестным.
    /// Безымянная жалоба («причина неизвестна» без названия) — сохраняется:
    /// сверить не с чем.
    /// </summary>
    public static (List<string> Kept, List<string> Dropped) DropUnknownReasonComplaints(
        AnalyzeRequest req, List<string> modelIssues)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        if (modelIssues.Count == 0) return (kept, dropped);

        var known = req.DowntimeReasons
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rep in req.ShiftReports)
        {
            if (!string.IsNullOrWhiteSpace(rep.DowntimeReason))
                known.Add(rep.DowntimeReason.Trim());
        }
        if (known.Count == 0)
        {
            kept.AddRange(modelIssues);
            return (kept, dropped);
        }

        foreach (var issue in modelIssues)
        {
            var lower = issue.ToLowerInvariant();
            if (lower.Contains("причин")
                && UnknownReasonMarkers.Any(m => lower.Contains(m))
                && known.Any(k => k.Length > 0 && lower.Contains(k.ToLowerInvariant())))
            {
                dropped.Add(issue);
                continue;
            }
            kept.Add(issue);
        }
        return (kept, dropped);
    }

    private static readonly string[] DualShiftMarkers =
    [
        "день/ночь",
        "ночь/день",
        "день и ночь",
        "ночь и день",
        "обе смены",
        "обеих сменах",
        "обоих сменах",
    ];

    /// <summary>
    /// Режет вопросы без привязки к смене: модель копирует шаблон формата
    /// «Смена День/Ночь:» буквально, не выбрав смену (пилот 24.09.2026, SKT21 22.09:
    /// «Смена День/Ночь: простой объясняет только простои…» — пересказ граничного
    /// правила как находка). Такую жалобу нельзя отнести ни к одной смене —
    /// аналитику с ней делать нечего. Одиночные теги не задеваются; вопросы
    /// вообще без смены, но с конкретикой, сохраняются как раньше.
    /// </summary>
    public static (List<string> Kept, List<string> Dropped) DropUnscopedShiftIssues(
        List<string> modelIssues)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var issue in modelIssues)
        {
            var lower = issue.ToLowerInvariant();
            if (ShiftTag(issue) == null && DualShiftMarkers.Any(m => lower.Contains(m)))
            {
                dropped.Add(issue);
                continue;
            }
            kept.Add(issue);
        }
        return (kept, dropped);
    }

    /// <summary>
    /// Режет выдумки про мастера, который в отчёте указан: «мастер не указан»
    /// при непустом поле мастер, и «оператор &lt;ФИО мастера&gt;» — модель берёт
    /// имя из отчёта смены и ищет его в cnc_operators, где мастеров нет
    /// по построению (кейс 06.10.2026, Rontek HTC420 №1 28.09: «Оператор Зайцев
    /// Максим Борисович отсутствует в системе» + «мастер не указан в системе»
    /// при мастере «Зайцев Максим Борисович» в обеих сменах). Содержательные
    /// претензии («комментарий не о простое», «причина не релевантна») не
    /// задеваются — только ложь про наличие/роль мастера.
    /// </summary>
    public static (List<string> Kept, List<string> Dropped) DropFalseMasterClaims(
        AnalyzeRequest req, List<string> modelIssues)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        if (modelIssues.Count == 0) return (kept, dropped);

        var masters = req.ShiftReports
            .Where(r => r.ReportExists && r.ShiftMinutes > 0 && !string.IsNullOrWhiteSpace(r.Master))
            .ToDictionary(r => r.Shift.Trim().ToLowerInvariant(), r => r.Master.Trim());
        var existingShifts = req.ShiftReports
            .Where(r => r.ReportExists && r.ShiftMinutes > 0)
            .Select(r => r.Shift.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (masters.Count == 0)
        {
            kept.AddRange(modelIssues);
            return (kept, dropped);
        }

        foreach (var issue in modelIssues)
        {
            var tag = ShiftTag(issue);
            if (tag != null && masters.TryGetValue(tag, out var master)
                && (ClaimsMasterUnspecified(issue) || LabelsMasterAsOperator(issue, master)))
            {
                dropped.Add(issue);
                continue;
            }
            // Без тега смены — только когда мастер указан ВО ВСЕХ существующих
            // отчётах: иначе жалоба может относиться к смене без мастера.
            if (tag == null && masters.Count == existingShifts.Count
                && ClaimsMasterUnspecified(issue))
            {
                dropped.Add(issue);
                continue;
            }
            kept.Add(issue);
        }
        return (kept, dropped);
    }

    internal static bool ClaimsMasterUnspecified(string issue)
    {
        var lower = issue.ToLowerInvariant();
        if (!lower.Contains("мастер")) return false;
        return lower.Contains("не указан") || lower.Contains("неуказан")
            || lower.Contains("отсутств") || lower.Contains("нет мастера")
            || lower.Contains("без мастера") || lower.Contains("не найден");
    }

    /// <summary>
    /// Сигнал/вопрос называет мастера смены из отчёта: полное ФИО либо
    /// фамилия + инициал («оператор Зайцев М.» при мастере «Зайцев М.Б.»).
    /// Голая фамилия без инициалов — не match (возможен однофамилец).
    /// </summary>
    internal static bool NamesShiftMaster(string text, string master)
    {
        var lower = (text ?? "").ToLowerInvariant().Replace('ё', 'е');
        var m = (master ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
        if (m.Length == 0) return false;
        if (lower.Contains(m)) return true;
        var surname = m.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (surname.Length < 3 || !lower.Contains(surname)) return false;
        var tokens = System.Text.RegularExpressions.Regex.Split(lower, @"[^a-zа-я]+")
            .Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        return m.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Where(t => t.Length > 0).Select(t => t[..1])
            .Any(i => tokens.Contains(i));
    }

    private static bool LabelsMasterAsOperator(string issue, string master)
    {
        if (!issue.ToLowerInvariant().Contains("оператор")) return false;
        return NamesShiftMaster(issue, master);
    }

    /// <summary>
    /// Режет придирки к комментарию про другой станок при причине
    /// «Отсутствие оператора»/«Другое»: владелец подтвердил, что работа,
    /// связанная с другим станком, — релевантная причина простоя (кейсы
    /// Rontek HTC420 №1 06–10.09: модель то молчала, то писала
    /// «без детализации»/«не о простое» про один и тот же текст).
    /// Покрывает ОБА класса (решение владельца 07.10.2026): совмещение
    /// («на двух станках», «двустаночная») и перевод на другое рабочее место
    /// («перешёл на др. станок», «помощь на других станках») — отсутствие
    /// объясняют оба; различие «совмещение vs перевод» важно только для
    /// КПД/машвремени (см. каталог и промпт), а не для простоя.
    /// Снимаются только претензии к детализации/содержанию комментария;
    /// требования причины и прочие вопросы сохраняются.
    /// </summary>
    public static (List<string> Kept, List<string> Dropped) DropTwoMachineRelevanceComplaints(
        AnalyzeRequest req, List<string> modelIssues)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        if (modelIssues.Count == 0) return (kept, dropped);

        var byShift = req.ShiftReports
            .Where(r => r.ReportExists && r.ShiftMinutes > 0)
            .ToDictionary(r => r.Shift.Trim().ToLowerInvariant(), r => r);

        foreach (var issue in modelIssues)
        {
            if (ShiftTag(issue) is { } tag
                && byShift.TryGetValue(tag, out var rep)
                && IsAbsenceOrOther(rep.DowntimeReason)
                && MentionsTwoMachineWork(rep.MasterComment)
                && IsCommentDetailComplaint(issue))
            {
                dropped.Add(issue);
                continue;
            }
            kept.Add(issue);
        }
        return (kept, dropped);
    }

    private static bool IsAbsenceOrOther(string? reason) =>
        string.Equals(reason?.Trim(), "Отсутствие оператора", StringComparison.OrdinalIgnoreCase)
        || string.Equals(reason?.Trim(), "Другое", StringComparison.OrdinalIgnoreCase);

    private static bool MentionsTwoMachineWork(string? comment)
    {
        // Словарь по реальным комментариям мастеров (выгрузка cnc_shifts
        // 06.10.2026): «Двустаночное/Двухстаночное обслуживание», «двустаночная
        // работа», «работа на двух станках», «перешёл на др станок»,
        // «с др станка», «помощь на других станках», «оператора сняли
        // с др станка» (кейс HTC650M 08.09: сокращение «др» матчер не ловил).
        // «многостаночн» в данных пока нет — добавлен по решению владельца.
        var lower = (comment ?? "").ToLowerInvariant();
        return lower.Contains("двустаночн") || lower.Contains("двухстаночн")
            || lower.Contains("многостаночн")
            || lower.Contains("двух станках") || lower.Contains("два станка")
            || lower.Contains("2 станках")
            || lower.Contains("др стан")
            || lower.Contains("другой станок") || lower.Contains("другого станка")
            || lower.Contains("другом станке") || lower.Contains("других станках");
    }

    private static bool IsCommentDetailComplaint(string issue)
    {
        var lower = issue.ToLowerInvariant();
        return lower.Contains("детализ") || lower.Contains("подробн")
            || lower.Contains("не о простое") || lower.Contains("нерелевант")
            || lower.Contains("не релевант")
            || (lower.Contains("комментар") && lower.Contains("релевант"));
    }

    /// <summary>
    /// Режет выдумки модели про отчёт, которого нет: «устарел», «мастер
    /// не указан», претензии к релевантности — когда проверять нечего.
    /// Триггер тот же, что рисует иконки наличия отчёта на главной
    /// (ReportExists из чтения cnc_shifts): два случая — блоков «Отчёт мастера»
    /// в запросе нет вообще (тогда любой shift-вопрос модели ни на что
    /// не опирается) и смена с признаком «отчёта нет» (тогда по этой смене
    /// режется всё, включая пересказ отсутствия своими словами: детерминированный
    /// hard «Нет суточного отчёта…» по такой смене есть всегда, а нечёткий дедуп
    /// в MergeIssues ловит пересказы на грани и может пропустить).
    /// Без привязки к смене — консервативно сохраняется.
    /// </summary>
    public static (List<string> Kept, List<string> Dropped) DropIssuesWithoutReport(
        AnalyzeRequest req, List<string> modelIssues)
    {
        var kept = new List<string>();
        var dropped = new List<string>();
        if (modelIssues.Count == 0) return (kept, dropped);

        // Блоков отчёта в запросе нет — модели нечего проверять.
        if (req.ShiftReports.Count == 0)
        {
            dropped.AddRange(modelIssues);
            return (kept, dropped);
        }

        // Условие зеркалит PromptBuilder.AppendShiftReports («отчёта нет»).
        var missingShifts = req.ShiftReports
            .Where(r => !r.ReportExists || r.ShiftMinutes <= 0)
            .Select(r => r.Shift.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (missingShifts.Count == 0)
        {
            kept.AddRange(modelIssues);
            return (kept, dropped);
        }

        foreach (var issue in modelIssues)
        {
            if (ShiftTag(issue) is { } tag && missingShifts.Contains(tag))
            {
                dropped.Add(issue);
                continue;
            }
            kept.Add(issue);
        }
        return (kept, dropped);
    }

    /// <summary>
    /// Детерминированная замена объяснения, когда эскалация вызвана ТОЛЬКО
    /// суточным отчётом: модель в этом случае регулярно перечисляет проблемы
    /// отчёта в explanation вопреки требованию краткости (v16+), а по записям
    /// сказать нечего — день чист. Возвращает null, если замена не нужна.
    /// </summary>
    public static string? ShiftOnlyExplanation(bool dataCaused, bool shiftCaused) =>
        (!dataCaused && shiftCaused)
            ? "Явных отклонений в записях не обнаружено. Есть вопросы к суточному отчёту мастера."
            : null;

    /// <summary>
    /// Итоговый список вопросов к отчёту мастера для ответа: детерминированные
    /// hard + soft, затем несогласия модели (S1). Порядок стабилен для UI-блока.
    /// Модельные формулировки, повторяющие детерминированный hard другими словами
    /// («[Ночь] Нет суточного отчёта» vs «Смена Ночь: отсутствие суточного отчёта»),
    /// выкидываются нечётким сравнением — детерминированный текст авторитетнее.
    /// </summary>
    public static List<string> MergeIssues(ShiftReportRuleResult det, IEnumerable<string>? modelIssues)
    {
        var merged = new List<string>(det.HardSignals);
        merged.AddRange(det.SoftSignals);
        var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
        foreach (var m in CleanModelIssues(modelIssues))
        {
            if (seen.Contains(m) || IsCoveredByDet(m, det.HardSignals)) continue;
            merged.Add(m);
            seen.Add(m);
        }
        return merged;
    }

    /// <summary>
    /// Модельный вопрос покрыт детерминированным hard той же смены, если делит
    /// с ним не меньше половины значащих слов (минимум 2). Сравнение по нормали:
    /// регистр/ё/пунктуация сняты, префиксы смен не участвуют (смена сверена отдельно).
    /// Консервативно: непохожие и короткие формулировки всегда сохраняются.
    /// </summary>
    private static bool IsCoveredByDet(string modelIssue, List<string> detHards)
    {
        var mShift = ShiftTag(modelIssue);
        var mWords = SignificantWords(modelIssue);
        if (mWords.Count == 0) return false;
        var need = Math.Max(2, mWords.Count / 2);

        foreach (var h in detHards)
        {
            var hShift = ShiftTag(h);
            if (mShift != null && hShift != null && mShift != hShift) continue;
            var hWords = SignificantWords(h).ToHashSet(StringComparer.Ordinal);
            if (mWords.Count(w => hWords.Contains(w)) >= need) return true;
        }
        return false;
    }

    /// <summary> Смена из префикса: «[День] …», «Смена День: …» или вольности модели
    /// («Ночная смена: …», «Дневной простой …» — прилагательное тоже привязывает
    /// вопрос к смене; «…в ночную смену…» внутри строки — запасной вариант,
    /// кейс 22.09.2026 Mazak). Иначе null. </summary>
    private static string? ShiftTag(string signal)
    {
        var lower = signal.Trim().ToLowerInvariant();
        foreach (var (names, tag) in new[]
                 {
                     (new[] { "день", "дня" }, "день"),
                     (new[] { "ночь", "ночи" }, "ночь"),
                 })
        {
            if (names.Any(n => lower.StartsWith($"[{n}]") || lower.StartsWith($"смена {n}:")))
                return tag;
        }
        if (lower.StartsWith("дневн")) return "день";
        if (lower.StartsWith("ночн")) return "ночь";
        if (lower.Contains("дневную смену") || lower.Contains("дневной смене")
            || lower.Contains("за дневную смену") || lower.Contains("в дневную смену")) return "день";
        if (lower.Contains("ночную смену") || lower.Contains("ночной смене")
            || lower.Contains("за ночную смену") || lower.Contains("в ночную смену")) return "ночь";
        return null;
    }

    private static List<string> SignificantWords(string text) =>
        System.Text.RegularExpressions.Regex.Split(
                text.ToLowerInvariant().Replace('ё', 'е'), @"[^a-zа-я0-9]+")
            .Where(w => w.Length >= 4)
            .Distinct()
            .ToList();

    /// <summary>
    /// Построчная сводка проверки для UI-блока «Отчёт мастера»: по каждой смене —
    /// факты (простой, причина) и детерминированный вердикт. Показывается всегда,
    /// даже когда вопросов нет: видно, что отчёт проверен. Семантический вердикт
    /// модели (S1) живёт отдельно — в <see cref="MergeIssues"/>.
    /// </summary>
    public static List<string> Summarize(AnalyzeRequest req, ShiftReportRuleResult det)
    {
        var lines = new List<string>();
        if (req.ShiftReports.Count == 0) return lines;

        foreach (var rep in req.ShiftReports)
        {
            var tag = $"[{rep.Shift}]";
            if (!rep.ReportExists || rep.ShiftMinutes <= 0)
            {
                lines.Add($"{tag} Нет отчёта мастера");
                continue;
            }

            var ratio = rep.FreshUnspecifiedDowntimes / rep.ShiftMinutes;
            var idle = rep.FreshUnspecifiedDowntimes >= rep.ShiftMinutes ? ", простой целиком" : "";
            var reason = string.IsNullOrWhiteSpace(rep.DowntimeReason)
                ? "причина не указана"
                : $"причина «{rep.DowntimeReason.Trim()}»";
            var verdict = det.HardSignals.Any(s => s.StartsWith(tag)) ? " — вопрос ниже" : " — ок";
            lines.Add($"{tag} Простой {rep.FreshUnspecifiedDowntimes:0} мин ({ratio:0%}){idle}, {reason}{verdict}");
        }

        return lines;
    }

    /// <summary>
    /// Выжившие после слияния модельные вопросы (без детерминированных hard/soft):
    /// именно они попадают в общий Signals. Срезанные fuzzy-дедупом дубли из Signals
    /// тоже исчезают — иначе диалогу нечем их отфильтровать (Except по ShiftReportIssues
    /// их не находит) и они висят в «Данных».
    /// </summary>
    public static List<string> ModelSurvivors(ShiftReportRuleResult det, List<string> merged)
    {
        var detSet = det.HardSignals.Concat(det.SoftSignals)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. merged.Where(s => !detSet.Contains(s))];
    }

    /// <summary>
    /// Вердикт модели считается причиной по данным, только если она назвала проблемы
    /// по данным: сигналы — уже почищенные (эхо вынуты, галлюцинации срезаны).
    /// Голый requires_review без сигналов (все претензии — к отчёту) заголовок
    /// «Данных» не зажигает. Формула самой эскалации не меняется — только атрибуция.
    /// </summary>
    public static bool LlmDataCaused(bool requiresReview, IReadOnlyList<string> cleanedSignals) =>
        requiresReview && cleanedSignals.Count > 0;

    /// <summary>
    /// Кто стал причиной эскалации — данные записей или суточный отчёт.
    /// S2-soft в ShiftReportIssues эскалацию не форсирует, поэтому жёлтая подсветка
    /// UI-блоков идёт по этим флагам, а не по непустоте массивов. resetEffective=true
    /// (пост-фильтр всё снял) гасит обе причины. Вердикт модели входит как
    /// signals-backed (см. <see cref="LlmDataCaused"/>), а не голый.
    /// </summary>
    public static (bool Data, bool ShiftReport) EscalationCauses(
        HardRuleResult hardRules, List<string> notDowngraded,
        bool llmDataCaused, bool llmHasError, bool resetEffective,
        ShiftReportRuleResult shiftRules, List<string> modelShiftIssues)
    {
        if (resetEffective) return (false, false);
        var data = hardRules.MustEscalate || notDowngraded.Count > 0
            || llmDataCaused || llmHasError;
        var shift = shiftRules.MustEscalate || modelShiftIssues.Count > 0;
        return (data, shift);
    }

    /// <summary>
    /// Итоговая уверенность вердикта. При детерминированной эскалации (hard
    /// по данным или по отчёту мастера) и при сбое парсинга (HasError) решение
    /// принимает код, а не модель — уверенность 1.0, как в DegenerateEscalation:
    /// система уверена, что человеку надо посмотреть. Иначе — самооценка модели
    /// как пришла (soft notDowngraded и модельные S1-вопросы её не трогают).
    /// </summary>
    public static double ComputeConfidence(
        bool hardMustEscalate, bool shiftMustEscalate, bool hasError, double llmConfidence) =>
        (hardMustEscalate || shiftMustEscalate || hasError) ? 1.0 : llmConfidence;
}
