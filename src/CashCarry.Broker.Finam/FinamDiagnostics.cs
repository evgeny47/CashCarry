using Grpc.Core;
using Grpc.Tradeapi.V1.Accounts;
using Grpc.Tradeapi.V1.Assets;
using static CashCarry.Broker.Finam.FinamConvert;

namespace CashCarry.Broker.Finam;

/// <summary>Проверка подключения и поиск символов — для ручного запуска из командной строки, ничего не торгует.</summary>
public static class FinamDiagnostics
{
    public static async Task CheckAsync(
        FinamConnection connection, FinamBroker broker, IEnumerable<string> symbols, TextWriter output, CancellationToken ct = default)
    {
        await Section(output, "Токен", async () =>
        {
            var details = await connection.TokenDetailsAsync(ct);
            output.WriteLine($"  действует до:  {details.ExpiresAt.ToDateTimeOffset():u}");
            output.WriteLine($"  только чтение: {(details.Readonly ? "да" : "нет")}");
            output.WriteLine($"  счета:         {string.Join(", ", details.AccountIds)}");
            // Нужны только площадки MOEX: MISX — акции, RTSX — срочный рынок.
            foreach (var p in details.MdPermissions.Where(p => p.Mic is "MISX" or "RTSX"))
                output.WriteLine($"  данные {p.Mic}:   {p.QuoteLevel}, задержка {p.DelayMinutes} мин");
            output.WriteLine($"  всего площадок с данными: {details.MdPermissions.Count}");

            if (string.IsNullOrWhiteSpace(connection.Options.AccountId) && details.AccountIds.Count == 1)
            {
                connection.Options.AccountId = details.AccountIds[0];
                output.WriteLine($"  Finam:AccountId не задан — для проверки взят единственный счёт {details.AccountIds[0]}");
            }
        });

        string accountId = connection.Options.AccountId;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            output.WriteLine("\nFinam:AccountId не задан — впишите один из счетов выше в appsettings.json.");
        }
        else
        {
            await Section(output, $"Счёт {accountId}", async () =>
            {
                var account = await connection.Accounts.GetAccountAsync(new GetAccountRequest { AccountId = accountId }, connection.Call(ct));
                output.WriteLine($"  тип/статус:    {account.Type} / {account.Status}, портфель {account.PortfolioCase}");
                output.WriteLine($"  оценка:        {ToDecimal(account.Equity):N2}");
                foreach (var money in account.Cash)
                    output.WriteLine($"  деньги:        {ToDecimal(money):N2} {money.CurrencyCode}");
                if (account.PortfolioMc is { } mc)
                    output.WriteLine($"  MC: доступно {ToDecimal(mc.AvailableCash):N2}, нач. маржа {ToDecimal(mc.InitialMargin):N2}, мин. маржа {ToDecimal(mc.MaintenanceMargin):N2}");
                if (account.PortfolioForts is { } forts)
                    output.WriteLine($"  FORTS: доступно {ToDecimal(forts.AvailableCash):N2}, резерв {ToDecimal(forts.MoneyReserved):N2}");
                foreach (var p in account.Positions)
                    output.WriteLine($"  позиция:       {p.Symbol} {ToDecimal(p.Quantity)} шт. по {ToDecimal(p.AveragePrice)}");
            });
        }

        foreach (string symbol in symbols)
        {
            await Section(output, $"Инструмент {symbol}", async () =>
            {
                var asset = await broker.AssetAsync(symbol, ct);
                output.WriteLine(asset.IsFutures
                    ? $"  lot_size:      {asset.LotSize} (у фьючерса это множитель, заявка — в контрактах)"
                    : $"  лот:           {asset.LotSize} шт.");
                output.WriteLine($"  шаг цены:      {asset.PriceStep}");
                if (asset.IsFutures)
                    output.WriteLine($"  контракт:      {asset.ContractSize} ед. базового актива, экспирация {asset.Expiration:u}");
                var p = await connection.Assets.GetAssetParamsAsync(
                    new GetAssetParamsRequest { Symbol = symbol, AccountId = connection.Options.AccountId }, connection.Call(ct));
                output.WriteLine($"  торговый лот:  {p.TradeLotSize} (шаг количества в заявке)");
                output.WriteLine($"  торгуется:     {p.IsTradable}, лонг {p.Longable?.Value}, шорт {p.Shortable?.Value}");
                if (p.LongInitialMargin is { } lim && p.ShortInitialMargin is { } sim)
                    output.WriteLine($"  ГО лонг/шорт:  {ToDecimal(lim):N2} / {ToDecimal(sim):N2} {sim.CurrencyCode}");

                var quote = await connection.MarketData.LastQuoteAsync(
                    new Grpc.Tradeapi.V1.Marketdata.QuoteRequest { Symbol = symbol }, connection.Call(ct));
                var q = quote.Quote;
                output.WriteLine($"  котировка:     {ToDecimal(q.Bid)} x {ToDecimal(q.BidSize)} / {ToDecimal(q.Ask)} x {ToDecimal(q.AskSize)}, последняя {ToDecimal(q.Last)} x {ToDecimal(q.LastSize)}");
            });
        }
    }

    /// <summary>Ищет активные инструменты, у которых тикер или название содержат строку.</summary>
    public static async Task FindSymbolsAsync(FinamConnection connection, string query, TextWriter output, CancellationToken ct = default)
    {
        const int maxResults = 50;
        int found = 0;
        long cursor = 0;
        do
        {
            var page = await connection.Assets.AllAssetsAsync(
                new AllAssetsRequest { Cursor = cursor, OnlyActive = true }, connection.Call(ct));
            foreach (var a in page.Assets.Where(a =>
                         a.Ticker.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine($"  {a.Symbol,-20} {a.Type,-10} {a.Name}");
                if (++found >= maxResults)
                {
                    output.WriteLine($"  ... показаны первые {maxResults}, уточните запрос");
                    return;
                }
            }
            cursor = page.NextCursor;
        }
        while (cursor != 0);

        if (found == 0)
            output.WriteLine("  ничего не найдено");
    }

    private static async Task Section(TextWriter output, string title, Func<Task> body)
    {
        output.WriteLine($"\n{title}:");
        try
        {
            await body();
        }
        catch (RpcException ex)
        {
            output.WriteLine($"  ОШИБКА {ex.StatusCode}: {ex.Status.Detail}");
        }
        catch (InvalidOperationException ex)
        {
            output.WriteLine($"  ОШИБКА: {ex.Message}");
        }
    }
}
