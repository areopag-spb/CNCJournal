using Microsoft.Data.SqlClient;
using remeLog.Core;
using remeLog.Core.Db;
using remeLog.Infrastructure.Extensions;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace remeLog.Infrastructure
{
    public static partial class Database
    {
        /// <param name="review">Решение аналитика. <see cref="DayReview.AiResult"/> —
        /// если задан, вердикт ИИ пишется тем же запросом (иначе ИИ-поля не трогаются).</param>
        public static async Task<DbResult<int>>
            SaveDayReviewAsync(DayReview review)
        {
            // Демо: ревью дня нигде не хранятся — возвращаем фиктивный Id.
            if (DomainSettings.DemoMode)
            {
                review.Id = -1;
                return await Task.FromResult(DbResult<int>.Ok(-1));
            }

            // ИИ-поля: COALESCE с текущими значениями — обычное сохранение решения
            // (без анализа) их не затирает, а принятый вердикт перезаписывает прежний.
            var ai = review.AiResult;
            // Столбцы автозакрытия (AutoClosed/AutoCloseBlockers) есть только в БД после
            // «Обновления БД»; версию схемы не поднимали, поэтому проверяем наличие на лету.
            const string upsertTemplate = @"
                DECLARE @id INT;

                IF EXISTS (SELECT 1 FROM ai_day_reviews
                           WHERE Machine = @Machine AND ShiftDate = @ShiftDate)
                BEGIN
                    UPDATE ai_day_reviews
                    SET ReviewedBy      = CASE WHEN @TouchReviewMeta = 1 THEN @ReviewedBy ELSE ReviewedBy END,
                        ReviewedAt      = CASE WHEN @TouchReviewMeta = 1 THEN @ReviewedAt ELSE ReviewedAt END,
                        Decision        = @Decision,
                        IsFullyReviewed = @IsFullyReviewed,
                        Comment         = @Comment,
                        AiFeedback      = @AiFeedback,
                        AiRequiresReview   = COALESCE(@AiRequiresReview,   AiRequiresReview),
                        AiThinkingEnabled = COALESCE(@AiThinkingEnabled, AiThinkingEnabled),
                        AiConfidence       = COALESCE(@AiConfidence,       AiConfidence),
                        AiSignals          = COALESCE(@AiSignals,          AiSignals),
                        AiExplanation      = COALESCE(@AiExplanation,      AiExplanation),
                        AiModelVersion     = COALESCE(@AiModelVersion,     AiModelVersion),
                        AiPromptVersion    = COALESCE(@AiPromptVersion,    AiPromptVersion),
                        AiAnalyzedAt       = COALESCE(@AiAnalyzedAt,       AiAnalyzedAt)
                        /*AUTO_UPDATE*/
                    WHERE Machine = @Machine AND ShiftDate = @ShiftDate;

                    SELECT @id = Id FROM ai_day_reviews
                    WHERE Machine = @Machine AND ShiftDate = @ShiftDate;
                END
                ELSE
                BEGIN
                    INSERT INTO ai_day_reviews
                        (Machine, ShiftDate, ReviewedBy, ReviewedAt,
                         Decision, IsFullyReviewed, Comment, AiFeedback,
                         AiRequiresReview, AiThinkingEnabled, AiConfidence,
                         AiSignals, AiExplanation, AiModelVersion, AiPromptVersion, AiAnalyzedAt/*AUTO_COLS*/)
                    VALUES
                        (@Machine, @ShiftDate, @ReviewedBy, @ReviewedAt,
                         @Decision, @IsFullyReviewed, @Comment, @AiFeedback,
                         @AiRequiresReview, @AiThinkingEnabled, @AiConfidence,
                         @AiSignals, @AiExplanation, @AiModelVersion, @AiPromptVersion, @AiAnalyzedAt/*AUTO_VALS*/);

                    SET @id = SCOPE_IDENTITY();
                END

                SELECT @id;";

            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                var hasAuto = await HasAutoCloseColumnsAsync(conn);
                var upsertSql = upsertTemplate
                    .Replace("/*AUTO_UPDATE*/", hasAuto
                        ? @", AutoClosed = CASE WHEN @TouchReviewMeta = 1 THEN @AutoClosed ELSE AutoClosed END,
                            AutoCloseBlockers = CASE WHEN @TouchReviewMeta = 1 THEN @AutoCloseBlockers ELSE AutoCloseBlockers END"
                        : "")
                    .Replace("/*AUTO_COLS*/", hasAuto ? ", AutoClosed, AutoCloseBlockers" : "")
                    .Replace("/*AUTO_VALS*/", hasAuto ? ", @AutoClosed, @AutoCloseBlockers" : "");
                await using var cmd = new SqlCommand(upsertSql, conn);

                cmd.Parameters.AddWithValue("@AutoClosed", review.AutoClosed);
                cmd.Parameters.AddWithValue("@AutoCloseBlockers",
                    string.IsNullOrEmpty(review.AutoCloseBlockers) ? DBNull.Value : review.AutoCloseBlockers);
                cmd.Parameters.AddWithValue("@Machine", review.Machine);
                cmd.Parameters.AddWithValue("@ShiftDate", review.ShiftDate.Date);
                cmd.Parameters.AddWithValue("@ReviewedBy", review.ReviewedBy);
                cmd.Parameters.AddWithValue("@ReviewedAt", review.ReviewedAt);
                cmd.Parameters.AddWithValue("@TouchReviewMeta", review.TouchReviewMeta);
                cmd.Parameters.AddWithValue("@Decision", review.Decision.ToDbString());
                cmd.Parameters.AddWithValue("@IsFullyReviewed", review.IsFullyReviewed);
                cmd.Parameters.AddWithValue("@Comment",
                    string.IsNullOrEmpty(review.Comment) ? DBNull.Value : review.Comment);
                cmd.Parameters.AddWithValue("@AiFeedback",
                    string.IsNullOrEmpty(review.AiFeedback) ? DBNull.Value : review.AiFeedback);

                // Принятый вердикт ИИ — теми же параметрами, что и решение.
                cmd.Parameters.AddWithValue("@AiRequiresReview", ai is null ? DBNull.Value : ai.RequiresReview);
                cmd.Parameters.AddWithValue("@AiThinkingEnabled", ai is null ? DBNull.Value : review.AiThinkingEnabled);
                cmd.Parameters.AddWithValue("@AiConfidence", ai is null ? DBNull.Value : ai.Confidence);
                cmd.Parameters.AddWithValue("@AiSignals",
                    ai is null ? DBNull.Value : JsonSerializer.Serialize(ai.Signals, _jsonOpts));
                cmd.Parameters.AddWithValue("@AiExplanation",
                    ai is null || string.IsNullOrEmpty(ai.Explanation) ? DBNull.Value : ai.Explanation);
                cmd.Parameters.AddWithValue("@AiModelVersion",
                    ai is null || string.IsNullOrEmpty(review.AiModelVersion) ? DBNull.Value : review.AiModelVersion);
                cmd.Parameters.AddWithValue("@AiPromptVersion",
                    ai is null || string.IsNullOrEmpty(ai.PromptVersion) ? DBNull.Value : ai.PromptVersion);
                cmd.Parameters.AddWithValue("@AiAnalyzedAt", ai is null ? DBNull.Value : DateTime.Now);

                var scalar = await cmd.ExecuteScalarAsync();
                int newId = Convert.ToInt32(scalar);
                review.Id = newId;

                return DbResult<int>.Ok(newId);
            }
            catch (SqlException sqlEx)
            {
                Log.WriteError(sqlEx, $"SaveDayReviewAsync: #{sqlEx.Number}");
                return sqlEx.Number == 18456
                    ? DbResult<int>.Fail(DbResult.AuthError, $"Ошибка авторизации: {sqlEx.Message}")
                    : DbResult<int>.Fail(DbResult.Error, $"Ошибка БД #{sqlEx.Number}: {sqlEx.Message}");
            }
            catch (Exception ex)
            {
                Log.WriteError(ex,"SaveDayReviewAsync");
                return DbResult<int>.FailWithError(ex.Message);
            }
        }

        /// <summary> Куда попала ИИ-оценка суток при <see cref="SaveAiAnalysisAsync"/>. </summary>
        public enum AiAnalysisSaveKind
        {
            /// <summary> У суток ещё не было вердикта ИИ — записан в ai_day_reviews. </summary>
            Live,
            /// <summary> Живой вердикт уже есть — оценка легла в историю ai_day_reeval, вердикт не тронут. </summary>
            ReEval,
        }

        /// <summary>
        /// Запись ИИ-оценки суток (P3.4): живой вердикт неизменяем. Если у суток уже есть
        /// AiAnalyzedAt — оценка пишется только в историю <c>ai_day_reeval</c> (нет таблицы —
        /// ошибка, а НЕ перезапись); если вердикта не было (решение без ИИ) — как раньше,
        /// в ai_day_reviews. Принятый аналитиком вердикт попадает в ai_day_reviews другим путём
        /// (<see cref="SaveDayReviewAsync"/> с AiResult) — это осознанное решение человека.
        /// </summary>
        public const string SaveAiAnalysisSql = @"
        IF EXISTS (SELECT 1 FROM ai_day_reviews WHERE Id = @Id AND AiAnalyzedAt IS NOT NULL)
        BEGIN
            INSERT INTO ai_day_reeval
                (DayReviewId, AiRequiresReview, AiConfidence, AiSignals, AiExplanation,
                 AiModelVersion, AiPromptVersion, AiThinkingEnabled, EvaluatedAt, EvaluatedBy)
            VALUES
                (@Id, @AiRequiresReview, @AiConfidence, @AiSignals, @AiExplanation,
                 @AiModelVersion, @AiPromptVersion, @AiThinkingEnabled, @AiAnalyzedAt, @EvaluatedBy);
            SELECT 1;
        END
        ELSE
        BEGIN
            UPDATE ai_day_reviews
            SET AiRequiresReview = @AiRequiresReview,
                AiThinkingEnabled = @AiThinkingEnabled,
                AiConfidence     = @AiConfidence,
                AiSignals        = @AiSignals,
                AiExplanation    = @AiExplanation,
                AiModelVersion   = @AiModelVersion,
                AiPromptVersion   = @AiPromptVersion,
                AiAnalyzedAt     = @AiAnalyzedAt,
                AiFeedback       = NULL
            WHERE Id = @Id AND AiAnalyzedAt IS NULL;
            SELECT 0;
        END";

        public static async Task<DbResult<AiAnalysisSaveKind>> SaveAiAnalysisAsync(
            int dayReviewId, AiAnalysisResult result, string modelVersion, bool thinkingEnabled)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(DbResult<AiAnalysisSaveKind>.Ok(AiAnalysisSaveKind.Live));
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(SaveAiAnalysisSql, conn);
                cmd.Parameters.AddWithValue("@Id", dayReviewId);
                cmd.Parameters.AddWithValue("@AiRequiresReview", result.RequiresReview);
                cmd.Parameters.AddWithValue("@AiThinkingEnabled", thinkingEnabled);
                cmd.Parameters.AddWithValue("@AiConfidence", result.Confidence);
                cmd.Parameters.AddWithValue("@AiSignals", JsonSerializer.Serialize(result.Signals, _jsonOpts));
                cmd.Parameters.AddWithValue("@AiExplanation",
                    (object?)result.Explanation ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AiModelVersion", modelVersion);
                cmd.Parameters.AddWithValue("@AiPromptVersion",
                    (object?)result.PromptVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AiAnalyzedAt", DateTime.Now);
                cmd.Parameters.AddWithValue("@EvaluatedBy", Environment.UserName);
                var kind = Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1
                    ? AiAnalysisSaveKind.ReEval
                    : AiAnalysisSaveKind.Live;
                return DbResult<AiAnalysisSaveKind>.Ok(kind);
            }
            catch (Exception ex)
            {
                Log.WriteError(ex,"SaveAiAnalysisAsync");
                return DbResult<AiAnalysisSaveKind>.FailWithError(ex.Message);
            }
        }

        public static async Task<DayReview?> GetDayReviewAsync(string machine, DateTime shiftDate)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult<DayReview?>(null);
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                var sql = $@"
                SELECT {await DayReviewColumnsAsync(conn)}
                FROM ai_day_reviews
                WHERE Machine = @Machine AND ShiftDate = @ShiftDate";
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Machine", machine);
                cmd.Parameters.AddWithValue("@ShiftDate", shiftDate.Date);

                await using var r = await cmd.ExecuteReaderAsync();
                return await r.ReadAsync() ? ReadDayReview(r) : null;
            }
            catch (Exception ex)
            {
                Log.WriteError(ex,"GetDayReviewAsync");
                return null;
            }
        }

        public static async Task<Dictionary<(string Machine, DateTime Date), DayReview>>
            GetDayReviewsForPeriodAsync(IEnumerable<string> machines, DateTime fromDate, DateTime toDate)
        {
            if (DomainSettings.DemoMode)
                return await Task.FromResult(new Dictionary<(string, DateTime), DayReview>());
            var result = new Dictionary<(string, DateTime), DayReview>();

            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                var sql = $@"
                SELECT {await DayReviewColumnsAsync(conn)}
                FROM ai_day_reviews
                WHERE ShiftDate BETWEEN @From AND @To
                ORDER BY ShiftDate, Machine";
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@From", fromDate.Date);
                cmd.Parameters.AddWithValue("@To", toDate.Date);

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var dr = ReadDayReview(r);
                    result[(dr.Machine, dr.ShiftDate.Date)] = dr;
                }
            }
            catch (Exception ex)
            {
                Log.WriteError(ex,"GetDayReviewsForPeriodAsync");
            }

            return result;
        }

        /// <summary>
        /// Станко-сутки за период, по которым у ИИ есть сохранённый вердикт (ai_day_verdicts). Нет таблицы
        /// (БД не обновлена) или сбой — пустой набор.
        /// </summary>
        public static async Task<HashSet<(string Machine, DateTime Date)>> GetAiDayVerdictKeysAsync(
            DateTime fromDate, DateTime toDate)
        {
            var result = new HashSet<(string, DateTime)>();
            if (DomainSettings.DemoMode) return result;
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(
                    "SELECT Machine, ShiftDate FROM ai_day_verdicts WHERE ShiftDate BETWEEN @From AND @To", conn);
                cmd.Parameters.AddWithValue("@From", fromDate.Date);
                cmd.Parameters.AddWithValue("@To", toDate.Date);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    result.Add((r.GetString(0), r.GetDateTime(1).Date));
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, "GetAiDayVerdictKeysAsync");
            }
            return result;
        }

        public static async Task<List<DayReview>> GetAllDayReviewsAsync(CancellationToken ct = default)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(new List<DayReview>());
            var result = new List<DayReview>();

            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync(ct);
                var sql = $@"
                SELECT {await DayReviewColumnsAsync(conn, ct)}
                FROM ai_day_reviews
                ORDER BY ShiftDate, Machine";
                await using var cmd = new SqlCommand(sql, conn);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    result.Add(ReadDayReview(r));
                }
            }
            catch (Exception ex)
            {
                Log.WriteError(ex,"GetAllDayReviewsAsync");
            }

            return result;
        }

        public static async Task<List<DayReview>> GetAutoCloseHistoryAsync(CancellationToken ct = default)
        {
            // Кто и когда пакетно закрывал пустые сутки: маркер — комментарий
            // «Автозакрытие: ...», который ставит автозакрытие пустых.
            if (DomainSettings.DemoMode) return await Task.FromResult(new List<DayReview>());
            var result = new List<DayReview>();

            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync(ct);
                var sql = $@"
                SELECT {await DayReviewColumnsAsync(conn, ct)}
                FROM ai_day_reviews
                WHERE Comment LIKE @Marker + N'%'
                ORDER BY ShiftDate DESC, Machine";
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Marker", DayReview.AutoCloseCommentMarker);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    result.Add(ReadDayReview(r));
                }
            }
            catch (Exception ex)
            {
                Log.WriteError(ex,"GetAutoCloseHistoryAsync");
            }

            return result;
        }

        public static async Task<DbResult<string>>
            SavePartFlagAsync(PartFlag flag)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(DbResult<string>.Ok("OK"));
            const string sql = @"
                IF EXISTS (SELECT 1 FROM ai_part_flags
                           WHERE DayReviewId = @DayReviewId AND PartGuid = @PartGuid)
                    UPDATE ai_part_flags
                    SET IsCleared = @IsCleared, Comment = @Comment
                    WHERE DayReviewId = @DayReviewId AND PartGuid = @PartGuid;
                ELSE
                    INSERT INTO ai_part_flags (DayReviewId, PartGuid, IsCleared, Comment)
                    VALUES (@DayReviewId, @PartGuid, @IsCleared, @Comment);";

            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@DayReviewId", flag.DayReviewId);
                cmd.Parameters.AddWithValue("@PartGuid", flag.PartGuid);
                cmd.Parameters.AddWithValue("@IsCleared", flag.IsCleared);
                cmd.Parameters.AddWithValue("@Comment",
                    string.IsNullOrEmpty(flag.Comment) ? DBNull.Value : flag.Comment);

                await cmd.ExecuteNonQueryAsync();
                return DbResult<string>.Ok("OK");
            }
            catch (Exception ex)
            {
                Log.WriteError(ex,"SavePartFlagAsync");
                return DbResult<string>.FailWithError(ex.Message);
            }
        }

        public static async Task<List<PartFlag>> GetPartFlagsAsync(int dayReviewId)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(new List<PartFlag>());
            const string sql = @"
                SELECT Id, DayReviewId, PartGuid, IsCleared, Comment,
                       AiRequiresReview, AiConfidence, AiSuggestedReason, AiSignals, AiExplanation
                FROM ai_part_flags
                WHERE DayReviewId = @DayReviewId";

            var result = new List<PartFlag>();
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@DayReviewId", dayReviewId);

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    result.Add(new PartFlag
                    {
                        Id = r.GetInt32(0),
                        DayReviewId = r.GetInt32(1),
                        PartGuid = r.GetGuid(2),
                        IsCleared = r.GetBoolean(3),
                        Comment = r.IsDBNull(4) ? string.Empty : r.GetString(4),
                        AiRequiresReview = r.IsDBNull(5) ? null : r.GetBoolean(5),
                        AiConfidence = r.IsDBNull(6) ? null : r.GetDouble(6),
                        AiSuggestedReason = r.IsDBNull(7) ? null : r.GetString(7),
                        AiSignals = r.IsDBNull(8) ? null : r.GetString(8),
                        AiExplanation = r.IsDBNull(9) ? null : r.GetString(9),
                    });
                }
            }
            catch (Exception ex)
            {
                Log.WriteError(ex,"GetPartFlagsAsync");
            }
            return result;
        }

        public static async Task<DbResult<string>>
            ClearPartFlagsAsync(int dayReviewId)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(DbResult<string>.Ok("OK"));
            const string sql = "DELETE FROM ai_part_flags WHERE DayReviewId = @DayReviewId";

            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@DayReviewId", dayReviewId);
                await cmd.ExecuteNonQueryAsync();
                return DbResult<string>.Ok("OK");
            }
            catch (Exception ex)
            {
                Log.WriteError(ex,"ClearPartFlagsAsync");
                return DbResult<string>.FailWithError(ex.Message);
            }
        }

        private static DayReview ReadDayReview(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            Machine = r.GetString(1),
            ShiftDate = r.GetDateTime(2),
            ReviewedBy = r.GetString(3),
            ReviewedAt = r.GetDateTime(4),
            Decision = AnalystDecisionExtensions.FromDbString(r.GetString(5)),
            IsFullyReviewed = r.GetBoolean(6),
            Comment = r.IsDBNull(7) ? string.Empty : r.GetString(7),
            AiRequiresReview = r.IsDBNull(8) ? null : r.GetBoolean(8),
            AiConfidence = r.IsDBNull(9) ? null : r.GetDouble(9),
            AiSignals = r.IsDBNull(10) ? null : r.GetString(10),
            AiExplanation = r.IsDBNull(11) ? null : r.GetString(11),
            AiModelVersion = r.IsDBNull(12) ? null : r.GetString(12),
            AiPromptVersion = r.IsDBNull(13) ? null : r.GetString(13),
            AiAnalyzedAt = r.IsDBNull(14) ? null : r.GetDateTime(14),
            AiThinkingEnabled = r.IsDBNull(15) ? null : r.GetBoolean(15),
            AiFeedback = r.IsDBNull(16) ? null : r.GetString(16),
            AiVerdict = r.IsDBNull(17) ? null : r.GetString(17),
            // Столбцы автозакрытия в выборке только если они есть в БД (см. DayReviewColumnsAsync).
            AutoClosed = r.FieldCount > 18 && !r.IsDBNull(18) && r.GetBoolean(18),
            AutoCloseBlockers = r.FieldCount > 19 && !r.IsDBNull(19) ? r.GetString(19) : null,
        };

        private const string DayReviewBaseColumns = @"Id, Machine, ShiftDate, ReviewedBy, ReviewedAt,
                       Decision, IsFullyReviewed, Comment,
                       AiRequiresReview, AiConfidence, AiSignals, AiExplanation,
                       AiModelVersion, AiPromptVersion, AiAnalyzedAt,
                       AiThinkingEnabled, AiFeedback, AiVerdict";

        /// <summary> Список столбцов ai_day_reviews для ReadDayReview (индексы 0–17 фиксированы). </summary>
        private static async Task<string> DayReviewColumnsAsync(SqlConnection conn, CancellationToken ct = default) =>
            await HasAutoCloseColumnsAsync(conn, ct)
                ? DayReviewBaseColumns + ", AutoClosed, AutoCloseBlockers"
                : DayReviewBaseColumns;

        // Версию схемы не поднимали (см. AppSettings.RequiredSchemaVersion), поэтому клиент
        // новее БД: наличие столбцов автозакрытия проверяем на лету. «Есть» кэшируем насовсем,
        // «нет» — на 10 минут (администратор может обновить БД, не перезапуская клиентов).
        private static bool? _autoCloseColumns;
        private static DateTime _autoCloseColumnsCheckedAt;

        private static async Task<bool> HasAutoCloseColumnsAsync(SqlConnection conn, CancellationToken ct = default)
        {
            if (_autoCloseColumns == true) return true;
            if (_autoCloseColumns == false
                && DateTime.UtcNow - _autoCloseColumnsCheckedAt < TimeSpan.FromMinutes(10))
                return false;

            await using var cmd = new SqlCommand(
                @"SELECT CASE WHEN COL_LENGTH('ai_day_reviews', 'AutoClosed') IS NOT NULL
                               AND COL_LENGTH('ai_day_reviews', 'AutoCloseBlockers') IS NOT NULL
                              THEN 1 ELSE 0 END", conn);
            var has = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
            _autoCloseColumns = has;
            _autoCloseColumnsCheckedAt = DateTime.UtcNow;
            return has;
        }

        /// <summary>
        /// Пишет журнал авто-исключений одним батчем в одной транзакции. Таблицы нет
        /// (БД не обновлена) или запись не удалась — Fail: автозакрытие с исключениями
        /// без журнала не выполняется.
        /// </summary>
        public static async Task<DbResult<int>> SaveAutoExcludeLogAsync(IReadOnlyCollection<AutoExcludeLogEntry> entries)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(DbResult<int>.Ok(0));
            if (entries.Count == 0) return DbResult<int>.Ok(0);

            const string sql = @"
                INSERT INTO ai_auto_exclude_log
                    (Machine, ShiftDate, PartGuid, PartName, Setup, [Order], Reason,
                     AiPromptVersion, ExcludedBy, ExcludedAt)
                VALUES
                    (@Machine, @ShiftDate, @PartGuid, @PartName, @Setup, @Order, @Reason,
                     @AiPromptVersion, @ExcludedBy, @ExcludedAt);";

            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var tx = conn.BeginTransaction();
                foreach (var e in entries)
                {
                    await using var cmd = new SqlCommand(sql, conn, tx);
                    cmd.Parameters.AddWithValue("@Machine", e.Machine);
                    cmd.Parameters.AddWithValue("@ShiftDate", e.ShiftDate.Date);
                    cmd.Parameters.AddWithValue("@PartGuid", e.PartGuid);
                    cmd.Parameters.AddWithValue("@PartName", (object?)e.PartName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Setup", e.Setup);
                    cmd.Parameters.AddWithValue("@Order", (object?)e.Order ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Reason", (object?)e.Reason ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@AiPromptVersion", (object?)e.AiPromptVersion ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ExcludedBy", e.ExcludedBy);
                    cmd.Parameters.AddWithValue("@ExcludedAt", e.ExcludedAt);
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
                return DbResult<int>.Ok(entries.Count);
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, "SaveAutoExcludeLogAsync");
                return DbResult<int>.FailWithError(ex.Message);
            }
        }

        /// <summary> Журнал авто-исключений по суткам станка (не отменённые и отменённые). Нет таблицы — пусто. </summary>
        public static async Task<List<AutoExcludeLogEntry>> GetAutoExcludeLogAsync(string machine, DateTime shiftDate)
        {
            var result = new List<AutoExcludeLogEntry>();
            if (DomainSettings.DemoMode) return result;

            const string sql = @"
                SELECT Id, Machine, ShiftDate, PartGuid, PartName, Setup, [Order], Reason,
                       AiPromptVersion, ExcludedBy, ExcludedAt, RevertedBy, RevertedAt
                FROM ai_auto_exclude_log
                WHERE Machine = @Machine AND ShiftDate = @ShiftDate
                ORDER BY ExcludedAt, Id";

            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Machine", machine);
                cmd.Parameters.AddWithValue("@ShiftDate", shiftDate.Date);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    result.Add(new AutoExcludeLogEntry
                    {
                        Id = r.GetInt32(0),
                        Machine = r.GetString(1),
                        ShiftDate = r.GetDateTime(2),
                        PartGuid = r.GetGuid(3),
                        PartName = r.IsDBNull(4) ? string.Empty : r.GetString(4),
                        Setup = r.GetInt32(5),
                        Order = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                        Reason = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                        AiPromptVersion = r.IsDBNull(8) ? null : r.GetString(8),
                        ExcludedBy = r.GetString(9),
                        ExcludedAt = r.GetDateTime(10),
                        RevertedBy = r.IsDBNull(11) ? null : r.GetString(11),
                        RevertedAt = r.IsDBNull(12) ? null : r.GetDateTime(12),
                    });
                }
            }
            catch (Exception ex)
            {
                // Таблицы может не быть (БД не обновлена) — для чтения это «журнала нет».
                Log.WriteError(ex, "GetAutoExcludeLogAsync");
            }
            return result;
        }

        /// <summary>
        /// Сохраняет последний вердикт ИИ по станко-суткам (перезаписывает предыдущий). Вспомогательная запись:
        /// нет таблицы (БД не обновлена) или сбой — Fail, вызывающий не прерывается.
        /// </summary>
        public static async Task<DbResult<bool>> SaveAiDayVerdictAsync(AiDayVerdict v)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(DbResult<bool>.Ok(true));
            const string sql = @"
                MERGE ai_day_verdicts AS t
                USING (SELECT @Machine AS Machine, @ShiftDate AS ShiftDate) AS s
                   ON t.Machine = s.Machine AND t.ShiftDate = s.ShiftDate
                WHEN MATCHED THEN UPDATE SET
                    AnalyzedAt = @AnalyzedAt, AnalyzedBy = @AnalyzedBy, Source = @Source,
                    InputFingerprint = @Fingerprint, AnalysisVersion = @AnalysisVersion,
                    AiModelVersion = @Model, ThinkingEnabled = @Thinking,
                    RequiresReview = @RequiresReview, Confidence = @Confidence, ResultJson = @ResultJson
                WHEN NOT MATCHED THEN INSERT
                    (Machine, ShiftDate, AnalyzedAt, AnalyzedBy, Source, InputFingerprint, AnalysisVersion,
                     AiModelVersion, ThinkingEnabled, RequiresReview, Confidence, ResultJson)
                    VALUES (@Machine, @ShiftDate, @AnalyzedAt, @AnalyzedBy, @Source, @Fingerprint, @AnalysisVersion,
                     @Model, @Thinking, @RequiresReview, @Confidence, @ResultJson);";
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Machine", v.Machine);
                cmd.Parameters.AddWithValue("@ShiftDate", v.ShiftDate.Date);
                cmd.Parameters.AddWithValue("@AnalyzedAt", v.AnalyzedAt);
                cmd.Parameters.AddWithValue("@AnalyzedBy", v.AnalyzedBy);
                cmd.Parameters.AddWithValue("@Source", v.Source);
                cmd.Parameters.AddWithValue("@Fingerprint", v.InputFingerprint);
                cmd.Parameters.AddWithValue("@AnalysisVersion", (object?)v.AnalysisVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Model", (object?)v.AiModelVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Thinking", v.ThinkingEnabled);
                cmd.Parameters.AddWithValue("@RequiresReview", v.Result.RequiresReview);
                cmd.Parameters.AddWithValue("@Confidence", v.Result.Confidence);
                cmd.Parameters.AddWithValue("@ResultJson", JsonSerializer.Serialize(v.Result, _jsonOpts));
                await cmd.ExecuteNonQueryAsync();
                return DbResult<bool>.Ok(true);
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, "SaveAiDayVerdictAsync");
                return DbResult<bool>.FailWithError(ex.Message);
            }
        }

        /// <summary> Последний сохранённый вердикт ИИ по станко-суткам или null (нет записи, нет таблицы, сбой). </summary>
        public static async Task<AiDayVerdict?> GetAiDayVerdictAsync(string machine, DateTime shiftDate)
        {
            if (DomainSettings.DemoMode) return null;
            const string sql = @"
                SELECT Id, Machine, ShiftDate, AnalyzedAt, AnalyzedBy, Source, InputFingerprint, AnalysisVersion,
                       AiModelVersion, ThinkingEnabled, ResultJson
                FROM ai_day_verdicts WHERE Machine = @Machine AND ShiftDate = @ShiftDate";
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Machine", machine);
                cmd.Parameters.AddWithValue("@ShiftDate", shiftDate.Date);
                await using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return null;
                var result = JsonSerializer.Deserialize<AiAnalysisResult>(r.GetString(10)) ?? new AiAnalysisResult();
                return new AiDayVerdict
                {
                    Id = r.GetInt32(0),
                    Machine = r.GetString(1),
                    ShiftDate = r.GetDateTime(2),
                    AnalyzedAt = r.GetDateTime(3),
                    AnalyzedBy = r.GetString(4),
                    Source = r.GetString(5),
                    InputFingerprint = r.GetString(6),
                    AnalysisVersion = r.IsDBNull(7) ? null : r.GetString(7),
                    AiModelVersion = r.IsDBNull(8) ? null : r.GetString(8),
                    ThinkingEnabled = r.GetBoolean(9),
                    Result = result,
                };
            }
            catch (Exception ex)
            {
                // Таблицы может не быть (БД не обновлена) — для чтения это «вердикта нет».
                Log.WriteError(ex, "GetAiDayVerdictAsync");
                return null;
            }
        }

        /// <summary> Пишет решение автозакрытия в журнал. Нет таблицы/сбой — Fail (вызывающий не прерывается). </summary>
        public static async Task<DbResult<int>> SaveAutoCloseLogAsync(AutoCloseLogEntry e)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(DbResult<int>.Ok(0));
            const string sql = @"
                INSERT INTO ai_auto_close_log
                    (Machine, ShiftDate, RunAt, RunBy, DryRun, Outcome, Reasons,
                     AiRequiresReview, AiConfidence, AiPromptVersion, ExclusionsApplied)
                VALUES
                    (@Machine, @ShiftDate, @RunAt, @RunBy, @DryRun, @Outcome, @Reasons,
                     @AiRequiresReview, @AiConfidence, @AiPromptVersion, @ExclusionsApplied);
                SELECT CAST(SCOPE_IDENTITY() AS INT);";
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Machine", e.Machine);
                cmd.Parameters.AddWithValue("@ShiftDate", e.ShiftDate.Date);
                cmd.Parameters.AddWithValue("@RunAt", e.RunAt);
                cmd.Parameters.AddWithValue("@RunBy", e.RunBy);
                cmd.Parameters.AddWithValue("@DryRun", e.DryRun);
                cmd.Parameters.AddWithValue("@Outcome", e.Outcome);
                cmd.Parameters.AddWithValue("@Reasons",
                    e.Reasons.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(e.Reasons, _jsonOpts));
                cmd.Parameters.AddWithValue("@AiRequiresReview", (object?)e.AiRequiresReview ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AiConfidence", (object?)e.AiConfidence ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AiPromptVersion", (object?)e.AiPromptVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ExclusionsApplied", e.ExclusionsApplied);
                var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                e.Id = id;
                return DbResult<int>.Ok(id);
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, "SaveAutoCloseLogAsync");
                return DbResult<int>.FailWithError(ex.Message);
            }
        }

        /// <summary> Журнал автозакрытия за период (по дате суток). Нет таблицы — пусто. </summary>
        public static async Task<List<AutoCloseLogEntry>> GetAutoCloseLogAsync(DateTime from, DateTime to)
        {
            var result = new List<AutoCloseLogEntry>();
            if (DomainSettings.DemoMode) return result;
            const string sql = @"
                SELECT Id, Machine, ShiftDate, RunAt, RunBy, DryRun, Outcome, Reasons,
                       AiRequiresReview, AiConfidence, AiPromptVersion, ExclusionsApplied,
                       ControlVerdict, ControlBy, ControlAt, ControlComment
                FROM ai_auto_close_log
                WHERE ShiftDate BETWEEN @From AND @To
                ORDER BY ShiftDate, Machine, RunAt, Id";
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@From", from.Date);
                cmd.Parameters.AddWithValue("@To", to.Date);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    string[] reasons = Array.Empty<string>();
                    if (!r.IsDBNull(7))
                    {
                        try { reasons = JsonSerializer.Deserialize<string[]>(r.GetString(7)) ?? reasons; }
                        catch { reasons = new[] { r.GetString(7) }; }
                    }
                    result.Add(new AutoCloseLogEntry
                    {
                        Id = r.GetInt32(0),
                        Machine = r.GetString(1),
                        ShiftDate = r.GetDateTime(2),
                        RunAt = r.GetDateTime(3),
                        RunBy = r.GetString(4),
                        DryRun = r.GetBoolean(5),
                        Outcome = r.GetString(6),
                        Reasons = reasons,
                        AiRequiresReview = r.IsDBNull(8) ? null : r.GetBoolean(8),
                        AiConfidence = r.IsDBNull(9) ? null : r.GetDouble(9),
                        AiPromptVersion = r.IsDBNull(10) ? null : r.GetString(10),
                        ExclusionsApplied = r.GetInt32(11),
                        ControlVerdict = r.IsDBNull(12) ? null : r.GetString(12),
                        ControlBy = r.IsDBNull(13) ? null : r.GetString(13),
                        ControlAt = r.IsDBNull(14) ? null : r.GetDateTime(14),
                        ControlComment = r.IsDBNull(15) ? null : r.GetString(15),
                    });
                }
            }
            catch (Exception ex)
            {
                // Таблицы может не быть (БД не обновлена) — для чтения это «журнала нет».
                Log.WriteError(ex, "GetAutoCloseLogAsync");
            }
            return result;
        }

        /// <summary> Записывает результат выборочного контроля закрытых ИИ суток. </summary>
        public static async Task<DbResult<bool>> SetAutoCloseControlAsync(
            int logId, string verdict, string by, string? comment)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(DbResult<bool>.Ok(true));
            const string sql = @"
                UPDATE ai_auto_close_log
                SET ControlVerdict = @Verdict, ControlBy = @By, ControlAt = @At, ControlComment = @Comment
                WHERE Id = @Id";
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Id", logId);
                cmd.Parameters.AddWithValue("@Verdict", verdict);
                cmd.Parameters.AddWithValue("@By", by);
                cmd.Parameters.AddWithValue("@At", DateTime.Now);
                cmd.Parameters.AddWithValue("@Comment", string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment);
                await cmd.ExecuteNonQueryAsync();
                return DbResult<bool>.Ok(true);
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, "SetAutoCloseControlAsync");
                return DbResult<bool>.FailWithError(ex.Message);
            }
        }

        /// <summary>
        /// Отмечает отменёнными активные записи журнала по строкам суток (откат автозакрытия:
        /// сами исключения снимает вызывающий).
        /// </summary>
        public static async Task<DbResult<bool>> RevertAutoExcludeLogAsync(
            string machine, DateTime shiftDate, IReadOnlyCollection<Guid> partGuids, string revertedBy)
        {
            if (DomainSettings.DemoMode || partGuids.Count == 0) return await Task.FromResult(DbResult<bool>.Ok(true));
            const string sql = @"
                UPDATE ai_auto_exclude_log
                SET RevertedBy = @By, RevertedAt = @At
                WHERE Machine = @Machine AND ShiftDate = @ShiftDate
                  AND PartGuid = @PartGuid AND RevertedAt IS NULL";
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                foreach (var guid in partGuids)
                {
                    await using var cmd = new SqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@Machine", machine);
                    cmd.Parameters.AddWithValue("@ShiftDate", shiftDate.Date);
                    cmd.Parameters.AddWithValue("@PartGuid", guid);
                    cmd.Parameters.AddWithValue("@By", revertedBy);
                    cmd.Parameters.AddWithValue("@At", DateTime.Now);
                    await cmd.ExecuteNonQueryAsync();
                }
                return DbResult<bool>.Ok(true);
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, "RevertAutoExcludeLogAsync");
                return DbResult<bool>.FailWithError(ex.Message);
            }
        }

        /// <summary> Отмечает запись журнала отменённой (само исключение снимает вызывающий). </summary>
        public static async Task<DbResult<bool>> MarkAutoExcludeRevertedAsync(int id, string revertedBy)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(DbResult<bool>.Ok(true));
            const string sql = @"
                UPDATE ai_auto_exclude_log
                SET RevertedBy = @By, RevertedAt = @At
                WHERE Id = @Id AND RevertedAt IS NULL";
            try
            {
                await using var conn = new SqlConnection(DomainSettings.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@By", revertedBy);
                cmd.Parameters.AddWithValue("@At", DateTime.Now);
                await cmd.ExecuteNonQueryAsync();
                return DbResult<bool>.Ok(true);
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, "MarkAutoExcludeRevertedAsync");
                return DbResult<bool>.FailWithError(ex.Message);
            }
        }
    }
}
