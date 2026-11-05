using Dapper;
using Microsoft.Data.SqlClient;
using remeLog.Core;
using remeLog.Core.Db;
using remeLog.Core.Services.Demo;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using remeLog.Models.Reports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace remeLog.Infrastructure
{
    public static partial class Database
    {
        public async static Task<List<Machine>> GetMachinesAsync(IProgress<string> progress)
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                progress.Report("Демо-режим: станки из встроенного генератора.");
                return DemoStore.GetMachineFilters()
                    .Select((m, i) => new Machine { Id = i + 1, Name = m.Machine, Type = m.Type, IsActive = true }).ToList();
            }
            progress.Report("Подключение к БД...");
            try
            {
                await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);
                progress.Report("Чтение данных из БД...");
                var machines = (await conn.QueryAsync<Machine>(@"
                    SELECT * FROM cnc_machines WHERE IsActive = 1")).AsList();
                progress.Report("Чтение завершено");
                return machines;
            }
            catch (System.Exception ex)
            {
                Log.WriteError(ex, null);
                return new List<Machine>();
            }
        }

        public static DbResult<List<string>> ReadMachines()
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return DbResult<List<string>>.Ok(DemoStore.GetMachines());
            }
            try
            {
                using var conn = DbHelper.OpenConnection(DomainSettings.ConnectionString);
                var machines = conn.Query<string>("SELECT Name FROM cnc_machines ORDER BY Name").AsList();
                return DbResult<List<string>>.Ok(machines);
            }
            catch (SqlException sqlEx)
            {
                Log.WriteError(sqlEx, sqlEx.Number.ToString());
                return sqlEx.Number == 18456
                    ? DbResult<List<string>>.Fail(DbResult.AuthError, "Ошибка авторизации.")
                    : DbResult<List<string>>.Fail(DbResult.Error, sqlEx.Number.ToString());
            }
            catch (System.Exception ex)
            {
                Log.WriteError(ex, null);
                return DbResult<List<string>>.FailWithError(ex.Message);
            }
        }

        public static async Task<DbResult<List<MachineFilter>>> ReadMachinesAsync()
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.FromResult(DbResult<List<MachineFilter>>.Ok(DemoStore.GetMachineFilters()));
            }
            try
            {
                await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);
                const string sql = "SELECT Name AS Machine, Type, CAST(0 AS bit) AS Filter FROM cnc_machines WHERE IsActive = 1 ORDER BY Name";
                var machines = (await conn.QueryAsync<MachineFilter>(sql)).AsList();
                return DbResult<List<MachineFilter>>.Ok(machines);
            }
            catch (SqlException sqlEx)
            {
                Log.WriteError(sqlEx, sqlEx.Number.ToString());
                return sqlEx.Number == 18456
                    ? DbResult<List<MachineFilter>>.Fail(DbResult.AuthError, "Ошибка авторизации.")
                    : DbResult<List<MachineFilter>>.Fail(DbResult.Error, sqlEx.Number.ToString());
            }
            catch (System.Exception ex)
            {
                Log.WriteError(ex, null);
                return DbResult<List<MachineFilter>>.FailWithError(ex.Message);
            }
        }

        public static DbResult<List<(string Reason, bool RequireComment)>> ReadDeviationReasons(DeviationReasonType type)
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return DbResult<List<(string Reason, bool RequireComment)>>.Ok(
                    DemoStore.GetDeviationReasons(type));
            }
            try
            {
                const string sql = "SELECT Reason, RequireComment FROM cnc_deviation_reasons WHERE Type IS NULL OR Type = @Type ORDER BY Reason ASC";
                using var conn = DbHelper.OpenConnection(DomainSettings.ConnectionString);
                var reasons = conn.Query<(string Reason, bool RequireComment)>(sql, new { Type = type.ToString() }).AsList();
                return DbResult<List<(string Reason, bool RequireComment)>>.Ok(reasons);
            }
            catch (SqlException sqlEx)
            {
                Log.WriteError(sqlEx, sqlEx.Number.ToString());
                return DbResult<List<(string Reason, bool RequireComment)>>.Fail(DbResult.Error, sqlEx.Number.ToString());
            }
            catch (System.Exception ex)
            {
                Log.WriteError(ex, null);
                return DbResult<List<(string Reason, bool RequireComment)>>.FailWithError(ex.Message);
            }
        }

        public static DbResult<List<string>> ReadDowntimeReasons()
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return DbResult<List<string>>.Ok(DemoStore.GetDowntimeReasons());
            }
            try
            {
                using var conn = DbHelper.OpenConnection(DomainSettings.ConnectionString);
                var reasons = conn.Query<string>("SELECT Reason FROM cnc_downtime_reasons ORDER BY Reason ASC").AsList();
                return DbResult<List<string>>.Ok(reasons);
            }
            catch (SqlException sqlEx)
            {
                Log.WriteError(sqlEx, sqlEx.Number.ToString());
                return sqlEx.Number == 18456
                    ? DbResult<List<string>>.Fail(DbResult.AuthError, "Ошибка авторизации.")
                    : DbResult<List<string>>.Fail(DbResult.Error, sqlEx.Number.ToString());
            }
            catch (System.Exception ex)
            {
                Log.WriteError(ex, null);
                return DbResult<List<string>>.FailWithError(ex.Message);
            }
        }

        public async static Task<IEnumerable<Qualification>> GetQualificationsAsync(IProgress<string>? progress = null)
        {
            // Демо: квалификации не мокаются — отчёты по разрядам покажут пусто.
            if (DomainSettings.DemoMode) return Enumerable.Empty<Qualification>();
            progress?.Report("Подключение к БД...");
            var sql = @"SELECT [Qualification],
                [EfficiencyValueHH],[EfficiencyCoefficientHH],[EfficiencyValueH],[EfficiencyCoefficientH],
                [EfficiencyValueN],[EfficiencyCoefficientN],[EfficiencyValueL],[EfficiencyCoefficientL],
                [EfficiencyValueLL],[EfficiencyCoefficientLL],[EfficiencyValueLLL],[EfficiencyCoefficientLLL],
                [DownTimesValueHH],[DownTimesCoefficientHH],[DownTimesValueH],[DownTimesCoefficientH],
                [DownTimesValueN],[DownTimesCoefficientN],[DownTimesValueL],[DownTimesCoefficientL],
                [DownTimesValueLL],[DownTimesCoefficientLL],[DownTimesValueLLL],[DownTimesCoefficientLLL],
                [NonSerialEfficiencyValueHH],[NonSerialEfficiencyCoefficientHH],
                [NonSerialEfficiencyValueH],[NonSerialEfficiencyCoefficientH],
                [NonSerialEfficiencyValueN],[NonSerialEfficiencyCoefficientN],
                [NonSerialEfficiencyValueL],[NonSerialEfficiencyCoefficientL],
                [NonSerialEfficiencyValueLL],[NonSerialEfficiencyCoefficientLL],
                [NonSerialEfficiencyValueLLL],[NonSerialEfficiencyCoefficientLLL] FROM cnc_qualifications;";

            try
            {
                await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);
                progress?.Report("Чтение данных из БД...");
                var rows = (await conn.QueryAsync<QualificationRow>(sql)).AsList();
                var result = rows.Select(Qualification.FromRow).ToList();
                progress?.Report("Чтение завершено");
                return result;
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return Enumerable.Empty<Qualification>();
            }
        }

        public async static Task<bool> GetMachineSerialStatus(string machine, IProgress<string>? progress = null)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(false);
            progress?.Report("Подключение к БД...");
            try
            {
                await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);
                progress?.Report("Чтение данных из БД...");
                return await conn.QueryFirstOrDefaultAsync<bool>(
                    "SELECT IsSerial FROM cnc_machines WHERE Name = @Machine", new { Machine = machine });
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return false;
            }
        }

        /// <summary>
        /// Свойства станков, влияющие на К1: серийность и граница простоев (cnc_machines.IsSerial, DowntimeLimit).
        /// При сбое БД — пустой словарь (станки считаются несерийными, как и в прежних отчётах).
        /// </summary>
        public async static Task<Dictionary<string, MachineProfile>> GetMachineProfilesAsync(IProgress<string>? progress = null)
        {
            if (DomainSettings.DemoMode) return new Dictionary<string, MachineProfile>();
            try
            {
                progress?.Report("Чтение данных о станках...");
                await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);
                var rows = await conn.QueryAsync<(string Name, bool IsSerial, double DowntimeLimit)>(
                    "SELECT Name, IsSerial, DowntimeLimit FROM cnc_machines");
                return rows.ToDictionary(r => r.Name, r => new MachineProfile(r.IsSerial, r.DowntimeLimit));
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return new Dictionary<string, MachineProfile>();
            }
        }

        /// <summary>
        /// Серийность станка с явным дефолтом на случай сбоя БД. В отличие от
        /// GetMachineSerialStatus (дефолт false для отчётов), здесь дефолт true:
        /// для ИИ-контура сбой не должен тихо выключать проверку КПД изготовления.
        /// Используется только AiServiceClient (флаг isSerialMachine в запросе).
        /// </summary>
        public async static Task<bool> GetMachineSerialStatusOrDefault(
            string machine, bool defaultValue, CancellationToken ct = default)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult(defaultValue);
            try
            {
                await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);
                return await conn.QueryFirstOrDefaultAsync<bool>(
                    new CommandDefinition(
                        "SELECT IsSerial FROM cnc_machines WHERE Name = @Machine",
                        new { Machine = machine },
                        cancellationToken: ct));
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return defaultValue;
            }
        }

        /// <summary>
        /// Профиль промпта ИИ-анализа для станка (cnc_machines.AiPromptProfile).
        /// NULL/пусто — базовый промпт AiService.
        /// </summary>
        public async static Task<string?> GetMachineAiPromptProfileAsync(string machine, CancellationToken ct = default)
        {
            if (DomainSettings.DemoMode) return await Task.FromResult<string?>(null);
            try
            {
                await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);
                return await conn.QueryFirstOrDefaultAsync<string?>(
                    new CommandDefinition(
                        "SELECT AiPromptProfile FROM cnc_machines WHERE Name = @Machine",
                        new { Machine = machine },
                        cancellationToken: ct));
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return null;
            }
        }

        public async static Task<List<DateTime>> GetHolidaysAsync(IProgress<string>? progress)
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return await Task.FromResult(DomainSettings.Holidays.ToList());
            }
            progress?.Report("Подключение к БД...");
            try
            {
                await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);
                progress?.Report("Чтение данных из БД...");
                var holidays = (await conn.QueryAsync<DateTime>(
                    "SELECT Holidays FROM cnc_remelog_config")).AsList();
                progress?.Report("Чтение завершено");
                return holidays;
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return new List<DateTime>();
            }
        }

        public async static Task<List<ToolSearchCase>> GetToolSearchCasesAsync(List<Guid> guids, IProgress<string> progress)
        {
            // Демо: кейсы поиска инструмента не мокаются.
            if (DomainSettings.DemoMode) return new List<ToolSearchCase>();
            var cases = new List<ToolSearchCase>();
            if (guids == null || guids.Count == 0) return cases;

            progress.Report("Формирую запрос...");
            const int chunkSize = 2000;
            var chunks = guids.Select((g, i) => new { g, i })
                .GroupBy(x => x.i / chunkSize)
                .Select(g => g.Select(x => x.g).ToList()).ToList();

            try
            {
                await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);
                foreach (var chunk in chunks)
                {
                    progress.Report($"Запрашиваю данные...");
                    var chunkCases = await conn.QueryAsync<ToolSearchCase>(
                        "SELECT p.Operator, p.PartName AS Part, p.Machine, c.ToolType AS Type, c.Value AS Description, c.StartTime, c.EndTime, c.IsSuccess FROM cnc_tool_search_cases c INNER JOIN parts p ON c.PartGuid = p.Guid WHERE c.PartGuid IN @guids",
                        new { guids = chunk });
                    cases.AddRange(chunkCases);
                }
                progress.Report("Чтение завершено.");
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
            }
            return cases;
        }

        public static DbResult<WncConfig> GetWncConfig()
        {
            // Демо: Windchill недоступен вне рабочей среды.
            if (DomainSettings.DemoMode)
                return DbResult<WncConfig>.Fail(DbResult.Error, "Демо-режим: Windchill недоступен.");
            try
            {
                using var conn = DbHelper.OpenConnection(DomainSettings.ConnectionString);
                var row = conn.QueryFirstOrDefault<(string Server, string User, string Password, string LocalType)>(
                    "SELECT Server, [User], Pass AS Password, LocalType FROM cnc_wnc_cfg");
                if (row == default)
                    return DbResult<WncConfig>.NotFound();
                return DbResult<WncConfig>.Ok(new WncConfig(row.Server, row.User, row.Password, row.LocalType));
            }
            catch (SqlException sqlEx)
            {
                Log.WriteError(sqlEx, sqlEx.Number.ToString());
                return sqlEx.Number == 18456
                    ? DbResult<WncConfig>.Fail(DbResult.AuthError, "Ошибка авторизации.")
                    : DbResult<WncConfig>.Fail(DbResult.Error, sqlEx.Number.ToString());
            }
            catch (Exception ex)
            {
                Log.WriteError(ex, null);
                return DbResult<WncConfig>.FailWithError(ex.Message);
            }
        }

        /// <summary>
        /// Настройки, нужные только для расчёта К1: праздники (рабочие дни периода) и лимиты КПД наладки по станкам.
        /// В отличие от <see cref="UpdateAppSettings"/> ничего больше не трогает и не сохраняет — для eLog.
        /// </summary>
        public static async Task LoadKpiSettingsAsync()
        {
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                return;
            }
            await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);

            var holidays = (await conn.QueryAsync<DateTime?>("SELECT Holidays FROM cnc_remelog_config WHERE Holidays IS NOT NULL"))
                .Where(h => h.HasValue).Select(h => h!.Value).ToArray();
            DomainSettings.Holidays = holidays;

            var maxSetup = await conn.QueryFirstOrDefaultAsync<double?>("SELECT TOP 1 max_setup_limit FROM cnc_remelog_config WHERE max_setup_limit IS NOT NULL");
            if (maxSetup.HasValue) DomainSettings.MaxSetupLimit = maxSetup.Value;

            DomainSettings.MaxSetupLimits.Clear();
            var limits = await conn.QueryAsync<(string Name, double SetupCoefficient)>("SELECT Name, SetupCoefficient FROM cnc_machines");
            foreach (var (name, coeff) in limits)
                DomainSettings.MaxSetupLimits[name] = coeff;
        }

        public static async Task UpdateAppSettings()
        {
            // Демо: конфиг из генератора (см. DemoStore.ApplyConfigToDomainSettings),
            // права на фичи — все тестовые, SQL Server не трогаем.
            if (DomainSettings.DemoMode)
            {
                DemoStore.EnsureInitialized();
                Persistence.Save();
                await Task.CompletedTask;
                return;
            }
            await using var conn = await DbHelper.OpenConnectionAsync(DomainSettings.ConnectionString);

            var administrators = new List<string>();
            var operations = new List<string>();
            var holidays = new List<DateTime>();
            var engineerComments = new List<string>();

            var rows = await conn.QueryAsync<(double? max_setup_limit, double? long_setup_limit, string? NcArchivePath, string? NcIntermediatePath, string? Administrators, string? CncOperations, DateTime? Holidays, string? PcaReportPath, string? EngineerComments, string? AiIp, string? AiModel, int? SchemaVersion)>(
                "SELECT max_setup_limit, long_setup_limit, NcArchivePath, NcIntermediatePath, Administrators, CncOperations, Holidays, PcaReportPath, EngineerComments, AiIp, AiModel, SchemaVersion FROM cnc_remelog_config");

            DomainSettings.SchemaVersion = 0;
            foreach (var row in rows)
            {
                if (row.max_setup_limit.HasValue) DomainSettings.MaxSetupLimit = row.max_setup_limit.Value;
                if (row.long_setup_limit.HasValue) DomainSettings.LongSetupLimit = row.long_setup_limit.Value;
                if (row.NcArchivePath != null) DomainSettings.NcArchivePath = row.NcArchivePath;
                if (row.NcIntermediatePath != null) DomainSettings.NcIntermediatePath = row.NcIntermediatePath;
                if (row.Administrators != null) administrators.Add(row.Administrators);
                if (row.CncOperations != null) operations.Add(row.CncOperations);
                if (row.Holidays.HasValue) holidays.Add(row.Holidays.Value);
                if (row.PcaReportPath != null) DomainSettings.PcaReportPath = row.PcaReportPath;
                if (row.EngineerComments != null) engineerComments.Add(row.EngineerComments);
                if (row.AiIp != null) DomainSettings.AiIp = row.AiIp;
                if (!string.IsNullOrWhiteSpace(row.AiModel)) DomainSettings.AiModel = row.AiModel;
                if (row.SchemaVersion.HasValue && row.SchemaVersion.Value > DomainSettings.SchemaVersion)
                    DomainSettings.SchemaVersion = row.SchemaVersion.Value;
            }

            DomainSettings.Administrators = administrators.ToArray();
            DomainSettings.CncOperations = operations.ToArray();
            DomainSettings.Holidays = holidays.ToArray();
            DomainSettings.EngineerComments = engineerComments.ToArray();

            DomainSettings.MaxSetupLimits.Clear();
            var limits = await conn.QueryAsync<(string Name, double SetupCoefficient)>(
                "SELECT Name, SetupCoefficient FROM cnc_machines");
            foreach (var (name, coeff) in limits)
                DomainSettings.MaxSetupLimits[name] = coeff;

            var featureRow = await conn.QueryFirstOrDefaultAsync<(string UserName, int EnabledFeatures)>(
                "SELECT UserName, EnabledFeatures FROM cnc_remelog_feature_permissions WHERE UserName = @UserName",
                new { UserName = Environment.UserName });
            if (featureRow.UserName != null && !DomainSettings.FeaturesExplicitlySet)
                DomainSettings.EnabledFeatures |= (RemeLogFeature)featureRow.EnabledFeatures;

            Persistence.Save();
        }
    }
}
