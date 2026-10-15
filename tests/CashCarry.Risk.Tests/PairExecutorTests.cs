using CashCarry.Broker.Paper;
using CashCarry.Core.Instruments;
using CashCarry.Core.Market;
using CashCarry.Core.Positions;
using CashCarry.Risk.Execution;

namespace CashCarry.Risk.Tests;

public class PairExecutorTests
{
    private static readonly PairSpec Pair = new(new StockSpec("SBER", 10), sharesPerContract: 100);
    private static readonly CommissionSchedule Commissions = new(0.0004m, 2m);
    private static readonly FuturesContract Dec26 = new("SBRF-12.26", new DateOnly(2026, 12, 18));
    private static readonly FuturesContract Mar27 = new("SBRF-3.27", new DateOnly(2027, 3, 19));
    private static readonly DateTimeOffset MondayMorning = new(2026, 10, 12, 11, 0, 0, TimeSpan.FromHours(3));

    private readonly PaperBroker _broker;
    private readonly KillSwitch _killSwitch = new();

    public PairExecutorTests()
    {
        _broker = new PaperBroker(
            [
                new PaperInstrument("SBER", InstrumentKind.Stock, LotSize: 10),
                new PaperInstrument(Dec26.Symbol, InstrumentKind.Futures, InitialMargin: 5000m),
                new PaperInstrument(Mar27.Symbol, InstrumentKind.Futures, InitialMargin: 5000m),
            ],
            Commissions,
            initialCash: 200_000m);
        _broker.SetQuote("SBER", new Quote(300.00m, 300.10m));
        _broker.SetQuote(Dec26.Symbol, new Quote(31000m, 31010m));
        _broker.SetQuote(Mar27.Symbol, new Quote(31800m, 31810m));
    }

    private PairExecutor Executor(DateTimeOffset? now = null) => new(
        _broker,
        Pair,
        _killSwitch,
        TradingSchedule.MoexMainSession(),
        new MarginGuard(Pair, Commissions, new RiskLimits()),
        new ExecutionOptions
        {
            OrderTimeout = TimeSpan.FromMilliseconds(20),
            PollInterval = TimeSpan.FromMilliseconds(5),
            MaxAttemptsPerLeg = 2,
        },
        new FixedClock(now ?? MondayMorning));

    private static FuturesMarket Near => new(Dec26, new Quote(31000m, 31010m), 5000m);

    private async Task<(int Sber, int Dec, int Mar)> Positions()
    {
        var a = await _broker.GetAccountAsync();
        return (a.PositionOf("SBER"), a.PositionOf(Dec26.Symbol), a.PositionOf(Mar27.Symbol));
    }

    [Fact]
    public async Task Open_buys_stock_and_shorts_futures()
    {
        var result = await Executor().OpenAsync(null, Near, units: 2);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(2, result.Position!.Units);
        Assert.Equal(300.10m, result.Position.StockEntryPrice);
        Assert.Equal(31000m, result.Position.FuturesEntryPrice);
        Assert.Equal(2 * 12.004m + 4m, result.Position.CommissionsPaid);
        Assert.Equal((20, -2, 0), await Positions());
    }

    [Fact]
    public async Task Rejected_futures_leg_unwinds_stock()
    {
        _broker.RejectNext(Dec26.Symbol, "price out of limits");

        var result = await Executor().OpenAsync(null, Near, units: 2);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Null(result.Position);
        // Купили 200 акций по 300.10, продали по 300.00: −20 ₽ на спреде и 24.008 + 24 комиссии.
        Assert.Equal(-68.008m, result.UnwindPnl);
        Assert.Equal((0, 0, 0), await Positions());
        Assert.False(_killSwitch.IsTripped);
    }

    [Fact]
    public async Task Thin_futures_book_gives_partial_open_with_excess_stock_unwound()
    {
        _broker.SetQuote(Dec26.Symbol, new Quote(31000m, 31010m), bidLots: 1);

        var result = await Executor().OpenAsync(null, Near, units: 2);

        Assert.Equal(ExecutionStatus.Partial, result.Status);
        Assert.Equal(1, result.Units);
        Assert.Equal((10, -1, 0), await Positions());
    }

