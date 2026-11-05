using Microsoft.Data.SqlClient;
using remeLog.Core;
using remeLog.Core.Db;
using remeLog.Core.Extensions;
using remeLog.Core.Services;
using remeLog.Core.Services.Demo;
using remeLog.Infrastructure.Extensions;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Part = remeLog.Models.Part;

namespace remeLog.Infrastructure
{
    public static partial class Database
    {
        public static async Task DeleteSerialPartAsync(int partId, IProgress<string>? progress = null)
        {
            // Демо: серийные детали живут в памяти без Id — удаление через окно недоступно.
            if (DomainSettings.DemoMode)
            {
                progress?.Report("Демо-режим: удаление серийных деталей недоступно.");
                await Task.CompletedTask;
                return;
            }
            const string query = "DELETE FROM cnc_serial_parts WHERE Id = @Id;";

            using var connection = new SqlConnection(DomainSettings.ConnectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@Id", partId);

            progress?.Report($"Удаление детали с Id = {partId} из БД...");
            int rowsAffected = await command.ExecuteNonQueryAsync();

            if (rowsAffected > 0)
            {
                progress?.Report("Деталь успешно удалена.");
            }
            else
            {
                progress?.Report("Деталь с указанным Id не найдена.");
            }
        }

        public async static Task<List<Part>> ReadPartsWithConditions(string conditions, CancellationToken cancellationToken)
        {
            // Демо: прямой SQL недоступен — вызывающая сторона (PartsInfoWindow)
            // в демо ходит через ReadPartsWithCriteria. Запасной путь: все детали.
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.Run(() => DemoStore.GetParts(
                    DateTime.Today.AddDays(-DemoDataFactory.DaysBack), DateTime.Today), cancellationToken);
            }
            List<Part> parts = new();
            await Task.Run(async () =>
            {
                using (SqlConnection connection = new(DomainSettings.ConnectionString))
                {
                    await connection.OpenAsync(cancellationToken);
                    string query = $"SELECT * FROM Parts WHERE {conditions} ORDER BY StartSetupTime ASC;";
                    using (SqlCommand command = new(query, connection))
                    {
                        await FillPartsAsync(parts, command, cancellationToken);
                    }
                }
            }, cancellationToken);
            return parts;
        }

        /// <summary>
        /// Демо-эквивалент <see cref="ReadPartsWithConditions"/>: фильтрация в памяти
        /// по тем же критериям (см. <see cref="DemoStore.QueryParts"/>).
        /// </summary>
        public async static Task<List<Part>> ReadPartsWithCriteria(
            PartsFilterCriteria criteria, CancellationToken cancellationToken) =>
            await Task.Run(() => DemoStore.QueryParts(criteria), cancellationToken);

