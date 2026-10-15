using CashCarry.Core.Instruments;
using CashCarry.Core.Market;

namespace CashCarry.Core.Tests;

/// <summary>Общие данные: SBER (лот 10) против SBRF (100 акций), комиссии 0,04 % и 2 ₽/контракт.</summary>
internal static class Sber
{
    public static readonly PairSpec Pair = new(new StockSpec("SBER", 10), sharesPerContract: 100);
    public static readonly CommissionSchedule Commissions = new(stockRate: 0.0004m, futuresPerContract: 2m);

    public static readonly DateOnly Today = new(2026, 10, 8);
    public static readonly FuturesContract Dec26 = new("SBRF-12.26", new DateOnly(2026, 12, 18)); // 71 день
    public static readonly FuturesContract Mar27 = new("SBRF-3.27", new DateOnly(2027, 3, 19));

    public static FuturesMarket Market(FuturesContract c, decimal bid, decimal ask, decimal margin = 5000m) =>
        new(c, new Quote(bid, ask), margin);

    public static MarketSnapshot Snapshot(
        DateOnly today,
        Quote stock,
        FuturesMarket near,
        FuturesMarket? next = null,
        decimal freeCash = 100_000m,
        params DividendEvent[] dividends) =>
        new(today, stock, near, next, freeCash, dividends);
}
