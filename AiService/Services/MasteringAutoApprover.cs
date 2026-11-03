using AiService.Models;
using System.Text.RegularExpressions;

namespace AiService.Services;

/// <summary>
/// Подтверждает «Освоение» детерминированно, на стороне C#, не передавая решение модели.
/// Освоение считается подтверждённым, когда мастер выбрал «Освоение» в комбобоксе
/// (MasterSetupComment) и выполнено одно из двух:
///  • MasterSetupDetail или MasterComment указывает на потерю программы («не сохранилась,
///    отрабатывали заново» и т.п.) — достаточная причина по регламенту, перекрывает историю;
///  • история не опровергает освоение (нет записи с FinishedCount &gt; 0) и деталь не заявляет
///    изменение УП/КД/технологии — там нужна конкретика, оценку релевантности которой
///    регулярками не выразить, и она остаётся за моделью.
/// У подтверждённых деталей сигналы частичной наладки снимаются до построения промпта,
/// а при КПД наладки &lt;100% строка предлагается к исключению из отчётов с К1-формулировкой
/// (Триггер 3, промпт 2.6).
/// </summary>
/// <remarks>
/// Правило «комбобокса достаточно» прописано в промпте, но модель стабильно требует
/// дополнительного подтверждения историей, и пост-фильтрацией её отказ не лечится —
/// поэтому проверка вынесена в код.
/// </remarks>
public static class MasteringAutoApprover
{
    private static readonly Regex RevisionClaim = new(
        @"\bуп\b|\bкд\b|технолог|программ|изменен|чертеж|чертёж",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // «Программа не сохранилась, отрабатывали заново» — это потеря программы, а не заявление
    // об изменении УП/технологии; system_prompt.txt называет такую формулировку достаточной
    // конкретикой. Отделяется отдельными регулярками, потому что слово «программ» из
    // RevisionClaim попадает и в неё.
    // ВАЖНО: "не" и "сохран" проверяются НЕЗАВИСИМО, без требования соседства — реальные формулировки
    // разносят их словами между ("не БЫЛА сохранена"), жёсткая склейка "не\s*сохран" такое не ловит
    // (первая версия фикса ошибочно предполагала соседство — не проверил на реальном тексте).
    private static readonly Regex Negation = new(@"\bне\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SaveWord = new(@"сохран", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProgramLost = new(
        @"утеря|потеря|слет",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Держать в синхроне с AiHistorySensitiveReasons в remeLog/Models/Part.cs. В отличие от
    // «Освоение» история здесь НЕ проверяется вообще: «Отсутствие/Некорректные нормативов»
    // верим слепо по выбору мастера — норматив мог не совпасть из-за другого выданного
    // техпроцесса или ещё нескольких причин, которых в присланных полях просто не видно, и
    // формализовать их regex'ом/сверкой истории нельзя, в отличие от FinishedCount для
    // «Освоение». Модель verify-part стабильно требует «конкретику» вопреки прямому тексту
    // промпта (прогон 27.07: id «Кулачки», «Заглушка НМГ48-03-509-01» х2 — ok=false).
    private static readonly string[] NormativesReasons =
    {
        "Отсутствие нормативов",
        "Некорректные нормативы",
    };

    // «Изготовление не по техпроцессу» + глоссарийная формулировка master_check.txt правило 4 —
    // модель стабильно отклоняет и эти, несмотря на явные примеры в промпте (прогон 29.07:
    // «Заложена 1 установка» на трёх записях подряд ok=false с «требуется указать, что именно
    // отклонилось»). Ловим только regex-надёжные формулировки глоссария — «Заложен(а/о/ы) X»
    // (X = установка/операция/станок, число и род не важны), «деталь/заготовка с других
    // станков», «с универсального/ручного участка», количество установок («за 2 установки»,
    // «в одну установку») и «деталь/заготовка с <станок>» («деталь с 550») — конкретные
    // упоминания чужого станка/участка, дальше уточнять нечего (прод-логи verify, 08–09.26:
    // «С универсального участка» и «за 2 установки» флипуют true/false при том же входе).
    private const string NotByProcessReason = "Изготовление не по техпроцессу";
    private static readonly Regex LaidOutWord = new(@"залож\w*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LaidOutNoun = new(
        @"устан\w*|операц\w*|станк\w*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OtherMachinesRoute = new(
        @"с\s+других\s+станк\w*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // «С универсального/ручного участка» — та же ситуация, что «с других станков»
    // (глоссарий правила 4 master_check.txt). Отдельной регуляркой, т.к. «ручной»
    // без «участка» ловить нельзя — слишком широко.
    private static readonly Regex NonCncAreaRoute = new(
        @"универсальн\w*(\s+участ\w*)?|ручн\w*\s+участ\w*|с\s+универсальн\w*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // «МЦ <операция>» — механический цех: мастер называет, какая универсальная/ручная операция
    // выполнялась («МЦ Сверлильная операция»). ЭЖ ведёт учёт только операций ЧПУ, поэтому такое название
    // и есть объяснение (решение владельца 03.11.2026: 52 из 140 замечаний октября были на эту формулировку).
    // «МЦ» только отдельным словом и обязательно с названием операции следом.
    private static readonly Regex MechanicalShopRoute = new(
        @"(?<![\p{L}\d])МЦ(?![\p{L}\d])\W+\p{L}{3,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Количество установок как название отклонения («за 2 установки»,
    // «в одну установку»). Без требования слова «заложено» — реальное число
    // уже называет несоответствие (прод-логи 08.26: «за 2 установки» флипует).
    private static readonly Regex SetupCountRoute = new(
        @"\bза\s+\d+\s*установ\w*|\bв\s+(одн\w+|\d+)\s*установ\w*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // «Деталь/заготовка с <станок/номер>» («деталь с 550», «заготовка с Rontek») —
    // упоминание чужого станка называет замену станка/маршрута. Вызывается только
    // для причины «Изготовление не по техпроцессу», поэтому широко, но безопасно.
    private static readonly Regex PartFromOtherMachine = new(
        @"детал\w*\s+с\s+\S+|заготов\w*\s+с\s+\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // «Несоответствующие заготовки» — по правилу 3 master_check.txt достаточно
    // НАЗВАТЬ дефект одним словом («перекаленные» — дословный пример ok=true
    // в промпте, модель его отклоняет: прод-логи 07–08.26). Ловим любую
    // содержательную детализацию, отсекая только явный мусор.
    private const string MismatchedBlanksReason = "Несоответствующие заготовки";

    private static readonly string[] GarbageDetails =
    {
        "хз", "просто", "просто так", "просто так написал", "конкретика",
        "ы", "?", "-", "нет", "не знаю",
    };

    // Заказ «Без М/Л» здесь НЕ обрабатывается осознанно: «любой непустой
    // комментарий достаточен» — слишком грубое правило для кода, такие случаи
    // оценивает модель по особому случаю правила 3 master_check.txt
    // (минимальная планка + исключения). Дневной анализ по-прежнему
    // смягчает «Без М/Л» через FalsePositiveFilter.

    // Действие над УП/программой («написание УП», «редактирование программы») —
    // названное действие, по правилу 3 достаточно для «Другое»/«Доработка» и
    // подтверждает «Освоение» (упоминание смены УП — подтверждение, а не
    // опровержение; см. правило 2 master_check.txt). Действует в ОБОИХ контурах
    // (verify-part и дневной анализ) — строгость одинаковая.
    private static readonly Regex ProgramAction = new(
        @"написа|редакт|подгон|перепис|правк|дораб|отлад",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProgramObject = new(
        @"программ|\bуп\b|\bчпу\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool MentionsProgramWork(string detail) =>
        ProgramAction.IsMatch(detail) && ProgramObject.IsMatch(detail);
    public sealed record Outcome(
        List<string> RemovedSignals,
        List<string> AutoExcludes);

    /// <summary>
    /// Причина наладки самодостаточна (verify-part может подтверждать без обращения к модели):
    /// «Освоение» — по правилам <see cref="IsConfirmedMastering"/>; «Отсутствие/Некорректные
    /// нормативов» — безусловно, история не проверяется (см. комментарий у NormativesReasons);
    /// «Изготовление не по техпроцессу» — только для regex-надёжных формулировок глоссария;
    /// «Несоответствующие заготовки» — при содержательной детализации (назван дефект);
    /// «Другое»/«Доработка» — только при названном действии над УП/программой.
    /// Заказ «Без М/Л» осознанно не автоапрувится — его оценивает модель.
    /// </summary>
    public static bool IsSetupReasonSelfSufficient(PartContext p)
    {
        var detail = EffectiveDetail(p.MasterSetupDetail, p.MasterComment);

        var reason = p.MasterSetupComment.Trim();
        if (reason.Equals("Освоение", StringComparison.OrdinalIgnoreCase))
            return IsConfirmedMastering(p);
        if (NormativesReasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
            return true;
        if (reason.Equals(NotByProcessReason, StringComparison.OrdinalIgnoreCase))
            return MatchesNotByProcessGlossary(detail);
        if (reason.Equals(MismatchedBlanksReason, StringComparison.OrdinalIgnoreCase))
            return HasConcreteDefectDetail(detail);
        if (reason.Equals("Другое", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("Доработка", StringComparison.OrdinalIgnoreCase))
            return MentionsProgramWork(detail);
        return false;
    }

    /// <summary>
    /// Причина изготовления самодостаточна: «Отсутствие/Некорректные нормативов» — безусловно;
    /// «Изготовление не по техпроцессу» — только для regex-надёжных формулировок глоссария;
    /// «Несоответствующие заготовки» — при содержательной детализации;
    /// «Другое»/«Доработка» — только при названном действии над УП/программой.
    /// «Освоение» для изготовления не рассматривается — в текущем регламенте это причина наладки.
    /// Заказ «Без М/Л» осознанно не автоапрувится — его оценивает модель.
    /// </summary>
    public static bool IsMachiningReasonSelfSufficient(PartContext p)
    {
        var detail = EffectiveDetail(p.MasterMachiningDetail, p.MasterComment);

        var reason = p.MasterMachiningComment.Trim();
        if (NormativesReasons.Contains(reason, StringComparer.OrdinalIgnoreCase))
            return true;
        if (reason.Equals(NotByProcessReason, StringComparison.OrdinalIgnoreCase))
            return MatchesNotByProcessGlossary(detail);
        if (reason.Equals(MismatchedBlanksReason, StringComparison.OrdinalIgnoreCase))
            return HasConcreteDefectDetail(detail);
        if (reason.Equals("Другое", StringComparison.OrdinalIgnoreCase)
            || reason.Equals("Доработка", StringComparison.OrdinalIgnoreCase))
            return MentionsProgramWork(detail);
        return false;
    }

    /// <summary>
    /// Детализация называет дефект: непустая, не мусор. Одно слово («перекаленные»,
    /// «заготовки разных диаметров») — достаточно по правилу 3 master_check.txt.
    /// </summary>
    private static bool HasConcreteDefectDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        var text = detail.Trim();
        if (text.Length < 2) return false;
        return !GarbageDetails.Contains(text, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesNotByProcessGlossary(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        if (OtherMachinesRoute.IsMatch(detail)) return true;
        if (NonCncAreaRoute.IsMatch(detail)) return true;
        if (MechanicalShopRoute.IsMatch(detail)) return true;
        if (SetupCountRoute.IsMatch(detail)) return true;
        if (PartFromOtherMachine.IsMatch(detail)) return true;
        return LaidOutWord.IsMatch(detail) && LaidOutNoun.IsMatch(detail);
    }

    private static string EffectiveDetail(string detail, string archiveComment) =>
        string.IsNullOrWhiteSpace(detail) ? archiveComment : detail;

    /// <summary> Диспетчер для verify-part: покрывает ли C# аномалию по имени поля целиком. </summary>
    public static bool IsAnomalyFieldSelfSufficient(string field, PartContext p) => field switch
    {
        nameof(PartContext.MasterSetupDetail) => IsSetupReasonSelfSufficient(p),
        nameof(PartContext.MasterMachiningDetail) => IsMachiningReasonSelfSufficient(p),
        _ => false,
    };

    public static bool IsConfirmedMastering(PartContext p)
    {
        if (!p.MasterSetupComment.Trim().Equals("Освоение", StringComparison.OrdinalIgnoreCase)) return false;

        var detail = string.IsNullOrWhiteSpace(p.MasterSetupDetail) ? p.MasterComment : p.MasterSetupDetail;
        bool isProgramLoss = detail.Contains("программ", StringComparison.OrdinalIgnoreCase)
            && (ProgramLost.IsMatch(detail) || (Negation.IsMatch(detail) && SaveWord.IsMatch(detail)));

        // Потеря программы проверяется ДО истории и перекрывает её. Промпт называет эту причину
        // достаточной именно для случая «деталь делалась раньше, но программа не сохранилась»,
        // то есть история в таком случае как раз и должна показывать деталь — проверка истории
        // первой обнулила бы это исключение.
        if (isProgramLoss) return true;

        // Названное действие над УП/программой («написание УП», «редактирование программы») —
        // упоминание смены УП подтверждает освоение (правило 2 master_check.txt), а не
        // опровергает его. Действует и в дневном анализе (Apply) — строгость одинаковая.
        if (MentionsProgramWork(detail)) return true;

        if (HistoryRefutes(p.PartsHistory)) return false;

        return !RevisionClaim.IsMatch(detail);
    }

    private static bool HistoryRefutes(PartsHistoryDto? history) =>
        history != null && history.Lines.Any(l => l.FinishedCount > 0);

    /// <summary> Мутирует request: снимает сигналы наладки подтверждённых освоений. </summary>
    public static Outcome Apply(AnalyzeRequest request)
    {
        var removed = new List<string>();
        var excludes = new List<string>();

        foreach (var p in request.Parts)
        {
            if (!IsConfirmedMastering(p)) continue;

            var setupSignals = p.Signals
                .Where(s => s.Contains("наладк", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (setupSignals.Count > 0)
            {
                p.Signals = [.. p.Signals.Except(setupSignals)];
                removed.AddRange(setupSignals.Select(s => $"[{p.PartName}] {s}"));
            }

            if (p.SetupRatio is > 0 and < 1 && !p.NoSetupHappened)
                excludes.Add(
                    $"{p.PartName}§{p.Setup}§{p.Order}§освоение: КПД наладки "
                    + $"{p.SetupRatio.Value * 100:0}% ниже 100% может негативно повлиять на К1 оператора");
        }

        return new Outcome(removed, excludes);
    }
}
