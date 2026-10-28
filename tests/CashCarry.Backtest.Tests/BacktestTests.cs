using CashCarry.Core.Broker;
using CashCarry.Core.Market;
using CashCarry.Core.Strategy;
using CashCarry.Engine;
using CashCarry.Risk;
using CashCarry.Risk.Execution;
using Microsoft.Extensions.Logging;

namespace CashCarry.Backtest.Tests;

public sealed class BacktestTests : IDisposable
{
    private static readonly TimeSpan Msk = TimeSpan.FromHours(3);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cashcarry-backtest-" + Guid.NewGuid().ToString("N"));

    public BacktestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static PairSettings Sber() => new()
    {
        Name = "SBER",
        StockSymbol = "SBER@MISX",
        StockLotSize = 1,
        Futures = [new FuturesSettings { Symbol = "SRZ6@RTSX", LastTradingDay = new DateOnly(2026, 12, 17), InitialMargin = 5000m }],
        Strategy = new StrategyOptions { MinEntryAnnualYield = 0.12m, MinHoldAnnualYield = 0.06m, MaxUnits = 1 },
    };

    /// <summary>Минутные свечи дня (по умолчанию понедельник 12.10.2026) с 10:00 МСК, все с одной ценой закрытия, кроме переопределённых.</summary>
    private static List<Candle> Minutes(decimal close, int count, Dictionary<int, decimal>? overrides = null, int day = 12) =>
        Enumerable.Range(0, count).Select(i =>
        {
            decimal c = overrides?.GetValueOrDefault(i, close) ?? close;
            return new Candle(new DateTimeOffset(2026, 10, day, 10, i, 0, Msk), c, c, c, c, 1000m);
        }).ToList();

    private async Task<BacktestResult> RunAsync(PairSettings pair, List<Candle> stock, List<Candle> futures)
    {
        var history = new HistoryStore(Path.Combine(_dir, "history.db"));
        history.Save(pair.StockSymbol, Timeframe.M1, stock);
        history.Save(pair.Futures[0].Symbol, Timeframe.M1, futures);
        var lastDay = DateOnly.FromDateTime(stock[^1].Time.ToOffset(Msk).DateTime);
        var settings = new BacktestSettings { From = new DateOnly(2026, 10, 12), To = lastDay, InitialCash = 100_000m };
        var clock = new BacktestClock(DateTimeOffset.MinValue);
        var inputs = new BacktestInputs(settings, [pair], new RobotSettings(), new CommissionSchedule(0.0004m, 2m),
            new RiskLimits(), new ExecutionOptions(), history, _dir);
        return await BacktestRunner.RunAsync(inputs, clock, new BacktestLog(clock, LogLevel.None, TextWriter.Null), CancellationToken.None);
    }

    [Fact]
    public async Task Robot_enters_on_history_and_equity_matches_trades()
    {
        // Базис 1000 на ~35 тыс. капитала за 66 дней ≈ 15 % годовых — выше порога 12 %.
        var result = await RunAsync(Sber(), Minutes(300m, 30), Minutes(31000m, 30, new() { [20] = 31050m, [21] = 31050m }));

        Assert.Equal(2, result.Fills.Count);
        Assert.Null(result.KillSwitchReason);
        Assert.Equal(result.Pnl, result.Pairs.Single().Pnl, 2);
        Assert.Equal(1, result.Pairs.Single().OpenPosition?.Units);
    }

