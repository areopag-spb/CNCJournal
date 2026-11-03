using AiService.Models;

namespace AiService.Services;

/// <summary>
/// Результат детерминированной проверки жёстких правил.
/// HardSignals — требуют ОБЯЗАТЕЛЬНОЙ эскалации, LLM не может отменить.
/// SoftSignals — оставлены для будущих случаев, где контекст важен.
///               Машинное время > норматива переведено в HardSignals после того,
///               как LLM стабильно игнорировала его в soft-режиме.
/// </summary>
public record HardRuleResult(
    List<string> HardSignals,
    List<string> SoftSignals)
{
    public bool MustEscalate => HardSignals.Count > 0;
    public bool HasSoftSignals => SoftSignals.Count > 0;

    /// <summary>
    /// Ключи PartName§Setup§Order деталей с hard-сигналами — кандидаты в проблемные
    /// строки (флаги СГТ) безо всякой модели: hard она отменить не может.
    /// </summary>
    public List<string> HardFlaggedPartKeys { get; init; } = [];

    /// <summary>
    /// Пары (soft-сигнал, ключ детали): флаг ставится, только если сигнал уцелел
    /// после понижения моделью (см. CollectFlaggedPartKeys в AnalysisController).
    /// </summary>
    public List<(string Signal, string PartKey)> SoftFlagged { get; init; } = [];
}

public static class HardRuleEvaluator
{
    private const string MissingNormativesReason = "Отсутствие нормативов";
    private const string BadFillReason = "Некорректное заполнение";

