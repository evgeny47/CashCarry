using CashCarry.Core.Instruments;

namespace CashCarry.Core.Market;

public readonly record struct Quote
{
    public Quote(decimal bid, decimal ask)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bid);
        if (ask < bid)
            throw new ArgumentException($"Ask {ask} < Bid {bid}.", nameof(ask));
        Bid = bid;
        Ask = ask;
    }

    public decimal Bid { get; }
    public decimal Ask { get; }
}

/// <summary>Котировка фьючерса и текущее ГО на один контракт (руб.).</summary>
public sealed record FuturesMarket(FuturesContract Contract, Quote Quote, decimal InitialMargin);

/// <summary>
/// Срез рынка для принятия решения.
/// Near — ближний контракт (если позиция открыта — именно тот, что в шорте).
/// Next — следующая серия для переноса, может отсутствовать.
/// </summary>
public sealed record MarketSnapshot(
    DateOnly Today,
    Quote Stock,
    FuturesMarket Near,
    FuturesMarket? Next,
    decimal FreeCash,
    IReadOnlyList<DividendEvent>? Dividends = null)
{
    public IReadOnlyList<DividendEvent> Dividends { get; init; } = Dividends ?? [];
}
