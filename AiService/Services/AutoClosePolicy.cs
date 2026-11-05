using System.Text.RegularExpressions;
using AiService.Models;

namespace AiService.Services;

/// <summary>
/// Можно ли закрыть сутки без участия человека (этап «Полноценный запуск»).
/// Закрытие допустимо, только если вердикт «без эскалации» получен без признаков
/// ненадёжности. Любое сомнение — сутки остаются человеку: лишний разбор дёшев,
/// тихий пропуск аномалии — нет. Причины отказа возвращаются в ответе, чтобы
/// аналитик видел, почему сутки не закрыты автоматически.
/// </summary>
public static class AutoClosePolicy
{
    public const string ResetOverrodeModel =
        "пост-фильтр отменил эскалацию модели (модель просила разбор, фильтры сняли все основания)";
    public const string AgentFellBack =
        "агентный контур не завершился (сбой, таймаут или лимит итераций) — вердикт получен запасным путём";
    public const string ModelError =
        "ошибка разбора ответа модели";
    public const string AgentUnavailable =
        "агентный режим запрошен, но выключен на сервере — вердикт получен штатным путём без агента";

    public const string ContextOverflow =
        "контекст модели исчерпан (промпт и ответ упёрлись в лимит) — правила могли быть обрезаны";
    public const string ProcessDeviationAdmitted =
        "в комментариях есть явное признание работы не по техпроцессу — решает человек";

    /// <summary>
    /// «Не по тех.проц.», «не по техпроцессу», «не по технологии», «отступление/отклонение от
    /// техпроцесса». Это только запрет автозакрытия (ложное срабатывание отправляет сутки человеку,
    /// ничего не скрывая), а не фильтр эскалации. Кейс QTS350 23.10: оператор писал «работа не по
    /// тех.проц.», qwen закрыл сутки с уверенностью 0,95, второй проход (gemma4) нашёл.
    /// </summary>
    private static readonly Regex ProcessDeviationRx = new(
        @"не\s+по\s+(?:тех\.?\s*проц\w*|технолог\w*)|(?:отступлен\w*|отклонен\w*)\s+от\s+(?:тех\.?\s*проц\w*|технолог\w*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary> Тексты комментариев оператора и мастера по деталям дня и отчётам смен. </summary>
    public static bool MentionsProcessDeviation(AnalyzeRequest request)
    {
        foreach (var p in request.Parts)
        {
            if (Matches(p.OperatorComment) || Matches(p.MasterComment)
                || Matches(p.MasterSetupDetail) || Matches(p.MasterMachiningDetail)
                || Matches(p.MasterSetupComment) || Matches(p.MasterMachiningComment))
                return true;
        }
        return request.ShiftReports.Any(r => Matches(r.MasterComment));

        static bool Matches(string? text) => !string.IsNullOrWhiteSpace(text) && ProcessDeviationRx.IsMatch(text);
    }

    public const string UnexcludedLowKpdPrefix =
        "КПД ниже 100% объяснён освоением, ремонтом или «Другим», но исключение из К1 не оформлено — решает человек";

    private static readonly string[] LowKpdSetupReasons = ["Освоение", "Другое", "Доработка"];
    private static readonly string[] LowKpdMachiningReasons = ["Другое", "Доработка"];

    /// <summary> Оператор называет причиной освоение или ремонт/поломку оборудования. </summary>
    private static readonly Regex OperatorExcusableRx = new(
        @"освоени|ремонт|поломк|неисправн|авари",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Строки с КПД ниже 100%, у которых есть объяснение (причина мастера «Освоение»/«Другое»/«Доработка»
    /// либо слова оператора об освоении или ремонте), но исключение не оформлено: низкий КПД пойдёт
    /// в К1 оператора, хотя вина оператора не очевидна. Модель «ок» ставит, а решить исключать или нет
    /// должен человек (кейс Mazak QTS200ML 26.10: КПД наладки 34%, оператор «освоение», мастер
    /// «Другое: ремонт станка»). Строки из <paramref name="autoExcludeEntries"/> уже исключаются.
    /// </summary>
    public static List<string> UnexcludedExplainedLowKpd(
        AnalyzeRequest request, IEnumerable<string>? autoExcludeEntries)
    {
        var excluded = new HashSet<string>(
            (autoExcludeEntries ?? []).Select(e => string.Join('§', (e ?? "").Split('§').Take(3))));
        var found = new List<string>();

        foreach (var p in request.Parts)
        {
            var key = $"{p.PartName}§{p.Setup}§{p.Order}";
            if (excluded.Contains(key)) continue;

            var operatorExcuses = !string.IsNullOrWhiteSpace(p.OperatorComment)
                && OperatorExcusableRx.IsMatch(p.OperatorComment);

            var setupLow = p.SetupRatio is < 1.0 && !p.NoSetupHappened
                && (LowKpdSetupReasons.Contains((p.MasterSetupComment ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                    || operatorExcuses);
            var machLow = p.ProductionRatio is < 1.0 && !p.NoProductionHappened
                && (LowKpdMachiningReasons.Contains((p.MasterMachiningComment ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                    || operatorExcuses);

            if (setupLow || machLow)
                found.Add($"{p.PartName} (уст {p.Setup}, {(setupLow ? "наладка" : "изготовление")})");
        }
        return found;
    }

    public static List<string> Blockers(
        bool requiresReview, bool resetOverrodeModel, bool agentFellBack, bool hasError,
        bool agentUnavailable = false, bool processDeviationAdmitted = false,
        bool contextOverflow = false, IReadOnlyCollection<string>? unexcludedLowKpd = null)
    {
        var blockers = new List<string>();
        if (requiresReview) return blockers;

        if (resetOverrodeModel) blockers.Add(ResetOverrodeModel);
        if (agentFellBack) blockers.Add(AgentFellBack);
        if (agentUnavailable) blockers.Add(AgentUnavailable);
        if (hasError) blockers.Add(ModelError);
        if (processDeviationAdmitted) blockers.Add(ProcessDeviationAdmitted);
        if (contextOverflow) blockers.Add(ContextOverflow);
        if (unexcludedLowKpd is { Count: > 0 })
        {
            var shown = string.Join("; ", unexcludedLowKpd.Take(3));
            var more = unexcludedLowKpd.Count > 3 ? $" и ещё {unexcludedLowKpd.Count - 3}" : "";
            blockers.Add($"{UnexcludedLowKpdPrefix}: {shown}{more}");
        }
        return blockers;
    }

    public static bool IsEligible(bool requiresReview, IReadOnlyCollection<string> blockers) =>
        !requiresReview && blockers.Count == 0;
}
