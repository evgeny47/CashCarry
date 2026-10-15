using CashCarry.Core.Instruments;
using CashCarry.Core.Market;
using CashCarry.Core.Positions;

namespace CashCarry.Core.Tests;

public class InstrumentsAndBasisTests
{
    [Fact]
    public void Sbrf_contract_hedges_ten_sber_lots()
    {
        Assert.Equal(10, Sber.Pair.StockLotsPerUnit);
    }

    [Fact]
    public void Contract_not_divisible_by_lot_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new PairSpec(new StockSpec("SBER", 10), sharesPerContract: 105));
    }

    [Fact]
    public void Crossed_quote_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Quote(bid: 300.1m, ask: 300m));
    }

    [Fact]
    public void Open_and_close_basis_use_executable_sides()
    {
        var stock = new Quote(300.00m, 300.10m);
        var futures = new Quote(31000m, 31010m);

        Assert.Equal(31000m - 30010m, Basis.ToOpen(Sber.Pair, stock, futures));
        Assert.Equal(31010m - 30000m, Basis.ToClose(Sber.Pair, stock, futures));
    }

    [Theory]
    [InlineData(300.10, 31000, 310.00, 31060, 3)]
    [InlineData(300.10, 31000, 280.00, 28100, 1)]
    [InlineData(250.55, 25600, 251.00, 25000, 7)]
    public void Pnl_without_costs_equals_basis_change(
        decimal entryStockAsk, decimal entryFutBid, decimal exitStockBid, decimal exitFutAsk, int units)
    {
        var noFees = new CommissionSchedule(0m, 0m);
        var entryStock = new Quote(entryStockAsk - 0.01m, entryStockAsk);
        var entryFut = new Quote(entryFutBid, entryFutBid + 1m);
        var exitStock = new Quote(exitStockBid, exitStockBid + 0.01m);
        var exitFut = new Quote(exitFutAsk - 1m, exitFutAsk);

        var position = PairPosition.Open(Sber.Dec26, units, entryStockAsk, entryFutBid, commission: 0m);
        decimal pnl = position.NetPnlIfClosed(Sber.Pair, exitStock, exitFut, noFees);

        decimal basisChange = Basis.ToOpen(Sber.Pair, entryStock, entryFut) - Basis.ToClose(Sber.Pair, exitStock, exitFut);
        Assert.Equal(units * basisChange, pnl);
    }

    [Fact]
    public void Commissions_are_turnover_for_stock_and_per_contract_for_futures()
    {
        Assert.Equal(12.004m, Sber.Commissions.ForStock(300.10m, 100));
        Assert.Equal(6m, Sber.Commissions.ForFutures(-3));
    }

    [Fact]
    public void Dividends_are_net_of_tax_and_filtered_by_last_buy_date()
    {
        DividendEvent[] events =
        [
            new(new DateOnly(2026, 11, 20), 30m),
            new(new DateOnly(2027, 1, 15), 10m),
        ];

        decimal net = Dividends.NetPerShare(events, Sber.Today, Sber.Dec26.LastTradingDay, taxRate: 0.13m);

        Assert.Equal(26.1m, net);
    }
}
