using CashCarry.Core.Market;
using CashCarry.Core.Positions;

namespace CashCarry.Core.Tests;

public class PairPositionTests
{
    [Fact]
    public void Add_averages_entry_prices()
    {
        var position = PairPosition.Open(Sber.Dec26, 1, 300m, 31000m, commission: 14m)
            .Add(3, 304m, 31400m, commission: 42m);

        Assert.Equal(4, position.Units);
        Assert.Equal(303m, position.StockEntryPrice);
        Assert.Equal(31300m, position.FuturesEntryPrice);
        Assert.Equal(56m, position.CommissionsPaid);
    }

    [Fact]
    public void Roll_realizes_near_leg_and_switches_contract()
    {
        var position = PairPosition.Open(Sber.Dec26, 2, 300.10m, 31000m, commission: 0m)
            .Roll(Sber.Pair, Sber.Mar27, nearClosePrice: 30530m, nextOpenPrice: 31300m, commission: 8m);

        Assert.Equal(Sber.Mar27, position.Futures);
        Assert.Equal(31300m, position.FuturesEntryPrice);
        Assert.Equal(2 * (31000m - 30530m), position.RealizedPnl);
        Assert.Equal(8m, position.CommissionsPaid);
    }

    [Fact]
    public void Accrued_dividend_counts_in_pnl()
    {
        var noFees = new CommissionSchedule(0m, 0m);
        var position = PairPosition.Open(Sber.Dec26, 1, 300m, 30000m, commission: 0m)
            .AccrueDividend(Sber.Pair, netPerShare: 26.1m);

        // Отсечка прошла: акция подешевела на дивиденд, базис вырос, но начисленный дивиденд это компенсирует (кроме налога).
        decimal pnl = position.NetPnlIfClosed(Sber.Pair, new Quote(270m, 270m), new Quote(27000m, 27000m), noFees);

        Assert.Equal(2610m, pnl);
    }
}
