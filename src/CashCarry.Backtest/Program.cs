using CashCarry.Backtest;
using CashCarry.Broker.Finam;
using CashCarry.Engine;
using CashCarry.Risk;
using CashCarry.Risk.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

const string Usage = """
    Тестер CashCarry на истории Финам.

      dotnet run --project src/CashCarry.Backtest -- download [ключи]   загрузить свечи
      dotnet run --project src/CashCarry.Backtest -- run [ключи]        прогнать робота на загруженных свечах

    Ключи:
      --from 2026-10-01    первый день периода (по умолчанию: 7 дней назад для M1, 30 — для остальных)
      --to 2026-10-14      последний день (по умолчанию сегодня)
      --timeframe M1       M1, M5, M15, M30, H1
      --pairs SBER,GAZP    пары или all (по умолчанию включённые в настройках робота)
      --cash 500000        стартовые деньги (только run)
      --spread 1           спред в шагах цены, достраиваемый к close свечи (только run)
      --max-capital 50000  лимит денег робота (по умолчанию из настроек робота)
      --verbose            писать в консоль решения и сделки робота (только run)
      --Strategy:MinEntryAnnualYield=0.10   любой параметр настроек робота
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0 || args[0] is not ("download" or "run"))
{
    Console.WriteLine(Usage);
    return args.Length == 0 || args[0] is "help" or "--help" or "-h" ? 0 : 1;
}

string command = args[0];
bool verbose = args.Contains("--verbose");
string[] options = args.Skip(1).Where(a => a != "--verbose").ToArray();

// Пути (база истории, отчёты) — относительно папки программы, как у робота.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("backtest.json", optional: true)
    .AddUserSecrets(typeof(BacktestSettings).Assembly, optional: true)
    .AddCommandLine(options, new Dictionary<string, string>
    {
        ["--from"] = "Backtest:From",
        ["--to"] = "Backtest:To",
        ["--timeframe"] = "Backtest:Timeframe",
        ["--pairs"] = "Backtest:Pairs",
        ["--cash"] = "Backtest:InitialCash",
        ["--spread"] = "Backtest:SpreadTicks",
        ["--max-capital"] = "Robot:MaxCapital",
    })
    .Build();

T Bind<T>(string section) where T : new() => config.GetSection(section).Get<T>() ?? new T();

BacktestSettings settings;
RobotSettings robot;
List<PairSettings> pairs;
try
{
    settings = Bind<BacktestSettings>("Backtest");
    settings.Validate();
    robot = Bind<RobotSettings>("Robot");
    robot.Validate();
    pairs = settings.Select(PairsConfig.Load(config));
    settings.AddHistoricalDividends(pairs);
    if (pairs.Count == 0)
        throw new InvalidOperationException("не выбрано ни одной пары: включите пару в настройках робота или укажите --pairs.");
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Ошибка настроек: {ex.Message}");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var history = new HistoryStore(settings.HistoryDatabase);
using var loggers = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Warning));

var expired = ExpiredFutures.AddTo(pairs, settings.FromDate);
if (expired.Count > 0)
    Console.WriteLine($"Добавлены истёкшие серии (последний день — третий четверг, ГО — как у ближайшей настроенной): {string.Join(", ", expired)}");

try
{
    if (command == "download")
    {
        var finam = Bind<FinamOptions>("Finam");
        finam.Validate(requireAccount: false);
        using var connection = new FinamConnection(finam);
        return await HistoryDownloader.RunAsync(
            new FinamHistory(connection, loggers.CreateLogger<FinamHistory>()), history, settings, pairs, Console.Out, cts.Token);
    }

    string runDirectory = Path.GetFullPath(Path.Combine(settings.OutputDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    Directory.CreateDirectory(runDirectory);
    Console.WriteLine($"Прогон пар {string.Join(", ", pairs.Select(p => p.Name))}, стартовые деньги {settings.InitialCash:N0} ₽" +
                      (robot.MaxCapital is { } limit ? $", лимит робота {limit:N0} ₽" : "") + "...");

    var clock = new BacktestClock(DateTimeOffset.MinValue);
    var log = new BacktestLog(clock, verbose ? LogLevel.Information : LogLevel.Error, Console.Out);
    var inputs = new BacktestInputs(settings, pairs, robot, Bind<CommissionSettings>("Commissions").ToSchedule(),
        Bind<RiskLimits>("Risk"), Bind<ExecutionOptions>("Execution"), history, runDirectory);
    var result = await BacktestRunner.RunAsync(inputs, clock, log, cts.Token);

    BacktestReport.Print(result, settings, log.Problems, Console.Out);
    var (trades, equity) = BacktestReport.WriteCsv(result, runDirectory);
    Console.WriteLine();
    Console.WriteLine($"Сделки:      {trades}");
    Console.WriteLine($"По дням:     {equity}");
    Console.WriteLine($"Журнал:      {result.JournalPath}");
    return 0;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Ошибка: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Остановлено.");
    return 1;
}
