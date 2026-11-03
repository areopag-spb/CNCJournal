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

    public static List<string> Blockers(
        bool requiresReview, bool resetOverrodeModel, bool agentFellBack, bool hasError,
        bool agentUnavailable = false, bool processDeviationAdmitted = false)
    {
        var blockers = new List<string>();
        if (requiresReview) return blockers;

        if (resetOverrodeModel) blockers.Add(ResetOverrodeModel);
        if (agentFellBack) blockers.Add(AgentFellBack);
        if (agentUnavailable) blockers.Add(AgentUnavailable);
        if (hasError) blockers.Add(ModelError);
        if (processDeviationAdmitted) blockers.Add(ProcessDeviationAdmitted);
        return blockers;
    }

    public static bool IsEligible(bool requiresReview, IReadOnlyCollection<string> blockers) =>
        !requiresReview && blockers.Count == 0;
}
