using CashCarry.Broker.Paper;
using CashCarry.Core.Broker;
using CashCarry.Core.Market;
using CashCarry.Engine;
using CashCarry.Risk;
using CashCarry.Risk.Execution;
using Microsoft.Extensions.Logging;

namespace CashCarry.Backtest;

public sealed record BacktestInputs(
    BacktestSettings Settings,
    IReadOnlyList<PairSettings> Pairs,
    RobotSettings Robot,
    CommissionSchedule Commissions,
    RiskLimits Risk,
    ExecutionOptions Execution,
    HistoryStore History,
    string RunDirectory);

public sealed record EquityPoint(DateOnly Date, decimal Equity, decimal CapitalInUse);

public sealed record PairResult(string Name, decimal Pnl, decimal Dividends, int Fills, PairPositionSummary? OpenPosition);

public sealed record PairPositionSummary(int Units, string Futures, decimal UnrealizedPnl);

public sealed record BacktestResult(
    DateTimeOffset FirstTick,
    DateTimeOffset LastTick,
    int Ticks,
    int TradingTicks,
    int WarmupTicks,
    IReadOnlyList<string> WarmupSymbols,
    decimal InitialCash,
    decimal FinalEquity,
    decimal MaxDrawdown,
    decimal MaxDrawdownPercent,
    decimal MaxCapitalInUse,
    decimal ExposureShare,
    IReadOnlyList<EquityPoint> Daily,
    IReadOnlyList<Fill> Fills,
    IReadOnlyList<PairResult> Pairs,
    string? KillSwitchReason,
    string JournalPath)
{
    public decimal Pnl => FinalEquity - InitialCash;
    public decimal Commissions => Fills.Sum(f => f.Commission);
}

/// <summary>
/// Прогон робота на истории: тот же RobotWorker, стратегия, исполнитель, проверки ГО и лимит капитала, что и в бою,
/// только котировки — из свечей, брокер — симулятор, а время двигает тестер. Тик — закрытие каждой свечи любого инструмента.
/// </summary>
public static class BacktestRunner
{
    /// <param name="clock">Часы прогона; те же часы у <paramref name="log"/>, чтобы в логе было время тестера.</param>
    public static async Task<BacktestResult> RunAsync(BacktestInputs inputs, BacktestClock clock, BacktestLog log, CancellationToken ct)
    {
        var (settings, pairSettings, robot) = (inputs.Settings, inputs.Pairs, inputs.Robot);
        var (from, to) = settings.Period();

        var candles = new Dictionary<string, IReadOnlyList<Candle>>();
        foreach (string symbol in HistoryDownloader.Symbols(pairSettings, settings.FromDate))
        {
            var loaded = inputs.History.Load(symbol, settings.Timeframe, from, to);
            if (loaded.Count > 0)
                candles[symbol] = loaded;
        }
        var missingStocks = pairSettings.Where(p => !candles.ContainsKey(p.StockSymbol)).Select(p => p.StockSymbol).ToList();
        if (missingStocks.Count > 0)
            throw new InvalidOperationException(
                $"нет свечей {settings.Timeframe} по {string.Join(", ", missingStocks)} за период — сначала загрузите историю командой download с теми же ключами.");

        clock.Now = from;
        using var loggers = new LoggerFactory([log]);
        var paper = new PaperBroker(
            pairSettings.SelectMany(p => p.Futures
                .Select(f => new PaperInstrument(f.Symbol, InstrumentKind.Futures, 1, p.PointValue, f.InitialMargin))
                .Prepend(new PaperInstrument(p.StockSymbol, InstrumentKind.Stock, p.StockLotSize))),
            inputs.Commissions, settings.InitialCash);
        var broker = new RecordingBroker(paper, clock);
        var killSwitch = new KillSwitch();
        var schedule = robot.IgnoreTradingSchedule ? TradingSchedule.AlwaysOpen() : TradingSchedule.MoexMainSession(robot.Holidays);
        string journalPath = Path.Combine(inputs.RunDirectory, "robot.db");
        var store = new RobotStore(journalPath);

        var pairs = pairSettings
            .Select(p => new TradingPair(p, inputs.Commissions, broker, killSwitch, schedule, inputs.Risk, inputs.Execution, clock, loggers))
            .ToList();
        var feed = new HistoricalFeed(paper, pairs, candles, settings.Timeframe, settings.SpreadTicks);
        robot.Mode = BrokerMode.Paper;
        robot.DryRun = false;
        var worker = new RobotWorker(broker, feed, pairs, killSwitch, schedule, store, robot, clock, loggers.CreateLogger<RobotWorker>());
        worker.RestoreState();

        var duration = settings.Timeframe.Duration();
        var timeline = candles.Values.SelectMany(c => c.Select(x => x.Time + duration)).Where(t => t <= to).Distinct().Order().ToList();
        if (timeline.Count == 0)
            throw new InvalidOperationException("за период нет ни одной свечи.");

        var lotSizes = pairSettings.ToDictionary(p => p.StockSymbol, p => p.StockLotSize);
        var daily = new List<EquityPoint>();
        var warmupSymbols = new SortedSet<string>();
        decimal peak = settings.InitialCash, maxDrawdown = 0m, maxDrawdownPercent = 0m, maxCapital = 0m;
        int tradingTicks = 0, warmupTicks = 0, exposedTicks = 0;
        decimal equity = settings.InitialCash, capital = 0m;

        foreach (var now in timeline)
        {
            ct.ThrowIfCancellationRequested();
            clock.Now = now;
            var today = DateOnly.FromDateTime(TradingSchedule.ToMoscow(now).DateTime);
            if (daily.Count == 0 || daily[^1].Date != today)
                daily.Add(new EquityPoint(today, equity, capital));

            await feed.UpdateAsync(now, ct);
            if (!schedule.IsOpen(now))
                continue;

            // Пока по акции или ближней серии не было ни одной свечи, котировки нет и пара упала бы с ошибкой — ждём.
            // Следующая серия не обязательна: без неё робот просто не переносит позицию.
            var missing = pairs.Where(p => p.IsActive).SelectMany(p => p.QuoteSymbols(today).Take(2)).Where(s => feed.Last(s) is null).ToList();
            if (missing.Count > 0)
            {
                warmupTicks++;
                warmupSymbols.UnionWith(missing);
                continue;
            }

            await worker.TickAsync(ct);
            tradingTicks++;

            equity = Equity(await paper.GetAccountAsync(ct), lotSizes, feed);
            capital = pairs.Sum(p => p.CapitalInUse());
            maxCapital = Math.Max(maxCapital, capital);
            if (pairs.Any(p => p.Position is { Units: > 0 }))
                exposedTicks++;
            peak = Math.Max(peak, equity);
            if (peak - equity > maxDrawdown)
            {
                maxDrawdown = peak - equity;
                maxDrawdownPercent = maxDrawdown / peak;
            }
            daily[^1] = daily[^1] with { Equity = equity, CapitalInUse = capital };
        }

        // Итог — по самым последним ценам (после торгового окна свечи ещё идут), как и результат по парам.
        equity = Equity(await paper.GetAccountAsync(ct), lotSizes, feed);
        if (daily.Count > 0)
            daily[^1] = daily[^1] with { Equity = equity };
        var results = pairs.Select(p => PairPnl(p, broker.Fills, feed)).ToList();
        return new BacktestResult(timeline[0], timeline[^1], timeline.Count, tradingTicks, warmupTicks, warmupSymbols.ToList(),
            settings.InitialCash, equity, maxDrawdown, maxDrawdownPercent, maxCapital,
            tradingTicks == 0 ? 0m : (decimal)exposedTicks / tradingTicks, daily, broker.Fills, results, killSwitch.Reason, journalPath);
    }

