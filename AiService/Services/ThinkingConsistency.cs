namespace AiService.Services;

/// <summary>
/// Рассинхрон между рассуждением модели (&lt;think&gt;) и финальным JSON:
/// в конце рассуждения вывод «требуется эскалация», а requires_review=false.
/// Модель не перенесла вывод в структурированный ответ (кейс 2026-06-30
/// Rontek HTC650M) — это её нестабильность, а не баг фильтров. Такие сутки
/// нельзя закрывать автоматически: вердикт поднимается в True.
/// </summary>
public static class ThinkingConsistency
{
    public const string ConflictSignal =
        "[СИСТЕМА] Нестабильный вывод модели: в рассуждении предложена эскалация, но итоговый вердикт был «без эскалации» — требуется проверка человеком";

    // Маркеры вывода об эскалации в самом конце рассуждения (не в середине —
    // там модель может гипотетически рассматривать «если бы это требовало
    // эскалации»; итог обычно в последних предложениях).
    private static readonly string[] Markers =
    [
        "требует эскалац",
        "требуется эскалац",
        "необходима эскалация",
        "необходимо эскалировать",
        "нужна эскалация",
        "следует эскалировать",
        "рекомендуется эскалац",
        "это требует эскалации",
    ];

    /// <summary>
    /// Есть ли в последней трети рассуждения утвердительный вывод об эскалации.
    /// «Не требует эскалации» и подобные отрицания не считаются.
    /// </summary>
    public static bool HasEscalationConclusion(string? thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking)) return false;

        var tail = thinking[(thinking.Length * 2 / 3)..];
        foreach (var marker in Markers)
        {
            var from = 0;
            while (true)
            {
                var at = tail.IndexOf(marker, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                if (!IsNegated(tail, at) && !IsHedged(tail, at)) return true;
                from = at + marker.Length;
            }
        }
        return false;
    }

    // Слова условия/осторожности в том же предложении: «если …, тогда требуется
    // эскалация», «возможно, потребуется эскалация», «в противном случае — эскалация».
    // Это рассматриваемая гипотеза, а не вывод. Живой разбор октября 2026 (12 суток
    // «нестабильного вывода»): все 12 — недописанное рассуждение, оборванное на
    // условной фразе, и итоговый JSON False; настоящий вывод («Итого: … требует
    // эскалации») условных слов не содержит.
    private static readonly string[] HedgeWords =
    [
        "если", "возможно", "вероятно", "в противном случае", "иначе", "тогда",
        "потребуется", "может быть", "может потребоваться", "либо",
    ];

    private static readonly char[] SentenceEnds = ['.', '!', '?', '\n'];

    private static bool IsHedged(string text, int markerAt)
    {
        var start = text.LastIndexOfAny(SentenceEnds, Math.Max(0, markerAt - 1)) + 1;
        var end = text.IndexOfAny(SentenceEnds, markerAt);
        if (end < 0) end = text.Length;
        var sentence = text[start..end].ToLowerInvariant();
        foreach (var word in HedgeWords)
            if (sentence.Contains(word)) return true;
        return false;
    }

    /// <summary> Отрицание непосредственно перед маркером: «не требует», «нет необходимости…», «без эскалации». </summary>
    private static bool IsNegated(string text, int markerAt)
    {
        var start = Math.Max(0, markerAt - 14);
        var before = text[start..markerAt].ToLowerInvariant();
        return before.Contains("не ") || before.Contains("нет ") || before.Contains("без ");
    }

    public static string Tail(string thinking, int max = 400)
    {
        var tail = thinking[(thinking.Length * 2 / 3)..];
        return tail[..Math.Min(max, tail.Length)];
    }
}
