using CashCarry.Broker.Finam;
using CashCarry.Broker.Paper;
using CashCarry.Risk;
using Grpc.Core;

namespace CashCarry.Host.Market;

/// <summary>
/// Paper-счёт на живых ценах: перед каждым тиком берёт котировки Финам и передаёт их симулятору.
/// Объём в стакане не ограничивается. При смене дня — клиринг симулятора.
/// </summary>
public sealed class FinamQuoteFeed(
    PaperBroker paper, FinamBroker finam, IReadOnlyList<TradingPair> pairs, ILogger<FinamQuoteFeed> log) : IMarketFeed
{
    private DateOnly? _today;

    public async Task UpdateAsync(DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TradingSchedule.ToMoscow(now).DateTime);
        if (_today is { } previous && today > previous)
            paper.RunClearing();
        _today = today;

        var symbols = pairs.Where(p => p.IsActive).SelectMany(p => p.QuoteSymbols(today));
        foreach (string symbol in symbols)
        {
            try
            {
                paper.SetQuote(symbol, await finam.GetQuoteAsync(symbol, ct));
            }
            catch (InvalidOperationException ex)
            {
                // Вне торгов двусторонней котировки нет — симулятор держит последнюю.
                log.LogDebug("No Finam quote for {Symbol}: {Message}", symbol, ex.Message);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.ResourceExhausted or StatusCode.Unavailable
                                              or StatusCode.DeadlineExceeded)
            {
                // Лимит запросов или сбой сети: для бумажного счёта достаточно последней цены, тик не роняем.
                log.LogWarning("Finam quote for {Symbol} skipped ({Status}), using last price", symbol, ex.StatusCode);
            }
        }
    }
}
