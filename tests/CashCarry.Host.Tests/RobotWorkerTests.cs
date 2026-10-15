using CashCarry.Broker.Paper;
using CashCarry.Core.Market;
using CashCarry.Core.Strategy;
using CashCarry.Host.Market;
using CashCarry.Risk;
using CashCarry.Risk.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashCarry.Host.Tests;

public sealed class RobotWorkerTests : IDisposable
{
    private static readonly DateTimeOffset MondayMorning = new(2026, 10, 12, 11, 0, 0, TimeSpan.FromHours(3));
    private static readonly CommissionSchedule Commissions = new(0.0004m, 2m);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cashcarry-test-{Guid.NewGuid():N}.db");
    private readonly KillSwitch _killSwitch = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private static PairSettings Pair(string name, string futures, decimal margin, bool enabled = true) => new()
    {
        Name = name,
        Enabled = enabled,
        StockSymbol = name,
        StockLotSize = 10,
        Futures = [new FuturesSettings { Symbol = futures, LastTradingDay = new DateOnly(2026, 12, 17), InitialMargin = margin }],
        Strategy = new StrategyOptions { MinEntryAnnualYield = 0.12m, MinHoldAnnualYield = 0.06m, MaxUnits = 1 },
    };

    // До экспирации 66 дней. SBER: базис 980 на капитал ~35 тыс. ≈ 15 % годовых; GAZP: базис 715 на ~15,5 тыс. ≈ 25 %.
    private static readonly Dictionary<string, Quote> Quotes = new()
    {
        ["SBER"] = new Quote(300.00m, 300.10m),
        ["SRZ6"] = new Quote(30990m, 31000m),
        ["GAZP"] = new Quote(130.00m, 130.01m),
        ["GZZ6"] = new Quote(13716m, 13717m),
    };

    private static PaperBroker Broker(decimal cash, IEnumerable<PairSettings> pairs)
    {
        var broker = new PaperBroker(
            pairs.SelectMany(p => p.Futures
                .Select(f => new PaperInstrument(f.Symbol, InstrumentKind.Futures, InitialMargin: f.InitialMargin))
                .Prepend(new PaperInstrument(p.StockSymbol, InstrumentKind.Stock, LotSize: p.StockLotSize))),
            Commissions,
            cash);
        var known = pairs.SelectMany(p => p.Futures.Select(f => f.Symbol).Prepend(p.StockSymbol)).ToHashSet();
        foreach (var (symbol, quote) in Quotes.Where(q => known.Contains(q.Key)))
            broker.SetQuote(symbol, quote);
        return broker;
    }

    private (RobotWorker Worker, IReadOnlyList<TradingPair> Pairs, RobotStore Store) Worker(
        PaperBroker broker, params PairSettings[] settings) => Worker(broker, null, settings);

    private (RobotWorker Worker, IReadOnlyList<TradingPair> Pairs, RobotStore Store) Worker(
        PaperBroker broker, decimal? maxCapital, params PairSettings[] settings)
    {
        var time = new FixedClock(MondayMorning);
        var schedule = TradingSchedule.AlwaysOpen();
        var execution = new ExecutionOptions { OrderTimeout = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.FromMilliseconds(5) };
        var pairs = settings
            .Select(s => new TradingPair(s, Commissions, broker, _killSwitch, schedule, new RiskLimits(), execution, time,
                NullLoggerFactory.Instance))
            .ToList();
        var store = new RobotStore(_dbPath);
        var worker = new RobotWorker(broker, new NoMarketFeed(), pairs, _killSwitch, schedule, store,
            new RobotSettings { MaxCapital = maxCapital }, time, NullLogger<RobotWorker>.Instance);
        worker.RestoreState();
        return (worker, pairs, store);
    }

