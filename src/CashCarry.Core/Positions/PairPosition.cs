using CashCarry.Core.Instruments;
using CashCarry.Core.Market;

namespace CashCarry.Core.Positions;

/// <summary>
/// Открытая позиция cash-and-carry: лонг Units × K акций + шорт Units фьючерсов.
/// Цены входа — средние. Вариационная маржа на итоговый PnL не влияет (это лишь движение денег),
/// поэтому PnL считается от цены входа во фьючерс, а не от клиринговой цены.
/// </summary>
public sealed record PairPosition
{
    public required FuturesContract Futures { get; init; }
    public required int Units { get; init; }
    public required decimal StockEntryPrice { get; init; }
    public required decimal FuturesEntryPrice { get; init; }

    /// <summary>Зафиксированный PnL по закрытым ногам фьючерса (при переносах).</summary>
    public decimal RealizedPnl { get; init; }
    public decimal CommissionsPaid { get; init; }
    /// <summary>Начисленные дивиденды после налога (право получено, выплата может быть позже).</summary>
    public decimal DividendsAccrued { get; init; }

    public static PairPosition Open(
        FuturesContract futures, int units, decimal stockPrice, decimal futuresPrice, decimal commission)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(units);
        return new PairPosition
        {
            Futures = futures,
            Units = units,
            StockEntryPrice = stockPrice,
            FuturesEntryPrice = futuresPrice,
            CommissionsPaid = commission,
        };
    }

    /// <summary>Добор единиц в тот же контракт; цены входа усредняются.</summary>
    public PairPosition Add(int units, decimal stockPrice, decimal futuresPrice, decimal commission)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(units);
        int total = Units + units;
        return this with
        {
            Units = total,
            StockEntryPrice = (StockEntryPrice * Units + stockPrice * units) / total,
            FuturesEntryPrice = (FuturesEntryPrice * Units + futuresPrice * units) / total,
            CommissionsPaid = CommissionsPaid + commission,
        };
    }

    /// <summary>Перенос: откупили ближний фьючерс, продали следующий. Акции не трогаем.</summary>
    public PairPosition Roll(
        PairSpec pair, FuturesContract next, decimal nearClosePrice, decimal nextOpenPrice, decimal commission) =>
        this with
        {
            Futures = next,
            FuturesEntryPrice = nextOpenPrice,
            RealizedPnl = RealizedPnl + Units * pair.PointValue * (FuturesEntryPrice - nearClosePrice),
            CommissionsPaid = CommissionsPaid + commission,
        };

    /// <summary>
    /// Частичное или полное закрытие. PnL закрытой части переходит в RealizedPnl, история позиции
    /// (комиссии, дивиденды) остаётся в ней — так NetPnlIfClosed всегда даёт итог всей кампании.
    /// При Units = 0 позиция закрыта, NetPnlIfClosed = итоговый результат.
    /// </summary>
    public PairPosition Reduce(PairSpec pair, int units, decimal stockExitPrice, decimal futuresExitPrice, decimal commission)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(units);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(units, Units);
        decimal pnl = units * pair.SharesPerContract * (stockExitPrice - StockEntryPrice)
            + units * pair.PointValue * (FuturesEntryPrice - futuresExitPrice);
        return this with
        {
            Units = Units - units,
            RealizedPnl = RealizedPnl + pnl,
            CommissionsPaid = CommissionsPaid + commission,
        };
    }

    /// <summary>Результат вынужденной «размотки» лишней ноги, отнесённый на позицию.</summary>
    public PairPosition RecordUnwind(decimal pnl, decimal commission) =>
        this with { RealizedPnl = RealizedPnl + pnl, CommissionsPaid = CommissionsPaid + commission };

    public PairPosition AccrueDividend(PairSpec pair, decimal netPerShare) =>
        this with { DividendsAccrued = DividendsAccrued + Units * pair.SharesPerContract * netPerShare };

    /// <summary>Чистый PnL, если закрыть обе ноги прямо сейчас по рынку (с комиссией закрытия).</summary>
    public decimal NetPnlIfClosed(PairSpec pair, Quote stock, Quote futures, CommissionSchedule commissions)
    {
        int shares = Units * pair.SharesPerContract;
        decimal stockPnl = shares * (stock.Bid - StockEntryPrice);
        decimal futuresPnl = Units * pair.PointValue * (FuturesEntryPrice - futures.Ask);
        decimal closeCost = commissions.ForStock(stock.Bid, shares) + commissions.ForFutures(Units);
        return stockPnl + futuresPnl + RealizedPnl + DividendsAccrued - CommissionsPaid - closeCost;
    }
}