    // Фиксированные строки из комбобокса MasterSetupComment/MasterMachiningComment,
    // которые однозначно требуют эскалации — это работа технологов/нормировщиков,
    // которые подключаются именно после эскалации.
    // internal: читается AgentTools (какие из них разбираются на несерийных — см. ниже).
    internal static readonly HashSet<string> EscalationReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Изготовление не по техпроцессу",
        "Некорректные нормативы",
        "Отсутствие нормативов",
        // «Некорректное заполнение» — данные недостоверны, требуют исправления:
        // эскалация безусловна (пилот агента 24.09.2026, SKT21 №104 23.09 — модель
        // сняла эскалацию дня с такой причиной). Формулировка сигнала своя
        // (не «пересмотр технологии», а исправление данных), см. ниже.
        "Некорректное заполнение",
    };

    /// <summary>
    /// Причины ИЗГОТОВЛЕНИЯ, которые на несерийном станке НЕ разбираются
    /// (решение владельца 30.09.2026): «норматив неточный» и «норматив отсутствует»
    /// — спор о времени, которое на несерийном станке всё равно не учитывается,
    /// поэтому эскалация по нему бессмысленна. «Отсутствие нормативов» в новых
    /// клиентах выбирается только для наладки, в наборе остаётся для старых клиентов
    /// и ранее сохранённых данных. Остальное на несерийном остаётся:
    /// «Изготовление не по техпроцессу» — отклонение от технологии (человеку надо
    /// смотреть в любом случае), «Некорректное заполнение» — вопрос к достоверности
    /// данных. Норматив=0 при заказе без комментария мастера (HardRule ниже) —
    /// тоже вопрос к данным, поэтому остаётся.
    /// </summary>
    internal static readonly HashSet<string> MachiningReasonsSuppressedOnNonSerial = new(StringComparer.OrdinalIgnoreCase)
    {
        "Некорректные нормативы",
        MissingNormativesReason,
    };

    /// <summary>
    /// Разбирается ли эта причина ИЗГОТОВЛЕНИЯ на несерийном станке: да, только если
    /// она эскалационная (иначе изготовление просто не оценивается) и не подавлена
    /// (MachiningReasonsSuppressedOnNonSerial). Зеркало AgentTools.ReasonSemantics —
    /// правило одно, чтобы модель и правила не разошлись.
    /// </summary>
    internal static bool JudgedOnNonSerialMachining(string? machiningReason)
    {
        var reason = machiningReason ?? "";
        return EscalationReasons.Contains(reason)
            && !MachiningReasonsSuppressedOnNonSerial.Contains(reason);
    }

    // Причины из комбобокса MasterMachiningComment, при наличии которых и непустого
    // MasterComment правило "машинное время > норматива" понижается до soft — решает
    // модель по soft_signal_explanation.txt: если объяснение причинно-релевантно
    // именно станочному времени, эскалация не требуется И деталь — кандидат в
    // suggest_exclude_from_reports (как следствие, не наоборот).
    private static readonly HashSet<string> MachiningTimeSoftDowngradeReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Разовое изменение времени из-за проблем с инструментом/оборудованием",
        "Несоответствующие заготовки",
    };

    // "Доработка" в причине наладки/изготовления освобождает от ряда проверок:
    // КПД = 0 и отсутствие норматива нормальны для доработки.
    private const string ReworkReason = "Доработка";

    // Причина из комбобокса, которая сама по себе объясняет рост машинного времени:
    // оператор делил время между двумя станками (решение владельца 06.10.2026,
    // кейсы Rontek HTC420 №1 06–07.09: модель то принимала, то эскалировала).
    // Закрытый список = достаточное основание, детализация не требуется.
    private const string TwoMachineReason = "Двустаночное обслуживание";

    /// <summary> Идентичность строки для флагов СГТ: имя + установка + заказ.
    /// ordinal (1-based) — номер строки в сутках; без него ключ неоднозначен
    /// внутри дня (см. Evaluate). </summary>
    public static string PartKey(PartContext p, int ordinal) =>
        $"{p.PartName}§{p.Setup}§{p.Order}§#{ordinal}";

    public static HardRuleResult Evaluate(AnalyzeRequest req)
    {
        var hard = new List<string>();
        var soft = new List<string>();
        var hardKeys = new List<string>();
        var softFlagged = new List<(string Signal, string PartKey)>();

        // Несерийный станок (cnc_machines.IsSerial=false, флаг от клиента):
        // КПД изготовления не оценивается НИ В ОДНОМ контуре. null = серийный.
        var checkProductionKpd = req.IsSerialMachine != false;

        for (var partIndex = 0; partIndex < req.Parts.Count; partIndex++)
        {
            var p = req.Parts[partIndex];
            // Ключ флага — с номером строки: «деталь+установка+заказ» внутри дня
            // не уникален (работа разбита на отрезки), и без номера клиент брал
            // первую по списку (кейс 30.09.2026, SKT21 25.09 «Винт…»). Здесь строка
            // известна точно — сразу пишем её номер.
            var key = PartKey(p, partIndex + 1);
            void AddHard(string signal) { hard.Add(signal); hardKeys.Add(key); }
            void AddSoft(string signal) { soft.Add(signal); softFlagged.Add((signal, key)); }
            var hasOrder = !string.IsNullOrWhiteSpace(p.Order)
                           && !p.Order.Equals("Без М/Л", StringComparison.OrdinalIgnoreCase);

            var isReworkSetup = ReworkReason.Equals(p.MasterSetupComment, StringComparison.OrdinalIgnoreCase);
            var isReworkMachining = ReworkReason.Equals(p.MasterMachiningComment, StringComparison.OrdinalIgnoreCase);

            // Норматив наладки/изготовления отсутствует при реальном заказе — требует объяснения
            // ВСЕГДА, независимо от факта работы в смену (синхронизировано с Part.cs.Error на
            // клиенте — там это блокирует сохранение — и с клиентским DetectPartSignals).
            if (p.SetupTimePlan <= 0 && hasOrder && string.IsNullOrWhiteSpace(p.MasterSetupComment))
                AddHard($"[{p.PartName}] Отсутствует норматив наладки при реальном заказе — комментарий мастера не указан");
            if (p.SingleProductionTimePlan <= 0 && hasOrder && string.IsNullOrWhiteSpace(p.MasterMachiningComment))
                AddHard($"[{p.PartName}] Отсутствует норматив изготовления при реальном заказе — комментарий мастера не указан");

            // комбобокс-причины, требующие участия технологов
            // Проверяем MasterSetupComment и MasterMachiningComment по фиксированному списку.
            // "Отсутствие нормативов" — только если есть заказ (доработка без норматива — норма).
            if (!string.IsNullOrWhiteSpace(p.MasterSetupComment)
                && EscalationReasons.Contains(p.MasterSetupComment))
            {
                if (p.MasterSetupComment.Equals(MissingNormativesReason, StringComparison.OrdinalIgnoreCase)
                    && !hasOrder)
                { }
                else if (p.MasterSetupComment.Equals(BadFillReason, StringComparison.OrdinalIgnoreCase))
                {
                    AddHard($"[{p.PartName}] Причина отклонения в наладке: «{BadFillReason}» — данные недостоверны, требуют исправления");
                }
                else
                {
                    AddHard($"[{p.PartName}] Причина отклонения в наладке требует пересмотра технологии: «{p.MasterSetupComment}»");
                }
            }

            if (!string.IsNullOrWhiteSpace(p.MasterMachiningComment)
                && EscalationReasons.Contains(p.MasterMachiningComment)
                // Несерийный станок: «норматив неточный/отсутствует» пропускаем —
                // это спор о времени, которое здесь не учитывается. «не по
                // техпроцессу» и «Некорректное заполнение» остаются: отклонение
                // от технологии и достоверность данных проверяются на любом станке
                // (см. MachiningReasonsSuppressedOnNonSerial). Норматив=0 при
                // заказе без комментария мастера (строка выше) — тоже вопрос к
                // данным, поэтому на несерийном остаётся.
                && (checkProductionKpd
                    || HardRuleEvaluator.JudgedOnNonSerialMachining(p.MasterMachiningComment)))
            {
                if (p.MasterMachiningComment.Equals(MissingNormativesReason, StringComparison.OrdinalIgnoreCase)
                    && !hasOrder)
                { }
                else if (p.MasterMachiningComment.Equals(BadFillReason, StringComparison.OrdinalIgnoreCase))
                {
                    AddHard($"[{p.PartName}] Причина отклонения в изготовлении: «{BadFillReason}» — данные недостоверны, требуют исправления");
                }
                else
                {
                    AddHard($"[{p.PartName}] Причина отклонения в изготовлении требует пересмотра технологии: «{p.MasterMachiningComment}»");
                }
            }

            // Правило 2: оператор сообщает о проблеме с нормативом/технологией.
            // SOFT — модель может понизить если мастер дал конкретное, причинно-релевантное
            // объяснение в MasterComment (не просто выбрал причину из комбобокса).
            // Остаётся HARD только если MasterComment пустой при наличии жалобы оператора.
            if (OperatorMentionsNormativeIssue(p.OperatorComment))
            {
                bool masterGaveConcreteExplanation =
                    !string.IsNullOrWhiteSpace(p.MasterMachiningComment)
                    || !string.IsNullOrWhiteSpace(p.MasterSetupComment);
                // Если мастер вообще ничего не написал — жёстко
                if (!masterGaveConcreteExplanation)
                    AddHard($"[{p.PartName}] Оператор сообщает о некорректном нормативе или технологии без объяснения мастера");
                else
                    AddSoft($"[{p.PartName}] Оператор сообщает о некорректном нормативе или технологии (мастер дал объяснение — требует верификации)");
            }

            // Правило 2б: оператор просит не учитывать/исключить запись. Исключение из
            // расчёта — решение СГТ, поэтому всегда HARD, независимо от мастера.
            if (OperatorAsksToExclude(p.OperatorComment))
                AddHard($"[{p.PartName}] Оператор просит не учитывать запись: «{p.OperatorComment!.Trim()}»");

            // Правило 3: КПД изготовления < 70% без объяснения мастера
            // null (= "б/и") не считается, доработка освобождена.
            // Несерийный станок: правило отключено целиком (см. checkProductionKpd).
            // Без норматива (SingleProductionTimePlan=0) ProductionRatio считается как 0/факт = 0%,
            // что не является реальным КПД — но это исключение только для "Без М/Л" (!hasOrder).
            // При реальном заказе отсутствие норматива само по себе проблема, эскалация должна идти.
            if (checkProductionKpd
                && p.ProductionRatio is { } pr && pr < 0.7
                && (p.SingleProductionTimePlan > 0 || hasOrder)
                && !p.NoProductionHappened
                && !isReworkMachining
                && string.IsNullOrWhiteSpace(p.MasterMachiningComment))
            {
                AddHard($"[{p.PartName}] КПД изготовления {pr:0.#%} < 70% без объяснения мастера");
            }

            // Правило 4: КПД = 0 при наличии фактической работы
            // Наладка: КПД наладки = 0 означает SetupTimePlan = 0 при SetupTimeFact > 0.
            // Это нормально для инструментальных операций (нет заказа, нет норматива),
            // но подозрительно когда заказ есть и это не доработка.
            if (p.SetupRatio is 0
                && p.SetupTimeFact > 0
                && p.SetupTimePlan > 0
                && !p.NoSetupHappened
                && hasOrder
                && !isReworkSetup)
            {
                AddHard($"[{p.PartName}] КПД наладки = 0% при наличии фактической наладки ({p.SetupTimeFact:0}мин) и норматива");
            }

            // Изготовление: КПД = 0 при наличии деталей и заказа — противоречие данных.
            // Несерийный станок: отключено вместе с остальными КПД-проверками изготовления.
            if (checkProductionKpd
                && p.ProductionRatio is 0
                && p.FinishedCount > 0
                && hasOrder
                && !isReworkMachining)
            {
                AddHard($"[{p.PartName}] КПД изготовления = 0% при {p.FinishedCount:0} изготовленных деталях");
            }

            // машинное время > норматива
            // Переведено из soft в hard: LLM стабильно игнорировала soft-сигнал,
            // не перенося его в downgraded_signals, но и не эскалируя.
            // Физический смысл: норматив = машинное время + ручные операции,
            // поэтому machiningTime > plan означает, что на ручные операции
            // времени не осталось вообще — это структурная аномалия, не шум.
            //
            // 2026-10-19: равенство (100%) больше не аномалия (решение владельца:
            // культура заполнения вынуждает, возможен откат обратно на >=).
            //
            // 2026-07-15: исключение — разовые причины (проблемы с инструментом/оборудованием,
            // несоответствующие заготовки) при непустом MasterComment → soft. Модель верифицирует
            // объяснение по soft_signal_explanation.txt: причинно-релевантно → requires_review не
            // повышается И деталь — кандидат в exclude (как следствие); нерелевантно/нет причины
            // из этого списка → остаётся HARD.
            //
            // 2026-07-25: убрано исключение по КПД>100% — хороший итоговый КПД сам по себе НЕ повод
            // пропускать сигнал (может маскировать, например, урезанный оператором перерыв ради
            // укладывания в норматив — такое всё равно требует разбора). Теперь единственное, что
            // решает hard/soft — наличие обоснованной причины у мастера, а не то, чем всё кончилось.
            //
            // 2026-07-25: MasterComment разделён на MasterSetupDetail/MasterMachiningDetail — здесь
            // нужна именно детализация ИЗГОТОВЛЕНИЯ, поэтому предпочитаем MasterMachiningDetail;
            // MasterComment (архив) остаётся запасным путём для записей до разделения поля.
            // Несерийный станок: правило отключено целиком (решение владельца
            // 30.09.2026) — изготовление здесь не учитывается, поэтому время
            // против норматива не показатель. Смотрим только на явные жалобы
            // в комментариях и «не по техпроцессу» (см. JudgedOnNonSerialMachining).
            if (checkProductionKpd
                && p.MachiningTime > 0.5 && p.SingleProductionTimePlan > 0
                && p.MachiningTime > p.SingleProductionTimePlan
                && !p.NoProductionHappened
                && !p.IsSmallBatch)
            {
                // Клиент уже прислал тот же факт («Машинное время X > штучного
                // норматива Y») — второй текст о том же в ответе только шумит
                // (кейс 30.09.2026, SKT21 27.09), поэтому берём текст клиента
                // дословно: в итоговом списке они склеятся через Distinct,
                // модель увидит в soft-списке ровно ту строку, что надо
                // скопировать в downgraded_signals. Вердикт и флаг при этом
                // решаются здесь же: одиночный клиентский сигнал иначе ни во
                // что не превращается (правило «2+» его не ловит) — кейс
                // 06.10.2026, HTC420 №2 17.09 уходил в ok с сигналом в списке.
                var signal = p.Signals.FirstOrDefault(s => s.Contains("Машинное время", StringComparison.OrdinalIgnoreCase))
                    ?? $"[{p.PartName}] Машинное время {p.MachiningTime:0.#}мин > " +
                       $"норматива {p.SingleProductionTimePlan:0.#}мин " +
                       $"({p.MachiningTime / p.SingleProductionTimePlan:0%})";

                bool hasConcreteOneTimeReason =
                    !string.IsNullOrWhiteSpace(p.MasterMachiningComment)
                    && MachiningTimeSoftDowngradeReasons.Contains(p.MasterMachiningComment)
                    && (!string.IsNullOrWhiteSpace(p.MasterMachiningDetail) || !string.IsNullOrWhiteSpace(p.MasterComment));

                // Двустаночное обслуживание объясняет рост машинного времени само по
                // себе (см. TwoMachineReason): модель верифицирует по
                // soft_signal_explanation.txt, понижает — день не эскалируется.
                bool isTwoMachineExplained =
                    TwoMachineReason.Equals(p.MasterMachiningComment, StringComparison.OrdinalIgnoreCase);

                if (hasConcreteOneTimeReason || isTwoMachineExplained)
                    AddSoft(signal);
                else
                    AddHard(signal);
            }
        }

        // два и более необъяснённых сигнала от C#-клиента
        // Soft-сигналы намеренно не включаем в подсчёт (их сейчас нет, но на будущее).
        var allClientSignals = req.Parts.SelectMany(p => p.Signals).Concat(req.Signals).Distinct().ToList();
        if (allClientSignals.Count >= 2)
        {
            hard.Add($"Два и более необъяснённых сигнала от системы ({allClientSignals.Count})");
        }

        return new HardRuleResult([.. hard.Distinct()], soft)
        {
            HardFlaggedPartKeys = [.. hardKeys.Distinct()],
            SoftFlagged = softFlagged,
        };
    }

    private static readonly string[] ExcludeRequestMarkers =
    [
        "не учитыв", "не учесть", "исключить", "исключите",
        "не считать", "не засчитыва", "не засчитать", "не брать в расчёт", "не брать в расчет",
        "не включать в расч", "не включайте в расч",
    ];

    private static bool OperatorAsksToExclude(string? comment) => AsksToExclude(comment);

    /// <summary> Текст просит не учитывать/исключить запись (маркеры общие для оператора и мастера). </summary>
    internal static bool AsksToExclude(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var l = text.ToLowerInvariant();
        return ExcludeRequestMarkers.Any(l.Contains);
    }

    private static bool OperatorMentionsNormativeIssue(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return false;
        var l = comment.ToLowerInvariant();

        if ((l.Contains("укладыва") || l.Contains("уложиться")) && l.Contains("норматив"))
            return false;

        if (l.Contains("норматив") || l.Contains("не соответствует")
            || l.Contains("некорректн") || l.Contains("режимы не")
            || l.Contains("программа не соответ") || l.Contains("скорректировать")
            // жалоба числами без слова «норматив»: «время наладки на 1 шт 320 мин»
            || l.Contains("на 1 шт") || l.Contains("на 1шт"))
            return true;

        // Явное требование оператора проверить нормативы/технологию (пилот 24.09.2026):
        // слово «технология/техпроцесс» + дефект. Голое «требуется проверка» без
        // привязки к нормативам/технологии — не триггер (слишком широко).
        // Зеркало — AiServiceClient.OperatorMentionsNormativeIssue, держать в синхроне.
        if (l.Contains("технолог") || l.Contains("техпроцесс") || l.Contains("техпроцес"))
        {
            return l.Contains("некорректн") || l.Contains("не соответств")
                || l.Contains("провер") || l.Contains("требу") || l.Contains("нужн")
                || l.Contains("неверн") || l.Contains("неправильн") || l.Contains("скорректир")
                || l.Contains("сомнева") || l.Contains("ошиб");
        }

        return false;
    }
}