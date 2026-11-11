using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static libeLog.Infrastructure.Sql.ApplicationFunctions;

namespace libeLog.Infrastructure.Sql
{
    public static class SqlSchemaBootstrapper
    {

        /// <summary>
        /// Применяет определение всех таблиц в базу данных. Создаёт отсутствующие таблицы, добавляет недостающие столбцы и ограничения.
        /// Безопасно вызывается повторно: если структура уже соответствует, ничего не делает.
        /// </summary>
        /// <param name="connection">Открытое соединение с SQL Server.</param>
        /// <param name="progress">Опциональный вывод прогресса.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        public static async Task ApplyAllAsync(string connectionString, SqlConnection connection, IProgress<(string, Status?)>? progress = null, CancellationToken cancellationToken = default)
        {
            var fnBuilder = new FunctionSqlBuilder();
            var manager = new FunctionManager(connectionString, fnBuilder);

            manager.Register(new FunctionDefinition
            {
                Name = "NormalizedPartNameWithoutComments",
                Parameters = new List<FunctionParameter>
                {
                    new("@name", "NVARCHAR(255)")
                },
                ReturnType = "NVARCHAR(255)",
                Options = new List<string> { "RETURNS NULL ON NULL INPUT", "SCHEMABINDING" },
                Body = @"
                    IF @name IS NULL OR LEN(LTRIM(RTRIM(@name))) = 0
                        RETURN '';

                    DECLARE @r NVARCHAR(255) = @name;

                    WHILE CHARINDEX('(', @r) > 0
                    BEGIN
                        DECLARE @s INT = CHARINDEX('(', @r);
                        DECLARE @e INT = CHARINDEX(')', @r, @s);
                        IF @e > 0
                            SET @r = STUFF(@r, @s, @e - @s + 1, '');
                        ELSE
                            BREAK;
                    END

                    SET @r = REPLACE(@r, '""', '');

                    WHILE CHARINDEX('  ', @r) > 0
                        SET @r = REPLACE(@r, '  ', ' ');

                    RETURN LOWER(LTRIM(RTRIM(@r)));"
            });
            manager.DeployAll();
            progress?.Report(($"Функции:{string.Join("\n", manager.Functions)}", Status.Ok));
            var tables = GetAllTableDefinitions();
            foreach (var table in tables)
            {
                await ApplyAsync(connectionString, connection, table, progress, cancellationToken);
            }
        }

        public static async Task ApplyAsync(
            string connectionString,
            SqlConnection connection,
            TableDefinition table,
            IProgress<(string, Status?)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var helper = new SqlSchemaDiffHelper(connectionString);
            progress?.Report(($"Таблица: {table.Name}", Status.Sync));

            await helper.ApplyMissingColumnsAndConstraintsAsync(table, progress, cancellationToken);

            if (table.Indexes.Count > 0)
            {
                string indexScript = SqlSchemaAlterHelper.GenerateAddIndexesScript(table.Name, table.Indexes);
                if (!string.IsNullOrWhiteSpace(indexScript))
                {
                    await ExecuteRawAsync(connection, indexScript, cancellationToken);
                    progress?.Report(($"Индексы таблицы {table.Name}", Status.Ok));
                }
            }

            if (table.DefaultRows.Count > 0)
            {
                await SeedDefaultRowsIfEmptyAsync(connection, table, progress, cancellationToken);
            }

            progress?.Report(($"Таблица: {table.Name}", Status.Ok));
        }

