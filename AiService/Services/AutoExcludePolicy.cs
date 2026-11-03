using AiService.Models;
using System.Text.RegularExpressions;

namespace AiService.Services;

/// <summary>
/// Какие предложения об исключении из К1 ИИ вправе применить сам при автозакрытии суток
/// (решение владельца 03.11.2026). Применяется только то, что проверяется данными:
///  1. «Освоение» мастера, подтверждённое (<see cref="MasteringAutoApprover.IsConfirmedMastering"/>),
///     при КПД наладки &lt; 100%;
///  2. причина мастера из закрытого списка: «Разовое изменение времени…», «Несоответствующие
///     заготовки», «Работа ученика» (последняя — при КПД &lt; 100% в категории причины);
///  3. мастер явно написал «исключить»/«не учитывать» в детализации или комментарии.
/// Строки с отмеченными простоями — обычные простои (обслуживание, оснастка), правила простоев
/// решают их отдельно: предложение игнорируется и не блокирует. Всё остальное (свободный текст
/// модели: «подгон размера», «выкатывание» и т. п.) не применяется и уводит сутки человеку
/// (решение принимает клиент по спискам из ответа). Просьба оператора — всегда HARD в HardRule.
/// </summary>
public static partial class AutoExcludePolicy
{
    private const string Mastering = "Освоение";
    private const string Apprentice = "Работа ученика";

    private static readonly string[] AutoMasterReasons =
    [
        "Разовое изменение времени из-за проблем с инструментом/оборудованием",
        "Несоответствующие заготовки",
    ];

    public enum Basis { None, Mastering, MasterReason, MasterExplicit, Downtime }

    /// <summary> Разбор предложений на применяемые сами и игнорируемые (простои). Остальные — вне списков. </summary>
    public static (List<string> Auto, List<string> Ignored) Classify(
        AnalyzeRequest request, IEnumerable<string> entries)
    {
        var auto = new List<string>();
        var ignored = new List<string>();
        foreach (var entry in entries)
        {
            var part = ResolvePart(request, entry);
            switch (part == null ? Basis.None : BasisOf(part))
            {
                case Basis.Mastering:
                case Basis.MasterReason:
                case Basis.MasterExplicit:
                    auto.Add(entry);
                    break;
                case Basis.Downtime:
                    ignored.Add(entry);
                    break;
            }
        }
        return (auto, ignored);
    }

    public static Basis BasisOf(PartContext p)
    {
        if (HardRuleEvaluator.AsksToExclude(p.MasterSetupDetail)
            || HardRuleEvaluator.AsksToExclude(p.MasterMachiningDetail)
            || HardRuleEvaluator.AsksToExclude(p.MasterComment))
            return Basis.MasterExplicit;

        var setupReason = (p.MasterSetupComment ?? "").Trim();
        var machReason = (p.MasterMachiningComment ?? "").Trim();

        if (setupReason.Equals(Mastering, StringComparison.OrdinalIgnoreCase)
            && MasteringAutoApprover.IsConfirmedMastering(p)
            && !p.NoSetupHappened && p.SetupRatio is < 1.0)
            return Basis.Mastering;

        if (AutoMasterReasons.Contains(setupReason, StringComparer.OrdinalIgnoreCase)
            || AutoMasterReasons.Contains(machReason, StringComparer.OrdinalIgnoreCase))
            return Basis.MasterReason;

        if ((setupReason.Equals(Apprentice, StringComparison.OrdinalIgnoreCase)
                && !p.NoSetupHappened && p.SetupRatio is < 1.0)
            || (machReason.Equals(Apprentice, StringComparison.OrdinalIgnoreCase)
                && !p.NoProductionHappened && p.ProductionRatio is < 1.0))
            return Basis.MasterReason;

        if (!string.IsNullOrWhiteSpace(p.SpecifiedDowntimesList))
            return Basis.Downtime;

        return Basis.None;
    }

    /// <summary> Строка суток по записи «Имя§Уст§Заказ§Причина[§#N]»; неоднозначная — null. </summary>
    private static PartContext? ResolvePart(AnalyzeRequest request, string entry)
    {
        var seg = (entry ?? "").Split('§');
        if (seg.Length < 3) return null;

        if (seg.Length > 3 && OrdinalTail().IsMatch(seg[^1].Trim())
            && int.TryParse(seg[^1].Trim()[1..], out var ordinal)
            && ordinal >= 1 && ordinal <= request.Parts.Count
            && request.Parts[ordinal - 1].PartName == seg[0])
            return request.Parts[ordinal - 1];

        var found = ExcludeTriggerValidator.FindAllParts(request.Parts, seg[0], seg[1], seg[2]);
        return found.Count == 1 ? found[0].Part : null;
    }

    [GeneratedRegex(@"^#\d{1,3}$")]
    private static partial Regex OrdinalTail();
}
