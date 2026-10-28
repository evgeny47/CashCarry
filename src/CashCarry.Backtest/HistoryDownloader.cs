using CashCarry.Broker.Finam;
using CashCarry.Engine;
using Grpc.Core;

namespace CashCarry.Backtest;

/// <summary>Качает свечи акций и фьючерсов выбранных пар за период и складывает в <see cref="HistoryStore"/>.</summary>
public static class HistoryDownloader
{
    public static async Task<int> RunAsync(
        FinamHistory finam, HistoryStore store, BacktestSettings settings, IReadOnlyList<PairSettings> pairs,
        TextWriter output, CancellationToken ct)
    {
        var (from, to) = settings.Period();
        output.WriteLine($"Загрузка свечей {settings.Timeframe} с {settings.FromDate:dd.MM.yyyy} по {settings.ToDate:dd.MM.yyyy}:");

        int failed = 0;
        foreach (string symbol in Symbols(pairs, settings.FromDate))
        {
            try
            {
                var candles = await finam.CandlesAsync(symbol, settings.Timeframe, from, to, ct);
                store.Save(symbol, settings.Timeframe, candles);
                var (count, first, last) = store.Coverage(symbol, settings.Timeframe);
                output.WriteLine($"  {symbol,-12} загружено {candles.Count,6}, всего в базе {count,6}" +
                                 (first is null ? "" : $" ({Moscow(first.Value)} — {Moscow(last!.Value)} МСК)"));
            }
            catch (RpcException ex)
            {
                failed++;
                output.WriteLine($"  {symbol,-12} ОШИБКА {ex.StatusCode}: {ex.Status.Detail}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Акции пар и их фьючерсы, которые ещё торговались в начале периода.</summary>
    public static IEnumerable<string> Symbols(IEnumerable<PairSettings> pairs, DateOnly from) =>
        pairs.SelectMany(p => p.Futures.Where(f => f.LastTradingDay >= from).Select(f => f.Symbol).Prepend(p.StockSymbol));

    private static string Moscow(DateTimeOffset time) => time.ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm");
}
