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

    public static List<string> Blockers(
        bool requiresReview, bool resetOverrodeModel, bool agentFellBack, bool hasError,
        bool agentUnavailable = false)
    {
        var blockers = new List<string>();
        if (requiresReview) return blockers;

        if (resetOverrodeModel) blockers.Add(ResetOverrodeModel);
        if (agentFellBack) blockers.Add(AgentFellBack);
        if (agentUnavailable) blockers.Add(AgentUnavailable);
        if (hasError) blockers.Add(ModelError);
        return blockers;
    }

    public static bool IsEligible(bool requiresReview, IReadOnlyCollection<string> blockers) =>
        !requiresReview && blockers.Count == 0;
}
