
using AiService.Models;
using AiService.Services;

namespace AiService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            // Database:ConnectionString в appsettings.json — ссылка ${ИМЯ} на переменную
            // окружения (сырые строки в репозитории запрещены). Резолвится здесь один раз —
            // дальше все читатели (SqlAgentDataSource, OperatorStatsService) видят готовое
            // значение через IConfiguration. Пусто/сырая строка деплоя — как есть.
            builder.Configuration["Database:ConnectionString"] =
                ConnectionStringReference.Resolve(builder.Configuration);
            builder.Logging.AddSimpleConsole(o =>
            {
                o.TimestampFormat = "HH:mm:ss.fff ";
                o.SingleLine = true;
            });
            builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs")));
            builder.Services.AddControllers();
            builder.Services.AddSingleton<OllamaService>();
            // IAgentChatClient резолвится в тот же OllamaService (очередь к GPU общая).
            builder.Services.AddSingleton<IAgentChatClient>(sp => sp.GetRequiredService<OllamaService>());
            builder.Services.AddSingleton<PromptBuilder>();
            builder.Services.AddSingleton<RequestLog>();
            // Кэш подсказок мастеру (VerifyCache:Enabled/TtlMinutes/MaxEntries).
            builder.Services.AddSingleton<VerifyCache>();
            // Срок хранения logs/ и request_logs/ (Retention:Days, по умолчанию 180; 0 — выключено).
            builder.Services.AddHostedService<LogRetentionService>();
            builder.Services.AddSingleton<WebSearchService>();
            builder.Services.AddSingleton<DocsService>();
            builder.Services.AddSingleton<StockService>();
builder.Services.AddSingleton<OperatorStatsService>();
            builder.Services.AddSingleton<ChatAgentService>();
            // Агентский контур дневного анализа (стадия 1: каркас, выключен флагом Agent:Enabled).
            builder.Services.AddSingleton<AgentLoopService>();
            // Источник фактологии tools (стадия 2): прямая БД при непустой
            // Database:ConnectionString, иначе данные запроса (тесты, replay).
            builder.Services.AddSingleton<IAgentDataSource>(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                return string.IsNullOrWhiteSpace(cfg.GetValue("Database:ConnectionString", ""))
                    ? RequestAgentDataSource.Instance
                    : new SqlAgentDataSource(
                        cfg, sp.GetRequiredService<ILogger<SqlAgentDataSource>>());
            });

            builder.Host.UseWindowsService();

            var app = builder.Build();

            var port = app.Configuration.GetValue("Port", 5050);
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogDebug("AiService started on http://0.0.0.0:{Port}", port);

            app.Use(async (context, next) =>
            {
                var ip = context.Connection.RemoteIpAddress;
                logger.LogDebug("{RemoteIP} {Method} {Path}", ip, context.Request.Method, context.Request.Path);
                await next();
            });

            app.MapControllers();

            app.Run($"http://0.0.0.0:{port}");
        }
    }
}