    [Fact]
    public async Task When_cash_is_short_the_higher_yield_pair_enters_first()
    {
        // 40 тыс. хватает на одну единицу любой пары, но не на обе.
        PairSettings[] settings = [Pair("SBER", "SRZ6", 5000m), Pair("GAZP", "GZZ6", 2500m)];
        var broker = Broker(40_000m, settings);
        var (worker, pairs, store) = Worker(broker, settings);

        await worker.TickAsync(CancellationToken.None);

        var account = await broker.GetAccountAsync();
        Assert.Equal(10, account.PositionOf("GAZP"));
        Assert.Equal(-1, account.PositionOf("GZZ6"));
        Assert.Equal(0, account.PositionOf("SBER"));
        Assert.Equal(1, pairs[1].Position?.Units);
        Assert.Null(pairs[0].Position);
        Assert.Equal(1, store.LoadPosition("GAZP")?.Units);
        Assert.False(_killSwitch.IsTripped);
    }

    [Fact]
    public async Task Disabled_pair_does_not_enter()
    {
        PairSettings[] settings = [Pair("SBER", "SRZ6", 5000m), Pair("GAZP", "GZZ6", 2500m, enabled: false)];
        var broker = Broker(40_000m, settings);
        var (worker, pairs, _) = Worker(broker, settings);

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(1, pairs[0].Position?.Units);
        Assert.Null(pairs[1].Position);
        Assert.Equal(0, (await broker.GetAccountAsync()).PositionOf("GAZP"));
    }

    [Fact]
    public async Task Capital_limit_blocks_entry_that_does_not_fit()
    {
        // Денег на счёте хватает на обе пары, но лимит 50 тыс.: GAZP (≈ 16,9 тыс. на единицу) входит первой,
        // SBER (≈ 38,1 тыс.) в остаток лимита уже не помещается.
        PairSettings[] settings = [Pair("SBER", "SRZ6", 5000m), Pair("GAZP", "GZZ6", 2500m)];
        var broker = Broker(200_000m, settings);
        var (worker, pairs, _) = Worker(broker, 50_000m, settings);

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(1, pairs[1].Position?.Units);
        Assert.Null(pairs[0].Position);
    }

    [Fact]
    public async Task Capital_limit_caps_units_per_entry()
    {
        var sber = Pair("SBER", "SRZ6", 5000m);
        sber.Strategy.UnitsPerEntry = 3;
        sber.Strategy.MaxUnits = 3;
        var broker = Broker(200_000m, [sber]);
        var (worker, pairs, _) = Worker(broker, 80_000m, sber);

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(2, pairs[0].Position?.Units); // 3 × 38,1 тыс. не влезает в 80 тыс., 2 — влезает
    }

    [Fact]
    public void Only_stock_near_and_next_contracts_are_quoted()
    {
        var settings = Pair("SBER", "SRZ6", 5000m);
        settings.Futures.Add(new FuturesSettings { Symbol = "SRH7", LastTradingDay = new DateOnly(2027, 3, 18), InitialMargin = 6000m });
        settings.Futures.Add(new FuturesSettings { Symbol = "SRM7", LastTradingDay = new DateOnly(2027, 6, 17), InitialMargin = 6000m });
        var (_, pairs, _) = Worker(Broker(40_000m, [settings]), settings);

        Assert.Equal(["SBER", "SRZ6", "SRH7"], pairs[0].QuoteSymbols(new DateOnly(2026, 10, 12)));
        Assert.Equal(["SBER", "SRH7", "SRM7"], pairs[0].QuoteSymbols(new DateOnly(2026, 12, 18)));
    }

    [Fact]
    public void Stored_position_of_unknown_pair_stops_startup()
    {
        var sber = Pair("SBER", "SRZ6", 5000m);
        new RobotStore(_dbPath).SavePosition("GAZP",
            Core.Positions.PairPosition.Open(new("GZZ6", new DateOnly(2026, 12, 17)), 1, 130m, 13716m, 0m));

        var ex = Assert.Throws<InvalidOperationException>(
            () => Worker(Broker(40_000m, [sber, Pair("GAZP", "GZZ6", 2500m)]), sber));

        Assert.Contains("GAZP", ex.Message);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
