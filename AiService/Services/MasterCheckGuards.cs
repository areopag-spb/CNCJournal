using System.Text.RegularExpressions;
using AiService.Models;

namespace AiService.Services;

/// <summary>
/// Детерминированные гарды verify-part: массовые пустые формулировки с устойчивым
/// вердиктом ok=false, на которых модель флипает между запусками при той же версии
/// промпта (кейсы 10.2026: «нет норм» — 18 повторов, «ТП на 230», «работа
/// с технологом»). Стоят между auto-approve самодостаточных причин и вызовом
/// модели: совпадение — вердикт без модели, с canned remark. Только известные
/// ложные паттерны (точное равенство нормализованного текста, стем «норм»
/// по токенам), никакой семантики и новых правил.
/// </summary>
public static partial class MasterCheckGuards
{
    public const string NormativeRemark =
        "текст про нормативы — выбирайте „Отсутствие/Некорректные нормативы“";

    public const string TpRemark =
        "«ТП на 230» не называет дефект, событие или действие — уточните, что именно объясняет аномалию";

    public const string TechnologistRemark =
        "«работа с технологом» не называет, что именно делали, — уточните действие";

    // Пустой ввод там, где он обязателен (аномалия есть только когда правила ЭЖ требуют объяснения):
    // модель ничего не добавляет к валидации клиента, поэтому тексты совпадают с её сообщениями.
    public const string EmptySetupRemark = "Необходимо указать причину отклонения в наладке и комментарий мастера.";
    public const string EmptyMachiningRemark = "Необходимо указать причину отклонения в изготовлении и комментарий мастера.";
    public const string EmptyDowntimeRemark = "Необходимо дать комментарий: простой более 50%.";

    /// <summary>
    /// Возвращает remark для ok=false, если хотя бы одна аномалия — известный
    /// пустой паттерн; иначе null (решает модель). Порядок детерминирован:
    /// аномалии — по списку запроса, внутри пары — точные совпадения раньше стема.
    /// </summary>
    public static string? CheckKnownFalsePattern(PartContext part, List<VerifyAnomaly> anomalies) =>
        Check(part, anomalies)?.Remark;

    /// <summary>
    /// Срабатывание гарда: замечание и, если причина выбрана неверно, предложение замены
    /// (причина из справочника + поле причины, к которому она относится).
    /// </summary>
    public sealed record GuardHit(string Remark, string? SuggestedReason = null, string? SuggestedField = null);

    public const string MissingNormativesReason = "Отсутствие нормативов";
    public const string IncorrectNormativesReason = "Некорректные нормативы";
    public const string ProcessDeviationReason = "Изготовление не по техпроцессу";

    /// <summary>
    /// «Нет норм» в изготовлении — следствие, а не причина. В техпроцессе норматив изготовления
    /// есть всегда; в записи его нет, потому что оператор поставил прочерк. По инструкции
    /// оператора единственное основание для прочерка — несовпадение операции, станка (типа/группы)
    /// или числа установок с технологической картой, а это причина «Изготовление не по
    /// техпроцессу» (регламент, п. 4.3). От мастера нужна причина, по которой норматив не проставлен.
    /// </summary>
    public const string MachiningNoNormRemark =
        "«нет норм» — следствие, а не причина: укажите, почему оператор не проставил норматив. "
        + "По инструкции прочерк ставят только при несовпадении операции, станка или числа установок "
        + "с техкартой — это «Изготовление не по техпроцессу» с указанием, что именно не совпадает";

    /// <summary>
    /// Причина про нормативы выбрана, а комментарий к ней называет отклонение от техпроцесса.
    /// Регламент, п. 4.3: «Отсутствие/Некорректные нормативы» — только при правильном техпроцессе;
    /// «если деталь выполняется не по техпроцессу, то для этого есть своя причина в списке».
    /// </summary>
    public const string DeviationUnderNormativeReasonRemark =
        "в комментарии названо отклонение от техпроцесса — по регламенту причина тогда "
        + "«Изготовление не по техпроцессу», а причины про нормативы выбирают только при правильном техпроцессе";

