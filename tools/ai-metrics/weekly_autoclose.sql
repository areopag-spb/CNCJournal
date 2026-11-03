-- Недельная сводка автозакрытия по журналу ai_auto_close_log (P3.3 / P1.4 плана исправлений).
-- Единица счёта — станко-сутки. Меняйте @From/@To; DryRun=1 — теневые запуски.
-- Запускать только на чтение: скрипт ничего не изменяет.
DECLARE @From date = DATEADD(day, -7, CAST(GETDATE() AS date));
DECLARE @To   date = CAST(GETDATE() AS date);

-- 1. Исходы по запускам (реальные и теневые отдельно), по последнему решению на станко-сутки.
;WITH last_decision AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY Machine, ShiftDate, DryRun ORDER BY RunAt DESC, Id DESC) rn
    FROM ai_auto_close_log
    WHERE RunAt >= @From AND RunAt < DATEADD(day, 1, @To)
)
SELECT CASE WHEN DryRun = 1 THEN 'тень' ELSE 'реальный' END AS Режим,
       Outcome AS Исход, COUNT(*) AS Станко_суток, SUM(ExclusionsApplied) AS Авто_исключений
FROM last_decision WHERE rn = 1
GROUP BY DryRun, Outcome ORDER BY DryRun, Outcome;

-- 2. Выборочный контроль закрытых ИИ станко-суток (кнопки «Согласен» / «Пропуск»).
SELECT ISNULL(ControlVerdict, 'не проверено') AS Контроль, COUNT(*) AS Станко_суток
FROM ai_auto_close_log
WHERE Outcome = 'Closed' AND DryRun = 0 AND RunAt >= @From AND RunAt < DATEADD(day, 1, @To)
GROUP BY ControlVerdict ORDER BY 2 DESC;

-- 3. Пропуски (miss / miss_hard) с разбором: кто, когда, комментарий.
SELECT Machine, ShiftDate, ControlVerdict, ControlBy, ControlAt, ControlComment
FROM ai_auto_close_log
WHERE ControlVerdict IN ('miss', 'miss_hard') AND ControlAt >= @From
ORDER BY ControlAt DESC;

-- 4. Топ причин, почему сутки оставлены человеку (Manual).
SELECT TOP 15 LEFT(Reasons, 120) AS Причины, COUNT(*) AS Станко_суток
FROM ai_auto_close_log
WHERE Outcome = 'Manual' AND RunAt >= @From AND RunAt < DATEADD(day, 1, @To)
GROUP BY LEFT(Reasons, 120) ORDER BY 2 DESC;

-- 5. Закрытые ИИ сутки, которые человек потом изменил (решение уже не «ИИ-авто»).
SELECT l.Machine, l.ShiftDate, r.ReviewedBy, r.Decision
FROM ai_auto_close_log l
JOIN ai_day_reviews r ON r.Machine = l.Machine AND r.ShiftDate = l.ShiftDate
WHERE l.Outcome = 'Closed' AND l.DryRun = 0 AND l.RunAt >= @From
  AND r.AutoClosed = 0
ORDER BY l.ShiftDate DESC;
