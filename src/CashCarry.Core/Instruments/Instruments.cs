namespace CashCarry.Core.Instruments;

/// <summary>Акция. LotSize — штук в одном лоте (SBER = 10), PriceStep — шаг цены.</summary>
public sealed record StockSpec
{
    public StockSpec(string symbol, int lotSize, decimal priceStep = 0.01m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lotSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priceStep);
        Symbol = symbol;
        LotSize = lotSize;
        PriceStep = priceStep;
    }

    public string Symbol { get; }
    public int LotSize { get; }
    public decimal PriceStep { get; }
}

/// <summary>Конкретный фьючерсный контракт серии (SBRF-12.26 и т.п.).</summary>
public sealed record FuturesContract
{
    public FuturesContract(string symbol, DateOnly lastTradingDay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        Symbol = symbol;
        LastTradingDay = lastTradingDay;
    }

    public string Symbol { get; }
    public DateOnly LastTradingDay { get; }
}

/// <summary>
/// Пара «акция + фьючерс на неё». Одна единица пары (unit) = 1 фьючерсный контракт
/// против SharesPerContract акций (SBRF: 100 акций = 10 лотов SBER).
/// PointValue — рублей за 1 пункт цены фьючерса (для SBRF = 1), FuturesPriceStep — шаг цены фьючерса.
/// </summary>
public sealed record PairSpec
{
    public PairSpec(StockSpec stock, int sharesPerContract, decimal pointValue = 1m, decimal futuresPriceStep = 1m)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sharesPerContract);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pointValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(futuresPriceStep);
        if (sharesPerContract % stock.LotSize != 0)
            throw new ArgumentException(
                $"Контракт ({sharesPerContract} шт.) не делится на лоты {stock.Symbol} ({stock.LotSize} шт.): точный хедж невозможен.",
                nameof(sharesPerContract));

        Stock = stock;
        SharesPerContract = sharesPerContract;
        PointValue = pointValue;
        FuturesPriceStep = futuresPriceStep;
    }

    public StockSpec Stock { get; }
    public int SharesPerContract { get; }
    public decimal PointValue { get; }
    public decimal FuturesPriceStep { get; }

    /// <summary>Лотов акции на один контракт.</summary>
    public int StockLotsPerUnit => SharesPerContract / Stock.LotSize;
}