    /// <summary>
    /// Проверка ДО автоодобрения самодостаточных причин: «Отсутствие/Некорректные нормативы»
    /// с комментарием, который сам называет отклонение от техпроцесса (VMC40C 08.11.2026:
    /// «Отсутствие нормативов» + «деталь с универсального участка»).
    /// </summary>
    public static GuardHit? CheckNormativeReasonContradiction(PartContext part, List<VerifyAnomaly> anomalies)
    {
        foreach (var a in anomalies)
        {
            var (reason, detail, reasonField) = a.Field switch
            {
                nameof(PartContext.MasterSetupDetail) =>
                    (part.MasterSetupComment, part.MasterSetupDetail, nameof(PartContext.MasterSetupComment)),
                nameof(PartContext.MasterMachiningDetail) =>
                    (part.MasterMachiningComment, part.MasterMachiningDetail, nameof(PartContext.MasterMachiningComment)),
                _ => ((string?)null, (string?)null, ""),
            };
            if (IsNormativeReason(reason) && NamesDeviation(detail))
                return new GuardHit(DeviationUnderNormativeReasonRemark, ProcessDeviationReason, reasonField);
        }
        return null;
    }

    private static bool IsNormativeReason(string? reason)
    {
        var r = reason?.Trim();
        return string.Equals(r, MissingNormativesReason, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r, IncorrectNormativesReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary> То же, что <see cref="CheckKnownFalsePattern"/>, но с предложением замены причины. </summary>
    public static GuardHit? Check(PartContext part, List<VerifyAnomaly> anomalies)
    {
        foreach (var a in anomalies)
        {
            if (IsEmptyRequiredInput(part, a.Field) is { } emptyRemark)
                return new GuardHit(emptyRemark);

            var (reason, detail, reasonField) = a.Field switch
            {
                nameof(PartContext.MasterSetupDetail) =>
                    (part.MasterSetupComment, part.MasterSetupDetail, nameof(PartContext.MasterSetupComment)),
                nameof(PartContext.MasterMachiningDetail) =>
                    (part.MasterMachiningComment, part.MasterMachiningDetail, nameof(PartContext.MasterMachiningComment)),
                _ => ((string?)null, (string?)null, ""),
            };
            if (reason is null || detail is null)
                continue;
            if (IsCryptogram(detail) is { } cryptogramRemark)
                return new GuardHit(cryptogramRemark);
            if (IsNormativeTextAtDrugoe(reason, detail) is { } normativeRemark)
            {
                var suggested = SuggestNormativeReason(detail);
                if (suggested == IncorrectNormativesReason)
                    return new GuardHit(normativeRemark, suggested, reasonField);
                // Мастер сам назвал в строке отклонение от техпроцесса («деталь с универсального
                // участка») — это и есть причина, по которой норматив не проставлен.
                if (RowNamesProcessDeviation(part))
                    return new GuardHit(
                        a.Field == nameof(PartContext.MasterMachiningDetail) ? MachiningNoNormRemark : DeviationUnderNormativeReasonRemark,
                        ProcessDeviationReason, reasonField);
                // Наладка: норматив может действительно отсутствовать — есть своя причина.
                // Изготовление: такой причины нет, «нет норм» ничего не объясняет.
                return a.Field == nameof(PartContext.MasterMachiningDetail)
                    ? new GuardHit(MachiningNoNormRemark)
                    : new GuardHit(normativeRemark, suggested, reasonField);
            }
        }
        return null;
    }

    /// <summary>
    /// Мастер сам назвал в строке отклонение от техпроцесса: выбрал эту причину в одной из
    /// категорий либо написал типовую формулировку («деталь с универсального/ручного участка»,
    /// «с других станков», «заложена … операция/установка»).
    /// </summary>
    public static bool RowNamesProcessDeviation(PartContext part) =>
        IsProcessDeviationReason(part.MasterSetupComment)
        || IsProcessDeviationReason(part.MasterMachiningComment)
        || NamesDeviation(part.MasterSetupDetail)
        || NamesDeviation(part.MasterMachiningDetail)
        || NamesDeviation(part.MasterComment);

    private static bool IsProcessDeviationReason(string? reason) =>
        string.Equals(reason?.Trim(), ProcessDeviationReason, StringComparison.OrdinalIgnoreCase);

    private static bool NamesDeviation(string? text) =>
        !string.IsNullOrWhiteSpace(text) && ProcessDeviationPhrase().IsMatch(text);

    [GeneratedRegex(@"(?:универсальн\w*|ручн\w*)\s+участ|с\s+друг\w+\s+станк|заложен\w*\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessDeviationPhrase();

    /// <summary>
    /// Какая из двух причин про нормативы подходит тексту: «некорректные/неверные/заниженные…» —
    /// «Некорректные нормативы», иначе («нет норм», «норматива нет») — «Отсутствие нормативов».
    /// </summary>
    public static string SuggestNormativeReason(string detail)
    {
        var text = NormalizeExact(detail);
        return IncorrectMarkers.Any(m => text.Contains(m, StringComparison.Ordinal))
            ? IncorrectNormativesReason
            : MissingNormativesReason;
    }

    private static readonly string[] IncorrectMarkers =
        ["некоррект", "неверн", "неправильн", "занижен", "завышен", "ошибочн"];

    /// <summary>
    /// Причина и комментарий мастера по полю аномалии пусты целиком (для простоев — пуст комментарий).
    /// Частично заполненное (выбрана причина без текста, текст без причины) решает модель.
    /// </summary>
    private static string? IsEmptyRequiredInput(PartContext part, string field) => field switch
    {
        nameof(PartContext.MasterSetupDetail) when IsBlank(part.MasterSetupComment) && IsBlank(part.MasterSetupDetail)
            && IsBlank(part.MasterComment) => EmptySetupRemark,
        nameof(PartContext.MasterMachiningDetail) when IsBlank(part.MasterMachiningComment) && IsBlank(part.MasterMachiningDetail)
            && IsBlank(part.MasterComment) => EmptyMachiningRemark,
        nameof(PartContext.SpecifiedDowntimesComment) when IsBlank(part.SpecifiedDowntimesComment) => EmptyDowntimeRemark,
        _ => null,
    };

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// Криптограммы: точное совпадение нормализованного комментария (регистр,
    /// лишние пробелы и висячая пунктуация не в счёт). Варианты с другим текстом
    /// («ТП на 231», «работал с технологом») — не задевает, их решает модель.
    /// </summary>
    private static string? IsCryptogram(string detail) => NormalizeExact(detail) switch
    {
        "тп на 230" => TpRemark,
        "работа с технологом" => TechnologistRemark,
        _ => null,
    };

    /// <summary>
    /// Текст про нормативы («нет норм», «норматива нет», «нармативов» с опечаткой)
    /// при выбранной причине «Другое» — неверный выбор причины из справочника.
    /// Стем считается по токенам: «нормально» («всё нормально») не задевает —
    /// токен «нормально» не равен «норм» и не начинается с «норматив».
    /// </summary>
    private static string? IsNormativeTextAtDrugoe(string reason, string detail)
    {
        if (!reason.Trim().Equals("Другое", StringComparison.OrdinalIgnoreCase))
            return null;
        var tokens = TokenSplit().Split(NormalizeExact(detail));
        return tokens.Any(t => t == "норм"
            || t.StartsWith("норматив", StringComparison.Ordinal)
            || t.StartsWith("нарматив", StringComparison.Ordinal))
            ? NormativeRemark
            : null;
    }

    private static string NormalizeExact(string s) =>
        Whitespace().Replace(s.Trim().TrimEnd('.', ',', ';', '!', '?', ':').Trim().ToLowerInvariant(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^\p{L}]+")]
    private static partial Regex TokenSplit();
}
