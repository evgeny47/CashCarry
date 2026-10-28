using CashCarry.Broker.Paper;
using CashCarry.Core.Market;
using CashCarry.Engine;
using CashCarry.Engine.Market;
using CashCarry.Risk;

namespace CashCarry.Backtest;

/// <summary>Часы тестера: время двигает прогон, а не система.</summary>
public sealed class BacktestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Подаёт симулятору котировки из свечей. Цена становится известна только после закрытия свечи (без заглядывания
/// вперёд); спред достраивается к close: bid = close − ⌈n/2⌉ шага, ask = bid + n шагов. Свечей нет — котировка прежняя.
/// При смене дня — клиринг и выплата дивидендов, право на которые получено в прошлые дни.
/// </summary>
public sealed class HistoricalFeed : IMarketFeed
{
    private readonly PaperBroker _paper;
    private readonly IReadOnlyList<TradingPair> _pairs;
    private readonly TimeSpan _duration;
    private readonly int _spreadTicks;
    private readonly Dictionary<string, Series> _series = new();
    private DateOnly? _today;

    public HistoricalFeed(PaperBroker paper, IReadOnlyList<TradingPair> pairs, IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles,
        Timeframe timeframe, int spreadTicks)
    {
        _paper = paper;
        _pairs = pairs;
        _duration = timeframe.Duration();
        _spreadTicks = spreadTicks;
        foreach (var pair in pairs)
        {
            _series[pair.Settings.StockSymbol] = new Series(candles[pair.Settings.StockSymbol], pair.Settings.StockPriceStep);
            foreach (var f in pair.Settings.Futures.Where(f => candles.ContainsKey(f.Symbol)))
                _series[f.Symbol] = new Series(candles[f.Symbol], pair.Settings.FuturesPriceStep);
        }
    }

    /// <summary>Дивиденды, зачисленные симулятору, по паре: ₽ после налога.</summary>
    public Dictionary<string, decimal> DividendsPaid { get; } = new();

    public Quote? Last(string symbol) => _series.TryGetValue(symbol, out var s) ? s.Quote : null;

    public Task UpdateAsync(DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TradingSchedule.ToMoscow(now).DateTime);
        if (_today is { } previous && today > previous)
            OnNewDay(previous, today);
        _today = today;

        foreach (var (symbol, series) in _series)
        {
            bool moved = false;
            while (series.Next < series.Candles.Count && series.Candles[series.Next].Time + _duration <= now)
            {
                series.Next++;
                moved = true;
            }
            if (!moved)
                continue;

            decimal close = series.Candles[series.Next - 1].Close;
            decimal bid = Math.Max(series.Step, close - Math.Ceiling(_spreadTicks / 2m) * series.Step);
            var quote = new Quote(bid, bid + _spreadTicks * series.Step);
            series.Quote = quote;
            _paper.SetQuote(symbol, quote);
        }
        return Task.CompletedTask;
    }

    private void OnNewDay(DateOnly previous, DateOnly today)
    {
        // Клиринг — по последним ценам прошлого дня, до новых котировок.
        _paper.RunClearing();
        foreach (var pair in _pairs)
        {
            foreach (var dividend in pair.Dividends.Where(d => d.LastBuyDate >= previous && d.LastBuyDate < today))
            {
                decimal net = dividend.AmountPerShare * (1m - pair.Settings.Strategy.DividendTaxRate);
                decimal before = _paper.GetAccountAsync().Result.Cash;
                _paper.PayDividend(pair.Settings.StockSymbol, net);
                DividendsPaid[pair.Name] = DividendsPaid.GetValueOrDefault(pair.Name) + _paper.GetAccountAsync().Result.Cash - before;
            }
        }
    }

    private sealed class Series(IReadOnlyList<Candle> candles, decimal step)
    {
        public IReadOnlyList<Candle> Candles { get; } = candles;
        public decimal Step { get; } = step;
        public int Next { get; set; }
        public Quote? Quote { get; set; }
    }
}
