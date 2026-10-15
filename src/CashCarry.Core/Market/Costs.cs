namespace CashCarry.Core.Market;

/// <summary>
/// Комиссии брокера и биржи вместе.
/// StockRate — доля от оборота по акциям (0.0004 = 0,04 %); FuturesPerContract — руб. за контракт за сделку.
/// </summary>
public sealed record CommissionSchedule
{
    public CommissionSchedule(decimal stockRate, decimal futuresPerContract)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stockRate);
        ArgumentOutOfRangeException.ThrowIfNegative(futuresPerContract);
        StockRate = stockRate;
        FuturesPerContract = futuresPerContract;
    }

    public decimal StockRate { get; }
    public decimal FuturesPerContract { get; }

    public decimal ForStock(decimal price, int shares) => Math.Abs(price * shares) * StockRate;

    public decimal ForFutures(int contracts) => Math.Abs(contracts) * FuturesPerContract;
}

/// <summary>
/// Дивиденд. LastBuyDate — последний день покупки с правом на дивиденд
/// (при T+1 — торговый день перед датой отсечки).
/// </summary>
public sealed record DividendEvent(DateOnly LastBuyDate, decimal AmountPerShare);

public static class Dividends
{
    /// <summary>Сумма дивидендов после налога на акцию с LastBuyDate в [from; to].</summary>
    public static decimal NetPerShare(
        IEnumerable<DividendEvent> events, DateOnly from, DateOnly to, decimal taxRate) =>
        events.Where(d => d.LastBuyDate >= from && d.LastBuyDate <= to)
              .Sum(d => d.AmountPerShare) * (1m - taxRate);
}