    /// <summary>Деньги + вариационная маржа + акции по цене покупателя (bid): столько получилось бы, закрыв всё сейчас.</summary>
    private static decimal Equity(AccountState account, IReadOnlyDictionary<string, int> lotSizes, HistoricalFeed feed) =>
        account.Cash + account.VariationMargin + account.Positions
            .Where(p => lotSizes.ContainsKey(p.Key))
            .Sum(p => p.Value * lotSizes[p.Key] * (feed.Last(p.Key)?.Bid ?? 0m));

    /// <summary>
    /// Результат пары по её сделкам: денежный поток по исполнениям с комиссиями + оценка остатка позиции
    /// (акции по bid, фьючерсы по середине спреда) + полученные дивиденды.
    /// </summary>
    private static PairResult PairPnl(TradingPair pair, IReadOnlyList<Fill> fills, HistoricalFeed feed)
    {
        var s = pair.Settings;
        decimal pnl = 0m;
        int count = 0;
        foreach (var group in fills.Where(f => f.Symbol == s.StockSymbol || s.Find(f.Symbol) is not null).GroupBy(f => f.Symbol))
        {
            bool stock = group.Key == s.StockSymbol;
            decimal multiplier = stock ? s.StockLotSize : s.PointValue;
            int position = 0;
            foreach (var f in group)
            {
                int signed = f.Side == OrderSide.Buy ? f.Quantity : -f.Quantity;
                position += signed;
                pnl -= signed * f.Price * multiplier + f.Commission;
                count++;
            }
            if (position != 0 && feed.Last(group.Key) is { } q)
                pnl += position * multiplier * (stock ? q.Bid : (q.Bid + q.Ask) / 2);
        }

        decimal dividends = feed.DividendsPaid.GetValueOrDefault(pair.Name);
        PairPositionSummary? open = null;
        if (pair.Position is { Units: > 0 } p && feed.Last(s.StockSymbol) is { } stockQuote && feed.Last(p.Futures.Symbol) is { } futuresQuote)
            open = new PairPositionSummary(p.Units, p.Futures.Symbol, p.NetPnlIfClosed(pair.Spec, stockQuote, futuresQuote, pair.Commissions));
        return new PairResult(pair.Name, pnl + dividends, dividends, count, open);
    }
}