        /// <summary>
        /// Вставляет строки справочника (заданные через <see cref="TableBuilder.AddDefaultRow"/>) в таблицу,
        /// если она пуста (seed-if-empty). Идемпотентно: заполненные деплоером таблицы не трогает —
        /// собственные причины не перезаписываются, свобода настройки сохраняется.
        /// </summary>
        private static async Task SeedDefaultRowsIfEmptyAsync(
            SqlConnection connection,
            TableDefinition table,
            IProgress<(string, Status?)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            bool wasOpen = connection.State == System.Data.ConnectionState.Open;
            if (!wasOpen)
                await connection.OpenAsync(cancellationToken);

            try
            {
                using (var countCommand = connection.CreateCommand())
                {
                    countCommand.CommandText = $"SELECT COUNT(*) FROM {SqlValidationHelper.EscapeName(table.Name)}";
                    var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
                    if (count > 0)
                        return;
                }

                foreach (var row in table.DefaultRows)
                {
                    var columns = row.Keys.ToList();
                    var sql = $"INSERT INTO {SqlValidationHelper.EscapeName(table.Name)} " +
                              $"({string.Join(", ", columns.Select(SqlValidationHelper.EscapeName))}) " +
                              $"VALUES ({string.Join(", ", columns.Select((_, i) => $"@p{i}"))})";

                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    for (int i = 0; i < columns.Count; i++)
                    {
                        var value = row[columns[i]];
                        command.Parameters.AddWithValue($"@p{i}", value ?? DBNull.Value);
                    }
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                progress?.Report(($"    | Заполнение справочника «{table.Name}» значениями по умолчанию ({table.DefaultRows.Count} строк)", Status.Ok));
            }
            finally
            {
                if (!wasOpen)
                    connection.Close();
            }
        }

        /// <summary>
        /// Выполняет произвольный DDL/DML-скрипт через SqlCommand
        /// Скрипт разбивается на батчи по разделителю GO (как SSMS).
        /// Удобно для индексов, представлений и прочего, что не описывается через TableBuilder.
        /// </summary>
        /// <param name="connection">Соединение с базой данных (открывать не нужно — метод управляет состоянием).</param>
        /// <param name="sql">Скрипт, возможно многобатчевый (с GO).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        public static async Task ExecuteRawAsync(
            SqlConnection connection,
            string sql,
            CancellationToken cancellationToken = default)
        {
            var batches = Regex.Split(sql, @"^\s*GO\s*($|--.*)", RegexOptions.Multiline | RegexOptions.IgnoreCase);

            bool wasOpen = connection.State == System.Data.ConnectionState.Open;
            if (!wasOpen)
                await connection.OpenAsync(cancellationToken);

            try
            {
                foreach (var batch in batches)
                {
                    string text = batch.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = text;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            finally
            {
                if (!wasOpen)
                    connection.Close();
            }
        }

        /// <summary>
        /// Возвращает все таблицы приложения, описанные через TableBuilder.
        /// </summary>
        public static List<TableDefinition> GetAllTableDefinitions() => new()
        {
            new TableBuilder("qc_inspections")
                .AddIdColumn()
                .AddStringColumn("part_name", 255, false)
                .AddStringColumn("order_number", 100, false)
                .AddStringColumn("parts_count", 50)
                .AddDateTimeColumn("started_at")
                .AddDateTimeColumn("completed_at")
                .AddStringColumn("result", 20)
                .AddStringColumn("operator", 100, false, "SUSER_SNAME()")
                .AddStringColumn("comment", -1, true)
                .AddIndex(
                    columns: new[] { "part_name", "order_number" },
                    filter:  "completed_at IS NULL",
                    name:    "IX_qc_inspections_active")
                .Build(),

            new TableBuilder("qc_users")
                .AddIdColumn()
                .AddStringColumn("Code1C", 255, false)
                .AddCompositeUnique("Code1C")
                .AddStringColumn("FirstName", 50, false)
                .AddStringColumn("LastName", 50, false)
                .AddStringColumn("Patronymic", 50)
                .AddBoolColumn("IsAdministrator", false, false)
                .Build(),

            new TableBuilder("cnc_deviation_reasons")
                .AddIdColumn()
                .AddStringColumn("Reason", -1, false)
                .AddNCharColumn("Type", 9)
                .AddBoolColumn("RequireComment", false)
                // Причины из комбобоксов MasterSetupComment/MasterMachiningComment — КОНТРАКТ для
                // валидации (remeLog.Core/Models/Part.cs), хард-правил (AiService HardRule.cs) и
                // промптов (AiService/prompts/*). Сидятся только если таблица пуста; при
                // переименовании причины в справочнике её нужно поправить и в этих местах.
                .AddDefaultRow(new { Reason = "", Type = (string?)null, RequireComment = false })
                .AddDefaultRow(new { Reason = "Другое", Type = (string?)null, RequireComment = true })
                // Только наладка: норматив изготовления отсутствовать не может (регламент, п. 4.2).
                .AddDefaultRow(new { Reason = "Отсутствие нормативов", Type = (string?)"Setup", RequireComment = false })
                .AddDefaultRow(new { Reason = "Некорректные нормативы", Type = (string?)null, RequireComment = false })
                .AddDefaultRow(new { Reason = "Неопытный оператор", Type = (string?)null, RequireComment = false })
                .AddDefaultRow(new { Reason = "Работа ученика", Type = (string?)null, RequireComment = false })
                .AddDefaultRow(new { Reason = "Небрежное отношение к работе", Type = (string?)null, RequireComment = true })
                .AddDefaultRow(new { Reason = "Некорректное заполнение", Type = (string?)null, RequireComment = false })
                .AddDefaultRow(new { Reason = "Освоение", Type = "Setup", RequireComment = false })
                .AddDefaultRow(new { Reason = "Особенности изготовления", Type = (string?)null, RequireComment = true })
                .AddDefaultRow(new { Reason = "Штучная/длительная работа", Type = "Machining", RequireComment = false })
                .AddDefaultRow(new { Reason = "Изготовление не по техпроцессу", Type = (string?)null, RequireComment = true })
                .AddDefaultRow(new { Reason = "Изготовление типовой детали", Type = "Setup", RequireComment = false })
                .AddDefaultRow(new { Reason = "Разовое изменение времени из-за проблем с инструментом/оборудованием", Type = "Machining", RequireComment = true })
                .AddDefaultRow(new { Reason = "Несоответствующие заготовки", Type = "Machining", RequireComment = false })
                .AddDefaultRow(new { Reason = "Двустаночное обслуживание", Type = (string?)null, RequireComment = false })
                .AddDefaultRow(new { Reason = "Доработка", Type = (string?)null, RequireComment = true })
                .Build(),

            new TableBuilder("cnc_downtime_reasons")
                .AddIdColumn()
                .AddStringColumn("Reason", 50, false)
                // Набор фиксирован: по этим причинам сводные отчёты считают отдельные
                // колонки (см. DowntimeReasons).
                .AddDefaultRow(new { Reason = DowntimeReasons.None })
                .AddDefaultRow(new { Reason = DowntimeReasons.NoOperator })
                .AddDefaultRow(new { Reason = DowntimeReasons.HardwareRepair })
                .AddDefaultRow(new { Reason = DowntimeReasons.NoPower })
                .AddDefaultRow(new { Reason = DowntimeReasons.ProcessRelatedLoss })
                .AddDefaultRow(new { Reason = DowntimeReasons.Other })
                .Build(),

            new TableBuilder("cnc_elog_config")
                .AddIdColumn()
                .AddStringColumn("SearchToolTypes", 50)
                .AddStringColumn("UpdatePath", -1)
                .AddStringColumn("LogPath", -1)
                .AddStringColumn("OrderPrefixes", -1)
                .AddStringColumn("AssignedPartsGsId")
                .AddStringColumn("OrdersXlPath")
                .Build(),

            new TableBuilder("cnc_machines")
                .AddIdColumn()
                .AddStringColumn("Name", 50, false)
                .AddBoolColumn("IsActive", false)
                .AddBoolColumn("IsSerial", false, false)
                .AddStringColumn("Type", 50, false)
                .AddIntColumn("SetupLimit", false)
                .AddDoubleColumn("SetupCoefficient", false)
                // Порог доли простоев для К1 (доля, 0.10 = 10%): меньше порога — коэффициент простоев 1.
                .AddDoubleColumn("DowntimeLimit", false, 0.10)
                // Профиль промпта для ИИ-анализа: AiService подставляет
                // prompts/system_prompt.{профиль}.txt; NULL — базовый промпт.
                .AddStringColumn("AiPromptProfile", 50)
                .AddIntColumn("WnId", false)
                .AddGuidColumn("WnUuid", false, false)
                .AddStringColumn("WnCounterSignal", 8, true)
                .AddStringColumn("WnNcNameSignal", 8, true)
                .AddStringColumn("WnNcPartNameSignal", 8, true)
                .AddStringColumn("WnCurrentCSSignal", 8, true)
                .AddStringColumn("WnCurrentPlaneSignal", 8, true)
                .AddStringColumn("WnCurrentBlockNumberSignal", 8, true)
                .AddStringColumn("WnCurrentBlockTextSignal", 8, true)
                .AddStringColumn("WnNcModeSignal", 8, true)
                .AddStringColumn("WnFeedHoldSignal", 8, true)
                .AddStringColumn("WnSBKSignal", 8, true)
                .AddStringColumn("WnDryRunSignal", 8, true)
                .AddStringColumn("WnMSTLKSignal", 8, true)
                .AddStringColumn("WnMLKSignal", 8, true)
                .AddStringColumn("WnRapidMultiplierSignal", 8, true)
                .AddStringColumn("WnFeedMultiplierSignal", 8, true)
                .AddStringColumn("WnSpindleSpeed1MultiplierSignal", 8, true)
                .AddStringColumn("WnSpindleSpeed2MultiplierSignal", 8, true)
                .AddStringColumn("WnProgramRunningSignal", 8, true)
                .AddStringColumn("WnActSpindle1SpeedSignal", 8, true)
                .AddStringColumn("WnActSpindle2SpeedSignal", 8, true)
                .AddStringColumn("WnActCutSpeedSignal", 8, true)
                .AddStringColumn("WnActFeedPerMinSignal", 8, true)
                .AddStringColumn("WnActFeedPerRevSignal", 8, true)
                .AddStringColumn("WnStopSignal", 8, true)
                .AddStringColumn("WnOpStopSignal", 8, true)
                .AddStringColumn("WnMcodeSignal", 8, true)
                .AddStringColumn("WnGMoveSignal", 8, true)
                .AddStringColumn("WnGCycleSignal", 8, true)
                .AddStringColumn("WnGcodeSignal", 8, true)
                .AddStringColumn("WnAbsXSignal", 8, true)
                .AddStringColumn("WnAbsYSignal", 8, true)
                .AddStringColumn("WnAbsZSignal", 8, true)
                .AddStringColumn("WnAbsZASignal", 8, true)
                .AddStringColumn("WnAbsASignal", 8, true)
                .AddStringColumn("WnAbsBSignal", 8, true)
                .AddStringColumn("WnAbsCSignal", 8, true)
                .AddStringColumn("WnAbsWSignal", 8, true)
                .AddStringColumn("WnRelXSignal", 8, true)
                .AddStringColumn("WnRelYSignal", 8, true)
                .AddStringColumn("WnRelZSignal", 8, true)
                .AddStringColumn("WnRelZASignal", 8, true)
                .AddStringColumn("WnRelASignal", 8, true)
                .AddStringColumn("WnRelBSignal", 8, true)
                .AddStringColumn("WnRelCSignal", 8, true)
                .AddStringColumn("WnRelWSignal", 8, true)
                .AddStringColumn("WnMachXSignal", 8, true)
                .AddStringColumn("WnMachYSignal", 8, true)
                .AddStringColumn("WnMachZSignal", 8, true)
                .AddStringColumn("WnMachZASignal", 8, true)
                .AddStringColumn("WnMachASignal", 8, true)
                .AddStringColumn("WnMachBSignal", 8, true)
                .AddStringColumn("WnMachCSignal", 8, true)
                .AddStringColumn("WnMachWSignal", 8, true)
                .AddStringColumn("WnToolSignal", 8, true)
                .AddStringColumn("WnToolHSignal", 8, true)
                .AddStringColumn("WnToolDSignal", 8, true)
                .AddStringColumn("WnToolVectorSignal", 8, true)
                .AddStringColumn("WnToolGeomRSignal", 8, true)
                .AddStringColumn("WnToolGeomXSignal", 8, true)
                .AddStringColumn("WnToolGeomYSignal", 8, true)
                .AddStringColumn("WnToolGeomZSignal", 8, true)
                .AddStringColumn("WnToolWearRSignal", 8, true)
                .AddStringColumn("WnToolWearXSignal", 8, true)
                .AddStringColumn("WnToolWearYSignal", 8, true)
                .AddStringColumn("WnToolWearZSignal", 8, true)
                .AddStringColumn("WnAlarmMessageSignal", 8, true)
                .AddStringColumn("WnLoadXSignal", 8, true)
                .AddStringColumn("WnLoadYSignal", 8, true)
                .AddStringColumn("WnLoadZSignal", 8, true)
                .AddStringColumn("WnLoadCSignal", 8, true)
                .AddStringColumn("WnLoadBSignal", 8, true)
                .AddStringColumn("WnLoadWSignal", 8, true)
                .AddStringColumn("WnLoadSpindle1Signal", 8, true)
                .AddStringColumn("WnLoadSpindle2Signal", 8, true)
                .AddStringColumn("WnG54X", 8, true)
                .AddStringColumn("WnG54Y", 8, true)
                .AddStringColumn("WnG54Z", 8, true)
                .AddStringColumn("WnG54A", 8, true)
                .AddStringColumn("WnG54B", 8, true)
                .AddStringColumn("WnG54C", 8, true)
                .AddStringColumn("WnG54W", 8, true)
                .AddStringColumn("WnG54ZA", 8, true)
                .AddStringColumn("WnG55X", 8, true)
                .AddStringColumn("WnG55Y", 8, true)
                .AddStringColumn("WnG55Z", 8, true)
                .AddStringColumn("WnG55A", 8, true)
                .AddStringColumn("WnG55B", 8, true)
                .AddStringColumn("WnG55C", 8, true)
                .AddStringColumn("WnG55W", 8, true)
                .AddStringColumn("WnG55ZA", 8, true)
                .AddStringColumn("WnG56X", 8, true)
                .AddStringColumn("WnG56Y", 8, true)
                .AddStringColumn("WnG56Z", 8, true)
                .AddStringColumn("WnG56A", 8, true)
                .AddStringColumn("WnG56B", 8, true)
                .AddStringColumn("WnG56C", 8, true)
                .AddStringColumn("WnG56W", 8, true)
                .AddStringColumn("WnG56ZA", 8, true)
                .AddStringColumn("WnG57X", 8, true)
                .AddStringColumn("WnG57Y", 8, true)
                .AddStringColumn("WnG57Z", 8, true)
                .AddStringColumn("WnG57A", 8, true)
                .AddStringColumn("WnG57B", 8, true)
                .AddStringColumn("WnG57C", 8, true)
                .AddStringColumn("WnG57W", 8, true)
                .AddStringColumn("WnG57ZA", 8, true)
                .AddStringColumn("WnG58X", 8, true)
                .AddStringColumn("WnG58Y", 8, true)
                .AddStringColumn("WnG58Z", 8, true)
                .AddStringColumn("WnG58A", 8, true)
                .AddStringColumn("WnG58B", 8, true)
                .AddStringColumn("WnG58C", 8, true)
                .AddStringColumn("WnG58W", 8, true)
                .AddStringColumn("WnG58ZA", 8, true)
                .AddStringColumn("WnG59X", 8, true)
                .AddStringColumn("WnG59Y", 8, true)
                .AddStringColumn("WnG59Z", 8, true)
                .AddStringColumn("WnG59A", 8, true)
                .AddStringColumn("WnG59B", 8, true)
                .AddStringColumn("WnG59C", 8, true)
                .AddStringColumn("WnG59W", 8, true)
                .AddStringColumn("WnG59ZA", 8, true)
                .Build(),

            new TableBuilder("cnc_operators")
                .AddIdColumn()
                .AddStringColumn("FirstName", 50, false)
                .AddStringColumn("LastName", 50, false)
                .AddStringColumn("Patronymic", 50)
                .AddIntColumn("Qualification", false)
                .AddBoolColumn("IsActive", false)
                .Build(),

            new TableBuilder("cnc_qualifications")
                .AddIdColumn("Qualification")
                .AddDoubleColumn("EfficiencyValueHH", false, 1.05)
                .AddDoubleColumn("EfficiencyCoefficientHH", false, 1.4)
                .AddDoubleColumn("EfficiencyValueH", false, 0.95)
                .AddDoubleColumn("EfficiencyCoefficientH", false, 1.2)
                .AddDoubleColumn("EfficiencyValueN", false, 0.85)
                .AddDoubleColumn("EfficiencyCoefficientN", false, 1.0)
                .AddDoubleColumn("EfficiencyValueL", false, 0.75)
                .AddDoubleColumn("EfficiencyCoefficientL", false, 0.8)
                .AddDoubleColumn("EfficiencyValueLL", false, 0.65)
                .AddDoubleColumn("EfficiencyCoefficientLL", false, 0.6)
                .AddDoubleColumn("EfficiencyValueLLL", false, 0)
                .AddDoubleColumn("EfficiencyCoefficientLLL", false, 0.5)
                .AddDoubleColumn("DownTimesValueHH", false, 0.05)
                .AddDoubleColumn("DownTimesCoefficientHH", false, 1.2)
                .AddDoubleColumn("DownTimesValueH", false, 0.15)
                .AddDoubleColumn("DownTimesCoefficientH", false, 1.1)
                .AddDoubleColumn("DownTimesValueN", false, 0.2)
                .AddDoubleColumn("DownTimesCoefficientN", false, 1)
                .AddDoubleColumn("DownTimesValueL", false, 0.25)
                .AddDoubleColumn("DownTimesCoefficientL", false, 0.8)
                .AddDoubleColumn("DownTimesValueLL", false, 0.3)
                .AddDoubleColumn("DownTimesCoefficientLL", false, 0.8)
                .AddDoubleColumn("DownTimesValueLLL", false, 1)
                .AddDoubleColumn("DownTimesCoefficientLLL", false, .5)
                .AddDoubleColumn("NonSerialEfficiencyValueHH", false, 1)
                .AddDoubleColumn("NonSerialEfficiencyCoefficientHH", false, 1.4)
                .AddDoubleColumn("NonSerialEfficiencyValueH", false, 0.9)
                .AddDoubleColumn("NonSerialEfficiencyCoefficientH", false, 1.2)
                .AddDoubleColumn("NonSerialEfficiencyValueN", false, 0.8)
                .AddDoubleColumn("NonSerialEfficiencyCoefficientN", false, 1.0)
                .AddDoubleColumn("NonSerialEfficiencyValueL", false, 0.7)
                .AddDoubleColumn("NonSerialEfficiencyCoefficientL", false, 0.8)
                .AddDoubleColumn("NonSerialEfficiencyValueLL", false, 0.6)
                .AddDoubleColumn("NonSerialEfficiencyCoefficientLL", false, 0.6)
                .AddDoubleColumn("NonSerialEfficiencyValueLLL", false, 0)
                .AddDoubleColumn("NonSerialEfficiencyCoefficientLLL", false, 0.5)
                .Build(),

            new TableBuilder("cnc_remelog_config")
                .AddIdColumn()
                .AddDoubleColumn("max_setup_limit")
                .AddDoubleColumn("long_setup_limit")
                .AddStringColumn("NcArchivePath")
                .AddStringColumn("NcIntermediatePath")
                .AddStringColumn("CncOperations")
                .AddStringColumn("Administrators")
                .AddSmallDateTimeColumn("Holidays")
                .AddStringColumn("EngineerComments")
                .AddStringColumn("AiIp")
                .AddStringColumn("AiModel")
                .AddIntColumn("SchemaVersion", false, 0)
                .Build(),

            new TableBuilder("cnc_remelog_feature_permissions")
                .AddIdColumn()
                .AddStringColumn("UserName", 255, false)
                .AddIntColumn("EnabledFeatures", false, 0)
                .AddCompositeUnique("UserName")
                .Build(),

            new TableBuilder("cnc_serial_parts")
                .AddIdColumn()
                .AddStringColumn("PartName", 255, false)
                .AddCompositeUnique("PartName")
                .AddIntColumn("YearCount", false)
                .AddComputedColumn("NormalizedPartName", "dbo.NormalizedPartNameWithoutComments(PartName)")
                .Build(),

            new TableBuilder("cnc_operations")
                .AddIdColumn()
                .AddIntColumn("SerialPartId", false)
                .AddStringColumn("Name", 255, false)
                .AddCompositeUnique("Name", "SerialPartId")
                .AddForeignKey("SerialPartId", "cnc_serial_parts", "Id", ForeignKeyAction.Cascade, ForeignKeyAction.Cascade)
                .AddIntColumn("OrderIndex", false, 0)
                .Build(),

            new TableBuilder("cnc_setups")
                .AddIdColumn()
                .AddIntColumn("CncOperationId", false)
                .AddByteColumn("Number", false)
                .AddCompositeUnique("CncOperationId", "Number")
                .AddForeignKey("CncOperationId", "cnc_operations", "Id", ForeignKeyAction.Cascade, ForeignKeyAction.Cascade)
                .Build(),

            new TableBuilder("cnc_normatives")
                .AddIdColumn()
                .AddIntColumn("CncSetupId", false)
                .AddByteColumn("NormativeType", false)
                .AddDoubleColumn("Value", false)
                .AddSmallDateTimeColumn("EffectiveFrom", false, "GETDATE()")
                .AddForeignKey("CncSetupId", "cnc_setups", "Id", ForeignKeyAction.Cascade, ForeignKeyAction.Cascade)
                .AddBoolColumn("IsAproved", false, false)
                .Build(),
            
            new TableBuilder("cnc_shifts")
                .AddIdColumn()
                .AddSmallDateTimeColumn("ShiftDate", false)
                .AddNCharColumn("Shift", 4, false)
                .AddStringColumn("Machine", -1, false)
                .AddStringColumn("Master", -1, false)
                .AddDoubleColumn("UnspecifiedDowntimes", false)
                .AddStringColumn("DowntimesComment", -1, false)
                .AddStringColumn("CommonComment", -1, false)
                .AddBoolColumn("IsChecked", false)
                .AddBoolColumn("GiverWorkplaceCleaned")
                .AddBoolColumn("GiverFailures")
                .AddBoolColumn("GiverExtraneousNoises")
                .AddBoolColumn("GiverLiquidLeaks")
                .AddBoolColumn("GiverToolBreakage")
                .AddDoubleColumn("GiverCoolantConcentration")
                .AddBoolColumn("RecieverWorkplaceCleaned")
                .AddBoolColumn("RecieverFailures")
                .AddBoolColumn("RecieverExtraneousNoises")
                .AddBoolColumn("RecieverLiquidLeaks")
                .AddBoolColumn("RecieverToolBreakage")
                .AddDoubleColumn("RecieverCoolantConcentration")
                // Операторы смены строкой через «, » (может быть несколько).
                // В КОНЦЕ таблицы: чтение cnc_shifts идёт SELECT * с позиционными
                // индексами 0…20 — сдвигать существующие колонки нельзя.
                .AddStringColumn("Operators", -1, nullable: true)
                .Build(),

            new TableBuilder("cnc_tool_search_cases")
                .AddIdColumn()
                .AddGuidColumn("PartGuid", nullable: false)
                .AddStringColumn("ToolType", 50, false)
                .AddStringColumn("Value", -1, false)
                .AddSmallDateTimeColumn("StartTime", false)
                .AddSmallDateTimeColumn("EndTime", false)
                .AddBoolColumn("IsSuccess")
                .AddForeignKey("PartGuid", "parts", "Guid", ForeignKeyAction.Cascade, ForeignKeyAction.Cascade)
                .Build(),

            new TableBuilder("cnc_wnc_cfg")
                .AddStringColumn("Server", 50, false)
                .AddStringColumn("User", 50, false)
                .AddStringColumn("Pass", 50, false)
                .AddStringColumn("LocalType", 50, false)
                .Build(),

            new TableBuilder("cnc_winnum_cfg")
                .AddIdColumn()
                .AddStringColumn("BaseUri", 255, false)
                .AddStringColumn("User", 50, false)
                .AddStringColumn("Pass", 50, false)
                .AddStringColumn("NcProgramFolder")
                .Build(),

            new TableBuilder("masters")
                .AddIdColumn()
                .AddStringColumn("FullName", -1, false)
                .AddBoolColumn("IsActive", false)
                .Build(),

            new TableBuilder("parts")
                .AddGuidColumn("Guid", isPrimaryKey: true)
                .AddStringColumn("Machine", -1, false)
                .AddNCharColumn("Shift", 4, false)
                .AddSmallDateTimeColumn("ShiftDate", false)
                .AddStringColumn("Operator", -1, false)
                .AddStringColumn("PartName", -1, false)
                .AddComputedColumn("NormalizedPartName", "dbo.NormalizedPartNameWithoutComments(PartName)")
                .AddStringColumn("Order", 50, false)
                .AddIntColumn("Setup", false)
                .AddDoubleColumn("FinishedCount", false)
                .AddIntColumn("DefectiveCount", false, 0)
                .AddIntColumn("TotalCount", false)
                .AddSmallDateTimeColumn("StartSetupTime", false)
                .AddSmallDateTimeColumn("StartMachiningTime", false)
                .AddDoubleColumn("SetupTimeFact", false)
                .AddSmallDateTimeColumn("EndMachiningTime", false)
                .AddDoubleColumn("SetupTimePlan", false)
                .AddDoubleColumn("SetupTimePlanForReport", false)
                .AddDoubleColumn("SingleProductionTimePlan", false)
                .AddDoubleColumn("ProductionTimeFact", false)
                .AddSqlServerColumn("MachiningTime", "BIGINT", o => o.Nullable(false))
                .AddDoubleColumn("SetupDowntimes", false)
                .AddDoubleColumn("MachiningDowntimes", false)
                .AddDoubleColumn("PartialSetupTime", false)
                .AddDoubleColumn("CreateNcProgramTime", false)
                .AddDoubleColumn("MaintenanceTime", false)
                .AddDoubleColumn("ToolSearchingTime", false)
                .AddDoubleColumn("ToolChangingTime", false)
                .AddDoubleColumn("MentoringTime", false)
                .AddDoubleColumn("ContactingDepartmentsTime", false)
                .AddDoubleColumn("FixtureMakingTime", false)
                .AddDoubleColumn("HardwareFailureTime", false)
                .AddStringColumn("OperatorComment", -1, false)
                .AddStringColumn("MasterSetupComment", -1)
                .AddStringColumn("MasterMachiningComment", -1)
                .AddStringColumn("SpecifiedDowntimesComment", -1)
                .AddStringColumn("UnspecifiedDowntimeComment", -1)
                .AddStringColumn("MasterComment", -1)
                .AddStringColumn("MasterSetupDetail", -1)
                .AddStringColumn("MasterMachiningDetail", -1)
                .AddDoubleColumn("FixedSetupTimePlan")
                .AddDoubleColumn("FixedProductionTimePlan")
                .AddStringColumn("EngineerConclusion", -1)
                .AddStringColumn("EngineerComment", -1)
                .AddBoolColumn("ExcludeFromReports")
                .AddStringColumn("LongSetupReasonComment", -1)
                .AddStringColumn("LongSetupFixComment", -1)
                .AddStringColumn("LongSetupEngeneerComment", -1)
                .AddDoubleColumn("ExcludedOperationsTime")
                .AddStringColumn("IncreaseReason", -1)
                .AddDoubleColumn("SpecialDowntimeTime", false, 0)
                // Переопределение причин аналитиком (СГТ 1). Поля мастера
                // (MasterSetupComment/MasterMachiningComment) после заполнения мастером не
                // правятся — решение аналитика живёт здесь, параллельным слоем. Эффективная
                // причина = override ?? отметка мастера, считается в C# (Part.Effective*Reason),
                // computed-колонки намеренно не используются: persisted computed при
                // пересоздании уезжает в конец таблицы и сдвигает ordinal'ы (см. PartsDb.cs).
                // IsMasterFault по умолчанию false — сам факт переопределения ещё не значит, что
                // мастер виноват; аналитик ставит флаг, когда у мастера была возможность выбрать
                // верно, и опционально поясняет ошибку в MasterFaultComment.
                // By/At — общие на запись: обе категории правятся в одну сессию разбора.
                .AddStringColumn("SetupReasonOverride", -1)
                .AddStringColumn("SetupReasonOverrideComment", -1)
                .AddBoolColumn("SetupReasonOverrideIsMasterFault", false, false)
                .AddStringColumn("SetupReasonOverrideMasterFaultComment", -1)
                .AddStringColumn("MachiningReasonOverride", -1)
                .AddStringColumn("MachiningReasonOverrideComment", -1)
                .AddBoolColumn("MachiningReasonOverrideIsMasterFault", false, false)
                .AddStringColumn("MachiningReasonOverrideMasterFaultComment", -1)
                .AddStringColumn("ReasonOverrideBy", 128)
                .AddDateTimeColumn("ReasonOverrideAt")
                .Build(),

            new TableBuilder("remeLog_app_presence")
                .AddGuidColumn("SessionId", true, false)
                .AddStringColumn("Application", 64, false)
                .AddStringColumn("MachineName", 128, false)
                .AddStringColumn("UserName", 128, false)
                .AddStringColumn("DisplayName", 128)
                .AddStringColumn("Status", 32, false, "'Online'")
                .AddStringColumn("AppVersion", 32)
                .AddStringColumn("IpAddress", 45)
                .AddIntColumn("EnabledFeatures", false, 0)
                .AddDateTimeColumn("StartedUtc", false)
                .AddDateTimeColumn("LastSeenUtc", false)
                .AddIndex(new[] { "LastSeenUtc" }, name: "IX_app_presence_alive")
                .AddIndex(
                    new[] { "MachineName", "UserName", "LastSeenUtc" },
                    name: "IX_app_presence_active_instances",
                    include: new[] { "SessionId", "AppVersion", "StartedUtc" })
                .Build(),

            new TableBuilder("remeLog_app_commands")
                .AddGuidColumn("Id", true, false)
                .AddGuidColumn("TargetSessionId")
                .AddStringColumn("TargetApplication", 64)
                .AddStringColumn("TargetMachine", 128, false)
                .AddStringColumn("TargetUser", 128)
                .AddStringColumn("SenderMachine", 128, false)
                .AddStringColumn("SenderUser", 128, false)
                .AddStringColumn("CommandType", 64, false)
                .AddStringColumn("Payload", -1)
                .AddDateTimeColumn("CreatedUtc", false)
                .AddDateTimeColumn("ProcessedUtc")
                .AddStringColumn("Result", -1)

                .AddIndex(
                    columns: new[] { "TargetSessionId", "CreatedUtc" },
                    include: new[] { "CommandType", "Payload", "TargetMachine" },
                    filter: "ProcessedUtc IS NULL",
                    name: "IX_app_commands_poll"
                )
                .AddIndex(
                    columns: new[] { "TargetSessionId", "ProcessedUtc" },
                    name: "IX_app_commands_session"
                )
                .AddIndex(
                    columns: new[] { "CreatedUtc" },
                    name: "IX_app_commands_cleanup"
                )
                .Build(),

            // Лог каждого обращения к Windchill REST API (см. remeLog.Core/Models/WindchillClient.cs).
            // Windchill видит только один общий сервисный логин (cnc_wnc_cfg.User) для всех
            // пользователей remeLog, поэтому "кто именно" фиксируется здесь, на стороне remeLog —
            // по Windows-логину/машине инициатора, а не по учётке Windchill. Нужен для
            // диагностики нагрузки/троттлинга на сервере Windchill: сопоставить время жалоб
            // пользователей с фактическими запросами (см. Database.LogWncRequestAsync).
            new TableBuilder("remeLog_wnc_requests")
                .AddIdColumn()
                .AddStringColumn("MachineName", 128, false)
                .AddStringColumn("UserName", 128, false)
                .AddStringColumn("RequestType", 20, false)
                .AddStringColumn("Params", -1)
                // Фактические HTTP-запросы к Windchill за одно действие пользователя, по строке
                // на запрос: "[статус] длительность URL". Одно действие — не всегда один запрос
                // (поиск с cadOnly=false опрашивает два entity set, скачивание PDF — сначала
                // метаданные, потом файл), и по этой колонке видно реальное число обращений.
                // URL самодостаточен: вставив его в браузер/Postman под сервисным логином из
                // cnc_wnc_cfg, получаем ровно тот сырой ответ, который разбирал remeLog — это же
                // можно отдать техподдержке как воспроизводимый пример. Учётных данных в URL нет
                // (Basic-auth уходит заголовком).
                .AddStringColumn("RequestUrls", -1)
                .AddIntColumn("ResultCount")
                .AddBoolColumn("Truncated")
                .AddBoolColumn("Success", false)
                .AddStringColumn("ErrorMessage", -1)
                .AddIntColumn("ElapsedMs", false)
                .AddDateTimeColumn("CreatedUtc", false)
                .AddIndex(new[] { "CreatedUtc" }, name: "IX_wnc_requests_time")
                .Build(),

            // Живой статус станка (наладка/изготовление/простой), который тихо пишет eLog,
            // пока строка ещё не закрыта (SyncParts синхронизирует в parts только завершённые
            // строки). MERGE по Machine — одна строка на станок. UpdatedUtc — heartbeat: если
            // давно не обновлялся, читающая сторона показывает "нет данных", а не "простой".
            new TableBuilder("cnc_machine_activity")
                .AddColumn("Machine", "NVARCHAR(50)", opt => opt.PrimaryKey().Nullable(false))
                // Статус сюда пишется вместе с названием простоя ("Обращение в другие службы [Н]"),
                // поэтому 20 символов не хватало: MERGE падал с усечением и heartbeat не проходил.
                .AddStringColumn("Status", 100, false)
                .AddStringColumn("PartName", -1)
                .AddStringColumn("Order", 50)
                .AddStringColumn("Operator", -1)
                .AddByteColumn("Setup")
                .AddNCharColumn("Shift", 4)
                // Локальное время начала фазы (как StartSetupTime/StartMachiningTime в parts) —
                // в отличие от UpdatedUtc, это не heartbeat, а бизнес-время для отображения.
                .AddDateTimeColumn("PhaseStartLocal")
                .AddDateTimeColumn("UpdatedUtc", false)
                .Build(),

            new TableBuilder("ai_day_reviews")
                .AddIdColumn()
                .AddStringColumn("Machine", 50, false)
                .AddSmallDateTimeColumn("ShiftDate", false)
                .AddCompositeUnique("Machine", "ShiftDate")
                .AddStringColumn("ReviewedBy", 128, false)
                .AddDateTimeColumn("ReviewedAt", false)
                .AddStringColumn("Decision", 20, false)
                .AddBoolColumn("IsFullyReviewed", false, false)
                .AddStringColumn("Comment", -1)
                .AddBoolColumn("AiRequiresReview")
                .AddBoolColumn("AiThinkingEnabled")
                .AddDoubleColumn("AiConfidence")
                .AddStringColumn("AiSignals", -1)
                .AddStringColumn("AiExplanation", -1)
                .AddStringColumn("AiModelVersion", 50)
                .AddStringColumn("AiPromptVersion", 50)
                .AddDateTimeColumn("AiAnalyzedAt")
                .AddStringColumn("AiFeedback", -1)
                .AddComputedColumn("AiVerdict",
                    @"CASE
                        WHEN AiAnalyzedAt IS NULL THEN N'не анализировалось'
                        WHEN (Decision = N'ok' AND AiRequiresReview = 0)
                          OR (Decision = N'escalated' AND AiRequiresReview = 1) THEN N'совпадение'
                        WHEN Decision = N'ok'        AND AiRequiresReview = 1 THEN N'ИИ лишний флаг'
                        WHEN Decision = N'escalated' AND AiRequiresReview = 0 THEN N'ИИ пропустил'
                        ELSE N'?'
                    END",
                    persisted: true)
                // Автозакрытие без человека (этап 2.3, research/ai-service/auto-close-plan.md).
                // AutoClosed — сутки закрыл ИИ сам (ReviewedBy = «ИИ-авто»); метрики
                // «согласие аналитика» должны исключать такие строки. AutoCloseBlockers —
                // причины, по которым сутки НЕ закрыты автоматически (JSON-массив строк).
                // В КОНЦЕ таблицы: порядок существующих столбцов не сдвигаем.
                .AddBoolColumn("AutoClosed", false, false)
                .AddStringColumn("AutoCloseBlockers", -1)
                .AddIndex(new[] { "ShiftDate" }, name: "IX_ai_day_reviews_date")
                .Build(),

            new TableBuilder("ai_part_flags")
                .AddIdColumn()
                .AddIntColumn("DayReviewId", false)
                .AddForeignKey("DayReviewId", "ai_day_reviews", "Id",
                               ForeignKeyAction.Cascade, ForeignKeyAction.Cascade)
                .AddGuidColumn("PartGuid", nullable: false)
                .AddForeignKey("PartGuid", "parts", "Guid",
                               ForeignKeyAction.Cascade, ForeignKeyAction.Cascade)
                .AddBoolColumn("IsCleared", false, false)
                .AddStringColumn("Comment", -1)
                // AI-поля на уровне строки
                .AddBoolColumn("AiRequiresReview")
                .AddDoubleColumn("AiConfidence")
                .AddStringColumn("AiSuggestedReason", 255)
                .AddStringColumn("AiSignals", -1)
                .AddStringColumn("AiExplanation", -1)
                .AddIndex(new[] { "DayReviewId" }, name: "IX_ai_part_flags_day")
                .AddIndex(new[] { "PartGuid" },    name: "IX_ai_part_flags_part")
                .Build(),

            // История слепок времён кнопкой «Слепить детали»: Guid строк без FK,
            // чтобы разбор причин переживал удаление самих деталей.
            new TableBuilder("part_snap_log")
                .AddIdColumn()
                .AddStringColumn("Machine", 50, false)
                .AddSmallDateTimeColumn("ShiftDate", false)
                .AddStringColumn("Shift", 10, false)
                .AddGuidColumn("PrevPartGuid", nullable: false)
                .AddGuidColumn("NextPartGuid", nullable: false)
                .AddStringColumn("NextPartName", 255)
                .AddSmallDateTimeColumn("OldStartSetupTime", false)
                .AddSmallDateTimeColumn("NewStartSetupTime", false)
                .AddIntColumn("GapMinutes", false)
                .AddStringColumn("SnappedBy", 128, false)
                .AddDateTimeColumn("SnappedAt", false)
                .AddIndex(new[] { "Machine", "ShiftDate" }, name: "IX_part_snap_log_machine_date")
                .Build(),

            // Журнал исключений из К1, которые ИИ применил сам при автозакрытии суток.
            // Guid строки без FK — как в part_snap_log: разбор переживает удаление деталей.
            // Reverted* — отмена аналитиком (исключение снято); сама строка не удаляется.
            new TableBuilder("ai_auto_exclude_log")
                .AddIdColumn()
                .AddStringColumn("Machine", 50, false)
                .AddSmallDateTimeColumn("ShiftDate", false)
                .AddGuidColumn("PartGuid", nullable: false)
                .AddStringColumn("PartName", 255)
                .AddIntColumn("Setup", false)
                .AddStringColumn("Order", 50)
                .AddStringColumn("Reason", -1)
                .AddStringColumn("AiPromptVersion", 50)
                .AddStringColumn("ExcludedBy", 128, false)
                .AddDateTimeColumn("ExcludedAt", false)
                .AddStringColumn("RevertedBy", 128)
                .AddDateTimeColumn("RevertedAt")
                .AddIndex(new[] { "Machine", "ShiftDate" }, name: "IX_ai_auto_exclude_log_machine_date")
                .AddIndex(new[] { "PartGuid" }, name: "IX_ai_auto_exclude_log_part")
                .Build(),

            // История повторных ИИ-оценок суток, у которых уже есть живой вердикт (P3.4):
            // пакетная/повторная оценка пишет сюда и НЕ меняет ai_day_reviews.Ai* — иначе
            // вердикт «на момент разбора» теряется, а метрики совпадения искажаются
            // (пакетная переоценка 25.09.2026 перезаписала живые строки).
            new TableBuilder("ai_day_reeval")
                .AddIdColumn()
                .AddIntColumn("DayReviewId", false)
                .AddForeignKey("DayReviewId", "ai_day_reviews", "Id",
                               ForeignKeyAction.Cascade, ForeignKeyAction.Cascade)
                .AddBoolColumn("AiRequiresReview")
                .AddDoubleColumn("AiConfidence")
                .AddStringColumn("AiSignals", -1)
                .AddStringColumn("AiExplanation", -1)
                .AddStringColumn("AiModelVersion", 50)
                .AddStringColumn("AiPromptVersion", 50)
                .AddBoolColumn("AiThinkingEnabled")
                .AddDateTimeColumn("EvaluatedAt", false)
                .AddStringColumn("EvaluatedBy", 128, false)
                .AddIndex(new[] { "DayReviewId" }, name: "IX_ai_day_reeval_day")
                .Build(),

            // Журнал каждого решения автозакрытия (в т.ч. в режиме тени, когда в БД
            // больше ничего не пишется): основа сводки автозакрытия и недельного контроля.
            // Outcome: Closed / WouldClose / Deferred / Manual / Error. Reasons — JSON-массив
            // причин. Control* — выборочный контроль аналитиком закрытых ИИ суток:
            // agree / miss / miss_hard (пропуск hard-класса — критерий остановки, P1.4).
            new TableBuilder("ai_auto_close_log")
                .AddIdColumn()
                .AddStringColumn("Machine", 50, false)
                .AddSmallDateTimeColumn("ShiftDate", false)
                .AddDateTimeColumn("RunAt", false)
                .AddStringColumn("RunBy", 128, false)
                .AddBoolColumn("DryRun", false, false)
                .AddStringColumn("Outcome", 20, false)
                .AddStringColumn("Reasons", -1)
                .AddBoolColumn("AiRequiresReview")
                .AddDoubleColumn("AiConfidence")
                .AddStringColumn("AiPromptVersion", 50)
                .AddIntColumn("ExclusionsApplied", false, 0)
                .AddStringColumn("ControlVerdict", 20)
                .AddStringColumn("ControlBy", 128)
                .AddDateTimeColumn("ControlAt")
                .AddStringColumn("ControlComment", -1)
                .AddIndex(new[] { "ShiftDate" }, name: "IX_ai_auto_close_log_date")
                .AddIndex(new[] { "Machine", "ShiftDate" }, name: "IX_ai_auto_close_log_machine_date")
                .Build(),

            // Последний вердикт ИИ по станко-суткам (не решение, а результат анализа): хранится и для суток,
            // которые ИИ оставил человеку, переживает перезапуск службы и срок хранения request_logs.
            // InputFingerprint — отпечаток отправленных данных, AnalysisVersion — версия анализа службы
            // (сборка, промпты, настройки): вердикт повторно используется, только если совпали оба.
            // ResultJson — полный ответ анализа. Одна строка на станок и дату (перезаписывается).
            new TableBuilder("ai_day_verdicts")
                .AddIdColumn()
                .AddStringColumn("Machine", 50, false)
                .AddSmallDateTimeColumn("ShiftDate", false)
                .AddCompositeUnique("Machine", "ShiftDate")
                .AddDateTimeColumn("AnalyzedAt", false)
                .AddStringColumn("AnalyzedBy", 128, false)
                .AddStringColumn("Source", 20, false)
                .AddStringColumn("InputFingerprint", 64, false)
                .AddStringColumn("AnalysisVersion", 32)
                .AddStringColumn("AiModelVersion", 50)
                .AddBoolColumn("ThinkingEnabled", false, false)
                .AddBoolColumn("RequiresReview")
                .AddDoubleColumn("Confidence")
                .AddStringColumn("ResultJson", -1, false)
                .AddIndex(new[] { "ShiftDate" }, name: "IX_ai_day_verdicts_date")
                .Build(),

        };
    }
}
