using CashCarry.Broker.Finam;
using CashCarry.Broker.Paper;
using CashCarry.Core.Broker;
using CashCarry.Core.Strategy;
using CashCarry.Engine;
using CashCarry.Engine.Market;
using CashCarry.Risk;
using CashCarry.Risk.Execution;
using Serilog;

const string ResetKillSwitchArg = "--reset-kill-switch";
const string FinamCheckArg = "--finam-check";
const string FindSymbolsArg = "--find-symbols";

bool resetKillSwitch = args.Contains(ResetKillSwitchArg);
bool finamCheck = args.Contains(FinamCheckArg);
int findIndex = Array.IndexOf(args, FindSymbolsArg);
string? findQuery = findIndex >= 0 && findIndex + 1 < args.Length ? args[findIndex + 1] : null;
string[] hostArgs = args
    .Where((a, i) => a is not (ResetKillSwitchArg or FinamCheckArg or FindSymbolsArg) && (findIndex < 0 || i != findIndex + 1))
    .ToArray();

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Конфиг, база и логи — относительно папки программы, а не папки, из которой её запустили.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = hostArgs,
    ContentRootPath = AppContext.BaseDirectory,
});
// Секрет Финам хранится в user-secrets; по умолчанию они читаются только в Development — подключаем всегда.
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);
var config = builder.Configuration;

T Bind<T>(string section) where T : new() => config.GetSection(section).Get<T>() ?? new T();

var robot = Bind<RobotSettings>("Robot");
var paper = Bind<PaperSettings>("Paper");
var finam = Bind<FinamOptions>("Finam");
List<PairSettings> pairSettings;
try
{
    robot.Validate();
    pairSettings = PairsConfig.Load(config);
    if (finamCheck || findQuery is not null || robot.Mode == BrokerMode.Finam || paper.PriceSource == PriceSource.Finam)
        finam.Validate(requireAccount: robot.Mode == BrokerMode.Finam && !finamCheck && findQuery is null);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Ошибка настроек: {ex.Message}");
    return 1;
}

var commissions = Bind<CommissionSettings>("Commissions").ToSchedule();

if (finamCheck || findQuery is not null)
{
    using var connection = new FinamConnection(finam);
    if (findQuery is not null)
    {
        Console.WriteLine($"Инструменты Финам, содержащие «{findQuery}»:");
        await FinamDiagnostics.FindSymbolsAsync(connection, findQuery, Console.Out);
    }
    else
    {
        // Все пары, включая отключённые: так проверяют новую пару перед тем, как включить её.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var symbols = pairSettings.SelectMany(p => p.Active(today).Select(f => f.Symbol).Prepend(p.StockSymbol));
        await FinamDiagnostics.CheckAsync(connection, new FinamBroker(connection, commissions), symbols, Console.Out);
    }
    return 0;
}

// У реального счёта своя база: позиция из симуляции не должна выглядеть как настоящая.
string dbPath = Path.GetFullPath(robot.DatabasePath, builder.Environment.ContentRootPath);
if (robot.Mode == BrokerMode.Finam)
    dbPath = Path.Combine(Path.GetDirectoryName(dbPath)!, Path.GetFileNameWithoutExtension(dbPath) + "-finam" + Path.GetExtension(dbPath));
var store = new RobotStore(dbPath);

if (resetKillSwitch)
{
    Console.WriteLine(store.LoadKillSwitch() is { } reason
        ? $"Kill switch сброшен ({Path.GetFileName(dbPath)}). Причина была: {reason}"
        : $"Kill switch не был активен ({Path.GetFileName(dbPath)}).");
    store.SaveKillSwitch(null);
    return 0;
}

if (robot.Mode == BrokerMode.Finam && !finam.TradingEnabled)
    robot.DryRun = true;

builder.Services.AddSerilog((_, logger) => logger.ReadFrom.Configuration(config));

