using CashCarry.Risk;
using CashCarry.Broker.Finam;

namespace CashCarry.Engine;

/// <summary>
/// Перед торговлей сверяет настройки с данными Финам. Расхождение в лотности или размере контракта
/// ломает хедж (не то количество акций на контракт), поэтому это ошибка запуска, а не предупреждение.
/// </summary>
public static class FinamStartupCheck
{
    public static async Task ValidateAsync(FinamBroker finam, IEnumerable<PairSettings> pairs, ILogger log, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var pair in pairs)
            await ValidatePairAsync(finam, pair, errors, log, ct);

        if (errors.Count > 0)
            throw new InvalidOperationException("Настройки не совпадают с данными Финам:\n  " + string.Join("\n  ", errors));
        log.LogInformation("Finam instruments verified: lot sizes and contract sizes match settings");
    }

    private static async Task ValidatePairAsync(FinamBroker finam, PairSettings pair, List<string> errors, ILogger log, CancellationToken ct)
    {
        var stock = await finam.AssetAsync(pair.StockSymbol, ct);
        if (stock.LotSize != pair.StockLotSize)
            errors.Add($"{pair.StockSymbol}: лот у Финам {stock.LotSize}, в настройках {pair.StockLotSize}");
        if (stock.PriceStep != pair.StockPriceStep)
            log.LogWarning("{Symbol}: price step at Finam {Finam}, in settings {Settings}",
                pair.StockSymbol, stock.PriceStep, pair.StockPriceStep);

        foreach (var futures in pair.Active(DateOnly.FromDateTime(DateTime.Today)))
        {
            var asset = await finam.AssetAsync(futures.Symbol, ct);
            if (!asset.IsFutures)
                errors.Add($"{futures.Symbol}: у Финам это не фьючерс");
            else if (asset.ContractSize != pair.SharesPerContract)
                errors.Add($"{futures.Symbol}: размер контракта у Финам {asset.ContractSize}, в настройках {pair.SharesPerContract}");
            if (asset.PriceStep != pair.FuturesPriceStep)
                log.LogWarning("{Symbol}: price step at Finam {Finam}, in settings {Settings}",
                    futures.Symbol, asset.PriceStep, pair.FuturesPriceStep);
            // Финам отдаёт экспирацию как полночь по Москве в UTC (2026-12-16 21:00Z = 17.12 МСК).
            if (asset.Expiration is { } expiration
                && DateOnly.FromDateTime(TradingSchedule.ToMoscow(expiration).DateTime) is var expiry
                && expiry != futures.LastTradingDay)
                log.LogWarning("{Symbol}: expiration at Finam {Finam:yyyy-MM-dd}, LastTradingDay in settings {Settings:yyyy-MM-dd}",
                    futures.Symbol, expiry, futures.LastTradingDay);

            decimal margin = await finam.ShortInitialMarginAsync(futures.Symbol, ct);
            if (margin > 0 && Math.Abs(margin - futures.InitialMargin) > margin * 0.1m)
                log.LogWarning("{Symbol}: initial margin at Finam {Finam:N2}, in settings {Settings:N2} — update Futures of pair {Pair}",
                    futures.Symbol, margin, futures.InitialMargin, pair.Name);
        }
    }
}