    [Fact]
    public async Task Trades_only_in_window_at_price_of_closed_candle()
    {
        // Свеча 10:04 закрывается в 10:05 — первый тик торгового окна; свеча 10:05 (цена 301) ещё не закрыта.
        var result = await RunAsync(Sber(), Minutes(300m, 30, new() { [5] = 301m }), Minutes(31000m, 30));

        var buy = result.Fills.Single(f => f.Symbol == "SBER@MISX");
        var sell = result.Fills.Single(f => f.Symbol == "SRZ6@RTSX");
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 10, 5, 0, Msk), buy.Time);
        Assert.Equal(300m, buy.Price);    // спред 1 шаг: купить по close
        Assert.Equal(30999m, sell.Price); // продать на шаг ниже close
        Assert.Equal(OrderSide.Sell, sell.Side);
    }

    [Fact]
    public async Task Dividend_is_paid_to_position_held_through_record_date()
    {
        var pair = Sber();
        pair.Dividends.Add(new DividendSettings { LastBuyDate = new DateOnly(2026, 10, 12), AmountPerShare = 10m });
        // После отсечки акция дешевеет на дивиденд, фьючерс — нет: без выплаты пара показала бы убыток 1000 ₽.
        var stock = Minutes(300m, 30).Concat(Minutes(290m, 30, day: 13)).ToList();
        var futures = Minutes(30000m, 30).Concat(Minutes(30000m, 30, day: 13)).ToList();

        var result = await RunAsync(pair, stock, futures);

        var sber = result.Pairs.Single();
        Assert.Equal(870m, sber.Dividends); // 100 акций × 10 ₽ × (1 − 13 % налога)
        Assert.Equal(result.Pnl, sber.Pnl, 2);
    }

    [Fact]
    public async Task Missing_stock_history_is_reported()
    {
        var history = new HistoryStore(Path.Combine(_dir, "history.db"));
        var settings = new BacktestSettings { From = new DateOnly(2026, 10, 12), To = new DateOnly(2026, 10, 12) };
        var clock = new BacktestClock(DateTimeOffset.MinValue);
        var inputs = new BacktestInputs(settings, [Sber()], new RobotSettings(), new CommissionSchedule(0.0004m, 2m),
            new RiskLimits(), new ExecutionOptions(), history, _dir);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BacktestRunner.RunAsync(inputs, clock, new BacktestLog(clock, LogLevel.None, TextWriter.Null), CancellationToken.None));
        Assert.Contains("download", ex.Message);
    }

    [Fact]
    public void History_store_round_trips_and_overwrites()
    {
        var store = new HistoryStore(Path.Combine(_dir, "history.db"));
        var t = new DateTimeOffset(2026, 10, 12, 10, 0, 0, Msk);
        store.Save("SBER@MISX", Timeframe.M1, [new Candle(t, 1.1m, 2.2m, 0.5m, 1.25m, 10m)]);
        store.Save("SBER@MISX", Timeframe.M1, [new Candle(t, 1.1m, 2.2m, 0.5m, 1.3m, 12m), new Candle(t.AddMinutes(1), 1m, 1m, 1m, 1m, 1m)]);

        var loaded = store.Load("SBER@MISX", Timeframe.M1, t, t.AddMinutes(1));

        Assert.Equal([new Candle(t, 1.1m, 2.2m, 0.5m, 1.3m, 12m)], loaded);
        Assert.Equal(2, store.Coverage("SBER@MISX", Timeframe.M1).Count);
        Assert.Equal(0, store.Coverage("SBER@MISX", Timeframe.M5).Count);
    }

    [Fact]
    public void Expired_quarterly_series_are_generated_before_configured_ones()
    {
        var pair = Sber();

        var added = ExpiredFutures.AddTo([pair], new DateOnly(2026, 3, 1));

        Assert.Equal(["SRH6@RTSX", "SRM6@RTSX", "SRU6@RTSX"], added);
        Assert.Equal(new DateOnly(2026, 9, 17), pair.Find("SRU6@RTSX")!.LastTradingDay);
        Assert.Equal(new DateOnly(2026, 3, 19), pair.Find("SRH6@RTSX")!.LastTradingDay);
        Assert.Equal(5000m, pair.Find("SRM6@RTSX")!.InitialMargin);
    }

    [Fact]
    public void Selected_pairs_are_enabled_and_unknown_names_rejected()
    {
        var disabled = Sber();
        disabled.Enabled = false;

        Assert.Empty(new BacktestSettings().Select([disabled]));
        Assert.True(new BacktestSettings { Pairs = "sber" }.Select([disabled]).Single().Enabled);
        Assert.Throws<InvalidOperationException>(() => new BacktestSettings { Pairs = "GAZP" }.Select([Sber()]));
    }
}
