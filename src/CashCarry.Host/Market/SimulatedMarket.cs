using CashCarry.Broker.Paper;
using CashCarry.Core.Market;
using CashCarry.Risk;
using Microsoft.Extensions.Logging;

namespace CashCarry.Host.Market;

/// <summary>
/// Синтетический рынок для Paper, своя цена у каждой пары. Акция — случайное блуждание; фьючерс — справедливая цена
/// S × K × (1 + ставка × дни / 365) − дивиденды до экспирации, где ставка колеблется вокруг AnnualCarry
/// с возвратом к среднему. Так базис то расширяется, то сужается, и видно, как робот входит и выходит.
/// При смене дня — клиринг, а после последнего дня покупки с дивидендом — гэп акции и выплата.
/// </summary>
public sealed class SimulatedMarket : IMarketFeed
{
    private readonly PaperBroker _broker;
    private readonly IReadOnlyList<SimulatedPair> _pairs;
    private readonly PaperSettings _settings;
    private readonly ILogger<SimulatedMarket> _log;
    private readonly Random _random;
    private DateOnly? _today;

    public SimulatedMarket(
        PaperBroker broker,
        IReadOnlyList<PairSettings> pairs,
        PaperSettings settings,
        ILogger<SimulatedMarket> log)
    {
        _broker = broker;
        _settings = settings;
        _log = log;
        _random = settings.Seed is { } seed ? new Random(seed) : new Random();
        _pairs = pairs
            .Select(p => new SimulatedPair(p, settings.StartPrices.GetValueOrDefault(p.Name, settings.StartStockPrice)))
            .ToList();
    }

    public Task UpdateAsync(DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TradingSchedule.ToMoscow(now).DateTime);
        if (_today is { } previous && today > previous)
            OnNewDay(previous, today);
        _today = today;

        foreach (var sim in _pairs)
        {
            var pair = sim.Settings;
            sim.StockMid *= 1m + _settings.StockVolatilityPerTick * Gaussian();
            sim.CarryDeviation = sim.CarryDeviation * (1m - _settings.CarryReversionPerTick)
                + _settings.CarryNoisePerTick * Gaussian();

            _broker.SetQuote(pair.StockSymbol, Spread(sim.StockMid, pair.StockPriceStep));
            foreach (var futures in pair.Active(today))
                _broker.SetQuote(futures.Symbol, Spread(FairFuturesPrice(sim, today, futures), pair.FuturesPriceStep));
        }

        return Task.CompletedTask;
    }

    private void OnNewDay(DateOnly previous, DateOnly today)
    {
        _broker.RunClearing();
        foreach (var sim in _pairs)
        {
            var pair = sim.Settings;
            foreach (var dividend in pair.Dividends.Where(d => d.LastBuyDate >= previous && d.LastBuyDate < today))
            {
                sim.StockMid -= dividend.AmountPerShare;
                _broker.PayDividend(pair.StockSymbol, dividend.AmountPerShare * (1m - pair.Strategy.DividendTaxRate));
                _log.LogInformation("Simulated {Pair} dividend gap {Amount} after last buy date {Date}",
                    pair.Name, dividend.AmountPerShare, dividend.LastBuyDate);
            }
        }
    }

    private decimal FairFuturesPrice(SimulatedPair sim, DateOnly today, FuturesSettings futures)
    {
        int days = futures.LastTradingDay.DayNumber - today.DayNumber;
        decimal carry = _settings.AnnualCarry + sim.CarryDeviation;
        decimal dividends = sim.Settings.Dividends
            .Where(d => d.LastBuyDate >= today && d.LastBuyDate <= futures.LastTradingDay)
            .Sum(d => d.AmountPerShare);
        decimal perShare = sim.StockMid * (1m + carry * days / 365m) - dividends;
        return perShare * sim.Settings.SharesPerContract / sim.Settings.PointValue;
    }

    private static Quote Spread(decimal mid, decimal step)
    {
        decimal bid = Math.Max(step, Math.Floor(mid / step) * step);
        return new Quote(bid, bid + step);
    }

    /// <summary>Нормальное распределение (Бокс–Мюллер).</summary>
    private decimal Gaussian()
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = _random.NextDouble();
        return (decimal)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    private sealed class SimulatedPair(PairSettings settings, decimal startPrice)
    {
        public PairSettings Settings { get; } = settings;
        public decimal StockMid { get; set; } = startPrice;
        public decimal CarryDeviation { get; set; }
    }
}
