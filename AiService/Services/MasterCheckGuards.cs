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
    public static string? CheckKnownFalsePattern(PartContext part, List<VerifyAnomaly> anomalies)
    {
        foreach (var a in anomalies)
        {
            if (IsEmptyRequiredInput(part, a.Field) is { } emptyRemark)
                return emptyRemark;

            var (reason, detail) = a.Field switch
            {
                nameof(PartContext.MasterSetupDetail) => (part.MasterSetupComment, part.MasterSetupDetail),
                nameof(PartContext.MasterMachiningDetail) => (part.MasterMachiningComment, part.MasterMachiningDetail),
                _ => ((string?)null, (string?)null),
            };
            if (reason is null || detail is null)
                continue;
            if (IsCryptogram(detail) is { } cryptogramRemark)
                return cryptogramRemark;
            if (IsNormativeTextAtDrugoe(reason, detail) is { } normativeRemark)
                return normativeRemark;
        }
        return null;
    }

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