    [Fact]
    public async Task Incomplete_stock_unit_is_unwound()
    {
        _broker.SetQuote("SBER", new Quote(300.00m, 300.10m), askLots: 15);

        var result = await Executor().OpenAsync(null, Near, units: 2);

        Assert.Equal(ExecutionStatus.Partial, result.Status);
        Assert.Equal(1, result.Position!.Units);
        Assert.Equal((10, -1, 0), await Positions());
    }

    [Fact]
    public async Task Failed_unwind_breaks_and_trips_kill_switch()
    {
        _broker.RejectNext(Dec26.Symbol, "exchange error");
        _broker.SetQuote("SBER", new Quote(300.00m, 300.10m), bidLots: 0);

        var result = await Executor().OpenAsync(null, Near, units: 2);

        Assert.Equal(ExecutionStatus.Broken, result.Status);
        Assert.True(_killSwitch.IsTripped);
        Assert.Equal((20, 0, 0), await Positions()); // голая нога осталась — Host должен поднять тревогу
    }

    [Fact]
    public async Task Kill_switch_blocks_open_but_not_close()
    {
        var opened = await Executor().OpenAsync(null, Near, units: 1);
        _killSwitch.Trip("manual");

        var open = await Executor().OpenAsync(opened.Position, Near, units: 1);
        var close = await Executor().CloseAsync(opened.Position!);

        Assert.Equal(ExecutionStatus.Failed, open.Status);
        Assert.Equal(ExecutionStatus.Completed, close.Status);
        Assert.Equal((0, 0, 0), await Positions());
    }

    [Fact]
    public async Task Does_not_trade_during_clearing()
    {
        var clearing = new DateTimeOffset(2026, 10, 12, 14, 2, 0, TimeSpan.FromHours(3));

        var result = await Executor(clearing).OpenAsync(null, Near, units: 1);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal((0, 0, 0), await Positions());
    }

    [Fact]
    public async Task Close_realizes_basis_pnl()
    {
        var opened = await Executor().OpenAsync(null, Near, units: 2);
        _broker.SetQuote("SBER", new Quote(310.00m, 310.10m));
        _broker.SetQuote(Dec26.Symbol, new Quote(31050m, 31060m));

        var result = await Executor().CloseAsync(opened.Position!);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(0, result.Position!.Units);
        // Акции: 200 × (310 − 300.10) = 1980; фьючерс: 2 × (31000 − 31060) = −120;
        // комиссии: 28.008 вход + 24.8 + 4 выход.
        Assert.Equal(1860m, result.Position.RealizedPnl);
        Assert.Equal(1860m - 56.808m,
            result.Position.NetPnlIfClosed(Pair, new Quote(1m, 1m), new Quote(1m, 1m), Commissions));
        Assert.Equal((0, 0, 0), await Positions());
    }

    [Fact]
    public async Task Roll_moves_short_to_next_contract()
    {
        var opened = await Executor().OpenAsync(null, Near, units: 2);
        _broker.SetQuote(Dec26.Symbol, new Quote(30520m, 30530m));
        _broker.SetQuote(Mar27.Symbol, new Quote(31300m, 31310m));

        var result = await Executor().RollAsync(opened.Position!, Mar27);

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(Mar27, result.Position!.Futures);
        Assert.Equal(31300m, result.Position.FuturesEntryPrice);
        Assert.Equal(2 * (31000m - 30530m), result.Position.RealizedPnl);
        Assert.Equal((20, 0, -2), await Positions());
    }

    [Fact]
    public async Task Rejected_next_leg_rolls_back_to_near()
    {
        var opened = await Executor().OpenAsync(null, Near, units: 2);
        _broker.SetQuote(Dec26.Symbol, new Quote(30520m, 30530m));
        _broker.RejectNext(Mar27.Symbol, "no trading");

        var result = await Executor().RollAsync(opened.Position!, Mar27);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(Dec26, result.Position!.Futures);
        Assert.Equal(2 * (30520m - 30530m), result.Position.RealizedPnl); // выкупили по ask, вернули по bid
        Assert.Equal((20, -2, 0), await Positions());
        Assert.False(_killSwitch.IsTripped);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