        public async static Task<ObservableCollection<Part>> ReadPartsByShiftDateAndMachine(DateTime fromDate, DateTime toDate, string machine, CancellationToken cancellationToken)
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.Run(() => new ObservableCollection<Part>(
                    DemoStore.GetParts(fromDate, toDate, machine)), cancellationToken);
            }
            ObservableCollection<Part> parts = new();
            using (SqlConnection connection = new(DomainSettings.ConnectionString))
            {
                connection.Open();

                string query = "SELECT * FROM Parts WHERE ShiftDate BETWEEN @FromDate AND @ToDate AND Machine = @Machine ORDER BY StartSetupTime ASC;";
                using (SqlCommand command = new(query, connection))
                {
                    command.Parameters.AddWithValue("@FromDate", fromDate);
                    command.Parameters.AddWithValue("@ToDate", toDate);
                    command.Parameters.AddWithValue("@Machine", machine);

                    await parts.FillPartsAsync(command, cancellationToken);
                }
            }
            return parts;
        }

        /// <summary>Отметки одного оператора за период по дате смены (включительно).</summary>
        public async static Task<List<Part>> ReadPartsByOperator(string @operator, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.Run(() => DemoStore.GetParts(fromDate, toDate)
                    .Where(p => p.Operator == @operator).ToList(), cancellationToken);
            }
            var parts = new List<Part>();
            using (SqlConnection connection = new(DomainSettings.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                const string query = "SELECT * FROM Parts WHERE ShiftDate BETWEEN @FromDate AND @ToDate AND Operator = @Operator ORDER BY StartSetupTime ASC;";
                using (SqlCommand command = new(query, connection))
                {
                    command.Parameters.AddWithValue("@FromDate", fromDate);
                    command.Parameters.AddWithValue("@ToDate", toDate);
                    command.Parameters.AddWithValue("@Operator", @operator);
                    await FillPartsAsync(parts, command, cancellationToken);
                }
            }
            return parts;
        }

        public async static Task<ObservableCollection<Part>> ReadPartsByShiftDate(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.Run(() => new ObservableCollection<Part>(
                    DemoStore.GetParts(fromDate, toDate)), cancellationToken);
            }
            ObservableCollection<Part> parts = new();
            using (SqlConnection connection = new(DomainSettings.ConnectionString))
            {
                connection.Open();

                string query = "SELECT * FROM Parts WHERE ShiftDate BETWEEN @FromDate AND @ToDate ORDER BY StartSetupTime ASC;";
                using (SqlCommand command = new(query, connection))
                {
                    command.Parameters.AddWithValue("@FromDate", fromDate);
                    command.Parameters.AddWithValue("@ToDate", toDate);

                    await parts.FillPartsAsync(command, cancellationToken);
                }
            }
            return parts;
        }

        public async static Task<ObservableCollection<Part>> ReadPartsByGuids(IEnumerable<Guid> guids, CancellationToken cancellationToken)
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.Run(() => new ObservableCollection<Part>(
                    DemoStore.GetPartsByGuids(guids)), cancellationToken);
            }
            ObservableCollection<Part> parts = new();

            if (guids == null || !guids.Any())
                return parts;

            const int chunkSize = 2000;

            var chunks = guids
                .Select((guid, index) => new { guid, index })
                .GroupBy(x => x.index / chunkSize)
                .Select(g => g.Select(x => x.guid).ToList());

            using var connection = new SqlConnection(DomainSettings.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            foreach (var chunk in chunks)
            {
                var parameters = chunk.Select((_, index) => $"@p{index}").ToArray();
                var query = $"SELECT * FROM Parts WHERE Guid IN ({string.Join(", ", parameters)}) ORDER BY StartSetupTime ASC;";

                using var command = new SqlCommand(query, connection);

                for (int i = 0; i < chunk.Count; i++)
                {
                    command.Parameters.AddWithValue(parameters[i], chunk[i]);
                }

                await parts.FillPartsAsync(command, cancellationToken);
            }

            return parts;
        }

        public async static Task<ObservableCollection<Part>> ReadPartsByPartNameAndOrder(string[] partNames, string[] orders, CancellationToken cancellationToken)
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.Run(() => new ObservableCollection<Part>(DemoStore
                    .GetParts(DateTime.Today.AddDays(-DemoDataFactory.DaysBack), DateTime.Today)
                    .Where(p => partNames.Contains(p.PartName) && orders.Contains(p.Order))
                    .OrderBy(p => p.StartSetupTime)), cancellationToken);
            }
            ObservableCollection<Part> parts = new();
            using (SqlConnection connection = new(DomainSettings.ConnectionString))
            {
                connection.Open();

                string query = "SELECT * FROM Parts WHERE PartName IN ('" + string.Join("','", partNames) + "') AND [Order] IN ('" + string.Join("','", orders) + "')";
                using (SqlCommand command = new(query, connection))
                {
                    await parts.FillPartsAsync(command, cancellationToken);
                }
            }
            return parts;
        }

        public async static Task<DbResult<string>> UpdatePartAsync(this Part part)
        {
            // Демо: сохранение в память, без SQL Server.
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.FromResult(DemoStore.UpsertPart(part));
            }
            try
            {
                using (SqlConnection connection = new(DomainSettings.ConnectionString))
                {
                    await connection.OpenAsync();
                    string updateQuery = "UPDATE Parts SET " +
                        "Machine = @Machine, " +
                        "Shift = @Shift, " +
                        "ShiftDate = @ShiftDate, " +
                        "Operator = @Operator, " +
                        "PartName = @PartName, " +
                        "[Order] = @Order, " +
                        "Setup = @Setup, " +
                        "FinishedCount = @FinishedCount, " +
                        "TotalCount = @TotalCount, " +
                        "StartSetupTime = @StartSetupTime, " +
                        "StartMachiningTime = @StartMachiningTime, " +
                        "EndMachiningTime = @EndMachiningTime, " +
                        "SetupTimeFact = @SetupTimeFact, " +
                        "SetupTimePlan = @SetupTimePlan, " +
                        "SetupTimePlanForReport = @SetupTimePlanForReport, " +
                        "SingleProductionTimePlan = @SingleProductionTimePlan, " +
                        "ProductionTimeFact = @ProductionTimeFact, " +
                        "MachiningTime = @MachiningTime, " +
                        "SetupDowntimes = @SetupDowntimes, " +
                        "MachiningDowntimes = @MachiningDowntimes, " +
                        "PartialSetupTime = @PartialSetupTime, " +
                        "CreateNcProgramTime = @CreateNcProgramTime, " +
                        "MaintenanceTime = @MaintenanceTime, " +
                        "ToolSearchingTime = @ToolSearchingTime, " +
                        "ToolChangingTime = @ToolChangingTime, " +
                        "MentoringTime = @MentoringTime, " +
                        "ContactingDepartmentsTime = @ContactingDepartmentsTime, " +
                        "FixtureMakingTime = @FixtureMakingTime, " +
                        "HardwareFailureTime = @HardwareFailureTime, " +
                        "SpecialDowntimeTime = @SpecialDowntimeTime, " +
                        "OperatorComment = @OperatorComment, " +
                        "MasterSetupComment = @MasterSetupComment, " +
                        "MasterMachiningComment = @MasterMachiningComment, " +
                        "SpecifiedDowntimesComment = @SpecifiedDowntimesComment, " +
                        "UnspecifiedDowntimeComment = @UnspecifiedDowntimeComment, " +
                        "MasterComment = @MasterComment, " +
                        "MasterSetupDetail = @MasterSetupDetail, " +
                        "MasterMachiningDetail = @MasterMachiningDetail, " +
                        "FixedSetupTimePlan = @FixedSetupTimePlan, " +
                        "FixedProductionTimePlan = @FixedProductionTimePlan, " +
                        "EngineerConclusion = @EngineerConclusion, " +
                        "EngineerComment = @EngineerComment, " +
                        "ExcludeFromReports = @ExcludeFromReports, " +
                        "LongSetupReasonComment = @LongSetupReasonComment, " +
                        "LongSetupFixComment = @LongSetupFixComment, " +
                        "LongSetupEngeneerComment = @LongSetupEngeneerComment, " +
                        "ExcludedOperationsTime = @ExcludedOperationsTime, " +
                        "IncreaseReason = @IncreaseReason, " +
                        "SetupReasonOverride = @SetupReasonOverride, " +
                        "SetupReasonOverrideComment = @SetupReasonOverrideComment, " +
                        "SetupReasonOverrideIsMasterFault = @SetupReasonOverrideIsMasterFault, " +
                        "SetupReasonOverrideMasterFaultComment = @SetupReasonOverrideMasterFaultComment, " +
                        "MachiningReasonOverride = @MachiningReasonOverride, " +
                        "MachiningReasonOverrideComment = @MachiningReasonOverrideComment, " +
                        "MachiningReasonOverrideIsMasterFault = @MachiningReasonOverrideIsMasterFault, " +
                        "MachiningReasonOverrideMasterFaultComment = @MachiningReasonOverrideMasterFaultComment, " +
                        "ReasonOverrideBy = @ReasonOverrideBy, " +
                        "ReasonOverrideAt = @ReasonOverrideAt, " +
                        "DefectiveCount = @DefectiveCount " +
                        "WHERE Guid = @Guid";
                    using (SqlCommand cmd = new(updateQuery, connection))
                    {
                        cmd.Parameters.AddWithValue("@Guid", part.Guid);
                        cmd.Parameters.AddWithValue("@Machine", part.Machine);
                        cmd.Parameters.AddWithValue("@Shift", part.Shift);
                        cmd.Parameters.AddWithValue("@ShiftDate", part.ShiftDate);
                        cmd.Parameters.AddWithValue("@Operator", part.Operator);
                        cmd.Parameters.AddWithValue("@PartName", part.PartName);
                        cmd.Parameters.AddWithValue("@Order", part.Order);
                        cmd.Parameters.AddWithValue("@Setup", part.Setup);
                        cmd.Parameters.AddWithValue("@FinishedCount", part.FinishedCount);
                        cmd.Parameters.AddWithValue("@TotalCount", part.TotalCount);
                        cmd.Parameters.AddWithValue("@StartSetupTime", part.StartSetupTime);
                        cmd.Parameters.AddWithValue("@StartMachiningTime", part.StartMachiningTime);
                        cmd.Parameters.AddWithValue("@SetupTimeFact", part.SetupTimeFact);
                        cmd.Parameters.AddWithValue("@EndMachiningTime", part.EndMachiningTime);
                        cmd.Parameters.AddWithValue("@SetupTimePlan", part.SetupTimePlan);
                        cmd.Parameters.AddWithValue("@SetupTimePlanForReport", part.SetupTimePlanForReport);
                        cmd.Parameters.AddWithValue("@SingleProductionTimePlan", part.SingleProductionTimePlan);
                        cmd.Parameters.AddWithValue("@ProductionTimeFact", part.ProductionTimeFact);
                        cmd.Parameters.AddWithValue("@MachiningTime", part.MachiningTime.Ticks);
                        cmd.Parameters.AddWithValue("@SetupDowntimes", part.SetupDowntimes);
                        cmd.Parameters.AddWithValue("@MachiningDowntimes", part.MachiningDowntimes);
                        cmd.Parameters.AddWithValue("@PartialSetupTime", part.PartialSetupTime);
                        cmd.Parameters.AddWithValue("@CreateNcProgramTime", part.CreateNcProgramTime);
                        cmd.Parameters.AddWithValue("@MaintenanceTime", part.MaintenanceTime);
                        cmd.Parameters.AddWithValue("@ToolSearchingTime", part.ToolSearchingTime);
                        cmd.Parameters.AddWithValue("@ToolChangingTime", part.ToolChangingTime);
                        cmd.Parameters.AddWithValue("@MentoringTime", part.MentoringTime);
                        cmd.Parameters.AddWithValue("@ContactingDepartmentsTime", part.ContactingDepartmentsTime);
                        cmd.Parameters.AddWithValue("@FixtureMakingTime", part.FixtureMakingTime);
                        cmd.Parameters.AddWithValue("@HardwareFailureTime", part.HardwareFailureTime);
                        cmd.Parameters.AddWithValue("@SpecialDowntimeTime", part.SpecialDowntimeTime);
                        cmd.Parameters.AddWithValue("@OperatorComment", part.OperatorComment);
                        cmd.Parameters.AddWithValue("@MasterSetupComment", part.MasterSetupComment);
                        cmd.Parameters.AddWithValue("@MasterMachiningComment", part.MasterMachiningComment);
                        cmd.Parameters.AddWithValue("@SpecifiedDowntimesComment", part.SpecifiedDowntimesComment);
                        cmd.Parameters.AddWithValue("@UnspecifiedDowntimeComment", part.UnspecifiedDowntimesComment);
                        cmd.Parameters.AddWithValue("@MasterComment", part.MasterComment);
                        cmd.Parameters.AddWithValue("@MasterSetupDetail", part.MasterSetupDetail);
                        cmd.Parameters.AddWithValue("@MasterMachiningDetail", part.MasterMachiningDetail);
                        cmd.Parameters.AddWithValue("@FixedSetupTimePlan", part.FixedSetupTimePlan);
                        cmd.Parameters.AddWithValue("@FixedProductionTimePlan", part.FixedProductionTimePlan);
                        cmd.Parameters.AddWithValue("@EngineerConclusion", part.EngineerConclusion);
                        cmd.Parameters.AddWithValue("@EngineerComment", part.EngineerComment);
                        cmd.Parameters.AddWithValue("@ExcludeFromReports", part.ExcludeFromReports);
                        cmd.Parameters.AddWithValue("@LongSetupReasonComment", part.LongSetupReasonComment);
                        cmd.Parameters.AddWithValue("@LongSetupFixComment", part.LongSetupFixComment);
                        cmd.Parameters.AddWithValue("@LongSetupEngeneerComment", part.LongSetupEngeneerComment);
                        cmd.Parameters.AddWithValue("@ExcludedOperationsTime", part.ExcludedOperationsTime);
                        cmd.Parameters.AddWithValue("@IncreaseReason", part.IncreaseReason);
                        cmd.Parameters.AddWithValue("@SetupReasonOverride", part.SetupReasonOverride);
                        cmd.Parameters.AddWithValue("@SetupReasonOverrideComment", part.SetupReasonOverrideComment);
                        cmd.Parameters.AddWithValue("@SetupReasonOverrideIsMasterFault", part.SetupReasonOverrideIsMasterFault);
                        cmd.Parameters.AddWithValue("@SetupReasonOverrideMasterFaultComment", part.SetupReasonOverrideMasterFaultComment);
                        cmd.Parameters.AddWithValue("@MachiningReasonOverride", part.MachiningReasonOverride);
                        cmd.Parameters.AddWithValue("@MachiningReasonOverrideComment", part.MachiningReasonOverrideComment);
                        cmd.Parameters.AddWithValue("@MachiningReasonOverrideIsMasterFault", part.MachiningReasonOverrideIsMasterFault);
                        cmd.Parameters.AddWithValue("@MachiningReasonOverrideMasterFaultComment", part.MachiningReasonOverrideMasterFaultComment);
                        cmd.Parameters.AddWithValue("@ReasonOverrideBy", part.ReasonOverrideBy);
                        cmd.Parameters.AddWithValue("@ReasonOverrideAt", (object?)part.ReasonOverrideAt ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@DefectiveCount", part.DefectiveCount);

                        var execureResult = await cmd.ExecuteNonQueryAsync();
                    }
                    await connection.CloseAsync();
                    return DbResult<string>.Ok("OK");
                }
            }
            catch (SqlException sqlEx)
            {
                switch (sqlEx.Number)
                {
                    case 18456:
                        var authMessage = $"Ошибка №{sqlEx.Number}:\nОшибка авторизации.";
                        Log.WriteError(sqlEx,authMessage);
                        return DbResult<string>.Fail(DbResult.AuthError, authMessage);
                    default:
                        var sqlExMessage = $"Ошибка №{sqlEx.Number}:";
                        Log.WriteError(sqlEx,sqlExMessage);
                        return DbResult<string>.Fail(DbResult.Error, sqlExMessage);
                }
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return DbResult<string>.FailWithError(ex.Message);
            }
                }

        static async Task FillPartsAsync(this ICollection<Part> parts, SqlCommand command, CancellationToken cancellationToken)
        {
            using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var guid = await reader.GetFieldValueAsync<Guid>(0, cancellationToken);
                    var machine = await reader.GetFieldValueAsync<string>(1, cancellationToken);
                    var shift = await reader.GetFieldValueAsync<string>(2, cancellationToken);
                    var shiftDate = await reader.GetFieldValueAsync<DateTime>(3, cancellationToken);
                    var @operator = await reader.GetFieldValueAsync<string>(4, cancellationToken);
                    var partName = await reader.GetFieldValueAsync<string>(5, cancellationToken);
                    var order = await reader.GetFieldValueAsync<string>(6, cancellationToken);
                    var setup = await reader.GetFieldValueAsync<int>(7, cancellationToken);
                    var finishedCount = await reader.GetFieldValueAsync<double>(8, cancellationToken);
                    var totalCount = await reader.GetFieldValueAsync<int>(9, cancellationToken);
                    var startSetupTime = await reader.GetFieldValueAsync<DateTime>(10, cancellationToken);
                    var startMachiningTime = await reader.GetFieldValueAsync<DateTime>(11, cancellationToken);
                    var setupTimeFact = await reader.GetFieldValueAsync<double>(12, cancellationToken);
                    var endMachiningTime = await reader.GetFieldValueAsync<DateTime>(13, cancellationToken);
                    var setupTimePlan = await reader.GetFieldValueAsync<double>(14, cancellationToken);
                    var setupTimePlanForReport = await reader.GetFieldValueAsync<double>(15, cancellationToken);
                    var singleProductionTimePlan = await reader.GetFieldValueAsync<double>(16, cancellationToken);
                    var productionTimeFact = await reader.GetFieldValueAsync<double>(17, cancellationToken);
                    var machiningTime = await reader.GetFieldValueAsync<long>(18, cancellationToken);
                    var setupDowntimes = await reader.GetFieldValueAsync<double>(19, cancellationToken);
                    var machiningDowntimes = await reader.GetFieldValueAsync<double>(20, cancellationToken);
                    var partialSetupTime = await reader.GetFieldValueAsync<double>(21, cancellationToken);
                    var createNcProgramTime = await reader.GetFieldValueAsync<double>(22, cancellationToken);
                    var maintenanceTime = await reader.GetFieldValueAsync<double>(23, cancellationToken);
                    var toolSearchingTime = await reader.GetFieldValueAsync<double>(24, cancellationToken);
                    var toolChangingTime = await reader.GetFieldValueAsync<double>(25, cancellationToken);
                    var mentoringTime = await reader.GetFieldValueAsync<double>(26, cancellationToken);
                    var contactiongDepartmentsTime = await reader.GetFieldValueAsync<double>(27, cancellationToken);
                    var fixtureMakingTime = await reader.GetFieldValueAsync<double>(28, cancellationToken);
                    var hardwareFailureTime = await reader.GetFieldValueAsync<double>(29, cancellationToken);
                    var operatorComment = await reader.GetFieldValueAsync<string>(30, cancellationToken);
                    var masterSetupComment = await reader.GetValueOrDefaultAsync(31, "", cancellationToken);
                    var masterMachiningComment = await reader.GetValueOrDefaultAsync(32, "", cancellationToken);
                    var specifiedDowntimesComment = await reader.GetValueOrDefaultAsync(33, "", cancellationToken);
                    var unspecifiedDowntimesComment = await reader.GetValueOrDefaultAsync(34, "", cancellationToken);
                    var masterComment = await reader.GetValueOrDefaultAsync(35, "", cancellationToken);
                    var fixedSetupTimePlan = await reader.GetValueOrDefaultAsync(36, 0.0, cancellationToken);
                    var fixedMachineTimePlan = await reader.GetValueOrDefaultAsync(37, 0.0, cancellationToken);
                    var engineerConclusion = await reader.GetValueOrDefaultAsync(38, "", cancellationToken);
                    var excludeFromReports = await reader.GetValueOrDefaultAsync(39, false, cancellationToken);
                    var longSetupReasonComment = await reader.GetValueOrDefaultAsync(40, "", cancellationToken);
                    var longSetupFixComment = await reader.GetValueOrDefaultAsync(41, "", cancellationToken);
                    var longSetupEngeneerComment = await reader.GetValueOrDefaultAsync(42, "", cancellationToken);
                    var excludedOperationsTime = await reader.GetValueOrDefaultAsync(43, 0.0, cancellationToken);
                    var increaseReason = await reader.GetValueOrDefaultAsync(44, "", cancellationToken);

                    var defectiveCount = await reader.GetValueOrDefaultAsync(46, 0, cancellationToken);
                    var specialDowntime = await reader.GetValueOrDefaultAsync(47, 0.0, cancellationToken);
                    // Колонки, добавленные через ALTER ADD после первого релиза, читаются по имени.
                    // Физический порядок колонок в конце таблицы не фиксирован: вычисляемая
                    // NormalizedPartName при обновлении схемы пересоздаётся и переезжает в конец,
                    // сдвигая ordinal всего, что было добавлено после неё.
                    var engineerComment = await reader.GetValueOrDefaultAsync("EngineerComment", "", cancellationToken);
                    var masterSetupDetail = await reader.GetValueOrDefaultAsync("MasterSetupDetail", "", cancellationToken);
                    var masterMachiningDetail = await reader.GetValueOrDefaultAsync("MasterMachiningDetail", "", cancellationToken);
                    var setupReasonOverride = await reader.GetValueOrDefaultAsync("SetupReasonOverride", "", cancellationToken);
                    var setupReasonOverrideComment = await reader.GetValueOrDefaultAsync("SetupReasonOverrideComment", "", cancellationToken);
                    var setupReasonOverrideIsMasterFault = await reader.GetValueOrDefaultAsync("SetupReasonOverrideIsMasterFault", false, cancellationToken);
                    var setupReasonOverrideMasterFaultComment = await reader.GetValueOrDefaultAsync("SetupReasonOverrideMasterFaultComment", "", cancellationToken);
                    var machiningReasonOverride = await reader.GetValueOrDefaultAsync("MachiningReasonOverride", "", cancellationToken);
                    var machiningReasonOverrideComment = await reader.GetValueOrDefaultAsync("MachiningReasonOverrideComment", "", cancellationToken);
                    var machiningReasonOverrideIsMasterFault = await reader.GetValueOrDefaultAsync("MachiningReasonOverrideIsMasterFault", false, cancellationToken);
                    var machiningReasonOverrideMasterFaultComment = await reader.GetValueOrDefaultAsync("MachiningReasonOverrideMasterFaultComment", "", cancellationToken);
                    var reasonOverrideBy = await reader.GetValueOrDefaultAsync("ReasonOverrideBy", "", cancellationToken);
                    var reasonOverrideAt = await reader.GetValueOrDefaultAsync<DateTime?>("ReasonOverrideAt", null, cancellationToken);

                    Part part = new(
                        guid,
                        machine,
                        shift,
                        shiftDate,
                        @operator,
                        partName,
                        order,
                        setup,
                        finishedCount,
                        defectiveCount,
                        totalCount,
                        startSetupTime,
                        startMachiningTime,
                        setupTimeFact,
                        endMachiningTime,
                        setupTimePlan,
                        setupTimePlanForReport,
                        singleProductionTimePlan,
                        productionTimeFact,
                        TimeSpan.FromTicks(machiningTime),
                        setupDowntimes,
                        machiningDowntimes,
                        partialSetupTime,
                        createNcProgramTime,
                        maintenanceTime,
                        toolSearchingTime,
                        toolChangingTime,
                        mentoringTime,
                        contactiongDepartmentsTime,
                        fixtureMakingTime,
                        hardwareFailureTime,
                        specialDowntime,
                        operatorComment,
                        masterSetupComment,
                        masterMachiningComment,
                        specifiedDowntimesComment,
                        unspecifiedDowntimesComment,
                        masterComment,
                        masterSetupDetail,
                        masterMachiningDetail,
                        fixedSetupTimePlan,
                        fixedMachineTimePlan,
                        engineerConclusion,
                        excludeFromReports,
                        engineerComment,
                        longSetupReasonComment,
                        longSetupFixComment,
                        longSetupEngeneerComment,
                        excludedOperationsTime,
                        increaseReason,
                        setupReasonOverride,
                        setupReasonOverrideComment,
                        setupReasonOverrideIsMasterFault,
                        setupReasonOverrideMasterFaultComment,
                        machiningReasonOverride,
                        machiningReasonOverrideComment,
                        machiningReasonOverrideIsMasterFault,
                        machiningReasonOverrideMasterFaultComment,
                        reasonOverrideBy,
                        reasonOverrideAt);
                    parts.Add(part);
                }
            }
        }

        public async static Task<List<PartsHistoryEntry>> ReadPartsHistoryAsync(
            string partName, string order, string machine, int setup, DateTime beforeDate,
            int maxRecords, int maxDaysBack, CancellationToken cancellationToken)
        {
            // Демо: история из памяти, без ревью аналитика.
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.Run(() => DemoStore
                    .GetParts(beforeDate.AddDays(-maxDaysBack), beforeDate.AddDays(-1), machine)
                    .Where(p => p.PartName == partName && p.Order != order && p.Setup == setup)
                    .OrderByDescending(p => p.ShiftDate).ThenByDescending(p => p.StartSetupTime)
                    .Take(maxRecords)
                    .Select(p => new PartsHistoryEntry { Part = p })
                    .ToList(), cancellationToken);
            }
            using var connection = new SqlConnection(DomainSettings.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            // Текущий заказ из выборки исключается. История нужна, чтобы проверить заявление
            // «деталь делается впервые» (Освоение и т.п.), а для этого годятся только записи
            // других заказов той же детали и установки: смены текущего заказа — это предыдущие
            // дни той же партии, они на «первый раз ли это» не отвечают.
            var partsSql =
                "SELECT TOP (@MaxRecords) * " +
                "FROM Parts " +
                "WHERE PartName = @PartName " +
                "  AND [Order] <> @Order " +
                "  AND Machine = @Machine " +
                "  AND Setup = @Setup " +
                "  AND ShiftDate < @BeforeDate " +
                "  AND ShiftDate >= @MinDate " +
                "ORDER BY ShiftDate DESC, StartSetupTime DESC;";

            var parts = new List<Part>();
            using (var cmd = new SqlCommand(partsSql, connection))
            {
                cmd.Parameters.AddWithValue("@MaxRecords", maxRecords);
                cmd.Parameters.AddWithValue("@PartName", partName);
                cmd.Parameters.AddWithValue("@Order", order);
                cmd.Parameters.AddWithValue("@Machine", machine);
                cmd.Parameters.AddWithValue("@Setup", setup);
                cmd.Parameters.AddWithValue("@BeforeDate", beforeDate.Date);
                cmd.Parameters.AddWithValue("@MinDate", beforeDate.Date.AddDays(-maxDaysBack));
                await FillPartsAsync(parts, cmd, cancellationToken);
            }

            if (parts.Count == 0)
                return new List<PartsHistoryEntry>();

            var uniqueDates = parts
                .Select(p => p.ShiftDate.Date)
                .Distinct()
                .ToList();

            var dateParams = uniqueDates
                .Select((_, i) => "@d" + i)
                .ToArray();

            var reviewSql =
                "SELECT ShiftDate, Decision, Comment, AiExplanation, AiFeedback " +
                "FROM ai_day_reviews " +
                "WHERE Machine = @Machine " +
                "  AND ShiftDate IN (" + string.Join(", ", dateParams) + ");";

            var reviews = new Dictionary<DateTime, (string Decision, string? Comment, string? AiExplanation, string? AiFeedback)>();
            using (var cmd = new SqlCommand(reviewSql, connection))
            {
                cmd.Parameters.AddWithValue("@Machine", machine);
                for (int i = 0; i < uniqueDates.Count; i++)
                    cmd.Parameters.AddWithValue(dateParams[i], uniqueDates[i]);

                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var date = reader.GetDateTime(0).Date;
                    var decision = reader.GetString(1);
                    var comment = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var aiExpl = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var aiFb = reader.IsDBNull(4) ? null : reader.GetString(4);
                    reviews[date] = (decision, comment, aiExpl, aiFb);
                }
            }

            var result = new List<PartsHistoryEntry>();
            foreach (var p in parts)
            {
                reviews.TryGetValue(p.ShiftDate.Date, out var review);
                result.Add(new PartsHistoryEntry
                {
                    Part = p,
                    AnalystDecision = review.Decision,
                    AnalystComment = review.Comment,
                    AiExplanation = review.AiExplanation,
                    AiFeedback = review.AiFeedback,
                });
            }
            return result;
        }

        public static DbResult<List<string>> ReadMasters()
        {
            if (DomainSettings.DemoMode)
                return DbResult<List<string>>.Ok(new List<string> { "Петров П.П.", "Сидоров С.С." });
            var masters = new List<string>();
            try
            {
                using (SqlConnection connection = new(DomainSettings.ConnectionString))
                {
                    connection.Open();
                    string query = $"SELECT FullName FROM masters WHERE IsActive = 1 ORDER BY FullName ASC";
                    using (SqlCommand command = new(query, connection))
                    {
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                masters.Add(reader.GetString(0));
                            }
                        }
                    }
                }
                return DbResult<List<string>>.Ok(masters);
            }
            catch (SqlException sqlEx)
            {
                switch (sqlEx.Number)
                {
                    case 18456:
                        Log.WriteError(sqlEx,$"Ошибка №{sqlEx.Number}:\nОшибка авторизации.");
                        return DbResult<List<string>>.Fail(DbResult.AuthError, "Ошибка авторизации.");
                    default:
                        Log.WriteError(sqlEx,$"Ошибка №{sqlEx.Number}:");
                        return DbResult<List<string>>.Fail(DbResult.Error, $"Ошибка №{sqlEx.Number}:");
                }
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return DbResult<List<string>>.FailWithError(ex.Message);
            }
        }

        public static DbResult<bool> DeletePart(this Part part)
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return DemoStore.DeletePart(part.Guid);
            }
            try
            {
                using (SqlConnection connection = new(DomainSettings.ConnectionString))
                {
                    connection.Open();
                    string query = $"DELETE FROM parts WHERE GUID = @Guid";
                    using (SqlCommand command = new(query, connection))
                    {
                        command.Parameters.AddWithValue("Guid", part.Guid);
                        command.ExecuteNonQuery();
                    }
                }
                return DbResult<bool>.Ok(true);
            }
            catch (SqlException sqlEx)
            {
                switch (sqlEx.Number)
                {
                    case 18456:
                        Log.WriteError(sqlEx,$"Ошибка №{sqlEx.Number}:\nОшибка авторизации.");
                        return DbResult<bool>.Fail(DbResult.AuthError, "Ошибка авторизации.");
                    default:
                        Log.WriteError(sqlEx,$"Ошибка №{sqlEx.Number}:");
                        return DbResult<bool>.Fail(DbResult.Error, $"Ошибка №{sqlEx.Number}:");
                }
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return DbResult<bool>.FailWithError(ex.Message);
            }
        }
    }
}