builder.Services
    .AddSingleton(robot)
    .AddSingleton<IReadOnlyList<PairSettings>>(pairSettings)
    .AddSingleton(commissions)
    .AddSingleton(Bind<RiskLimits>("Risk"))
    .AddSingleton(Bind<ExecutionOptions>("Execution"))
    .AddSingleton(store)
    .AddSingleton(TimeProvider.System)
    .AddSingleton(robot.IgnoreTradingSchedule
        ? TradingSchedule.AlwaysOpen()
        : TradingSchedule.MoexMainSession(robot.Holidays))
    .AddSingleton<IKillSwitch, KillSwitch>()
    .AddSingleton<IReadOnlyList<TradingPair>>(sp => pairSettings
        .Select(p => new TradingPair(
            p, commissions, sp.GetRequiredService<IBrokerGateway>(), sp.GetRequiredService<IKillSwitch>(),
            sp.GetRequiredService<TradingSchedule>(), sp.GetRequiredService<RiskLimits>(),
            sp.GetRequiredService<ExecutionOptions>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>()))
        .ToList())
    .AddSingleton<RobotWorker>()
    .AddHostedService(sp => sp.GetRequiredService<RobotWorker>());

bool useFinam = robot.Mode == BrokerMode.Finam || paper.PriceSource == PriceSource.Finam;
if (useFinam)
{
    builder.Services
        .AddSingleton(finam)
        .AddSingleton<FinamConnection>()
        .AddSingleton<FinamBroker>();
}

switch (robot.Mode)
{
    case BrokerMode.Paper:
        builder.Services
            .AddSingleton(new PaperBroker(
                pairSettings.SelectMany(p => p.Futures
                    .Select(f => new PaperInstrument(f.Symbol, InstrumentKind.Futures, 1, p.PointValue, f.InitialMargin))
                    .Prepend(new PaperInstrument(p.StockSymbol, InstrumentKind.Stock, p.StockLotSize))),
                commissions,
                paper.InitialCash))
            .AddSingleton<IBrokerGateway>(sp => sp.GetRequiredService<PaperBroker>());
        if (paper.PriceSource == PriceSource.Finam)
            builder.Services.AddSingleton<IMarketFeed, FinamQuoteFeed>();
        else
            builder.Services.AddSingleton<IMarketFeed>(sp => new SimulatedMarket(
                sp.GetRequiredService<PaperBroker>(), pairSettings, paper, sp.GetRequiredService<ILogger<SimulatedMarket>>()));
        break;
    case BrokerMode.Finam:
        builder.Services
            .AddSingleton<IBrokerGateway>(sp => sp.GetRequiredService<FinamBroker>())
            .AddSingleton<IMarketFeed, NoMarketFeed>();
        break;
}

var host = builder.Build();

var tradingPairs = host.Services.GetRequiredService<IReadOnlyList<TradingPair>>();
try
{
    host.Services.GetRequiredService<RobotWorker>().RestoreState();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Ошибка состояния в базе {Path.GetFileName(dbPath)}: {ex.Message}");
    return 1;
}

if (robot.Mode == BrokerMode.Finam)
{
    try
    {
        await FinamStartupCheck.ValidateAsync(
            host.Services.GetRequiredService<FinamBroker>(), tradingPairs.Where(p => p.IsActive).Select(p => p.Settings),
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FinamStartupCheck"));
    }
    catch (Exception ex) when (ex is InvalidOperationException or Grpc.Core.RpcException)
    {
        Console.Error.WriteLine($"Проверка инструментов Финам не пройдена: {ex.Message}");
        return 1;
    }
}

if (robot.Mode == BrokerMode.Paper)
{
    // Симулятор живёт в памяти: после перезапуска восстанавливаем в нём сохранённые позиции.
    var paperBroker = host.Services.GetRequiredService<PaperBroker>();
    foreach (var p in tradingPairs.Where(p => p.Position is { Units: > 0 }))
    {
        paperBroker.SeedPosition(p.Spec.Stock.Symbol, p.Position!.Units * p.Spec.StockLotsPerUnit, p.Position.StockEntryPrice);
        paperBroker.SeedPosition(p.Position.Futures.Symbol, -p.Position.Units, p.Position.FuturesEntryPrice);
    }
}

host.Run();
return 0;
