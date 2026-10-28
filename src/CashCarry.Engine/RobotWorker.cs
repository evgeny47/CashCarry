using System.Globalization;
using CashCarry.Core.Broker;
using CashCarry.Core.Market;
using CashCarry.Core.Positions;
using CashCarry.Core.Strategy;
using CashCarry.Engine.Market;
using CashCarry.Risk;
using CashCarry.Risk.Execution;

namespace CashCarry.Engine;

/// <summary>
/// Главный цикл. Каждый тик: котировки → по каждой паре сверка хеджа и решение стратегии → общая проверка ГО →
/// исполнение (сначала выходы и переносы, потом входы по убыванию доходности) → запись в SQLite.
/// При смене дня — начисление дивидендов на позиции.
/// </summary>
public sealed class RobotWorker(
    IBrokerGateway broker,
    IMarketFeed feed,
    IReadOnlyList<TradingPair> pairs,
    IKillSwitch killSwitch,
    TradingSchedule schedule,
    RobotStore store,
    RobotSettings settings,
    TimeProvider time,
    ILogger<RobotWorker> log) : BackgroundService
{
    private const int MaxConsecutiveErrors = 5;

    private DateOnly? _lastSeenDate;
    private DateTimeOffset _lastStatusLog = DateTimeOffset.MinValue;
    private bool _wasOpen;
    private int _consecutiveErrors;

    /// <summary>
    /// Вызывается до старта цикла: в Paper-режиме позиции нужно восстановить в симуляторе,
    /// а для Финам — проверить инструменты пар, по которым есть позиция.
    /// Позиция по паре, которой нет в настройках, — ошибка запуска: её некому вести.
    /// </summary>
    public void RestoreState()
    {
        string? migrated = store.MigrateLegacyPosition(
            p => pairs.FirstOrDefault(pair => pair.Settings.Find(p.Futures.Symbol) is not null)?.Name);
        if (migrated is not null)
            log.LogInformation("Position from single-pair database moved to pair {Pair}", migrated);

        foreach (var (name, position) in store.LoadPositions())
        {
            var pair = pairs.FirstOrDefault(p => p.Name == name) ?? throw new InvalidOperationException(
                $"в базе открыта позиция по паре {name} ({position.Units} ед., {position.Futures.Symbol}), но такой пары нет в Pairs.");
            if (pair.Settings.Find(position.Futures.Symbol) is null)
                throw new InvalidOperationException(
                    $"позиция пары {name} в контракте {position.Futures.Symbol}, которого нет в её Futures.");
            pair.Position = position;
        }

        _lastSeenDate = store.LoadLastSeenDate();
        if (store.LoadKillSwitch() is { } reason)
            killSwitch.Trip(reason);
        killSwitch.Tripped += reason => store.SaveKillSwitch(reason);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Robot started: mode {Mode}, {Count} pairs", settings.Mode, pairs.Count);
        foreach (var pair in pairs)
        {
            var o = pair.Settings.Strategy;
            log.LogInformation(
                "Pair {Pair}{Disabled}: {Stock}/{Contracts}, exit mode {ExitMode}, entry ≥ {Entry:P1}, hold ≥ {Hold:P1}, " +
                "roll ≥ {Roll:P1}, max units {MaxUnits}. Position: {Position}",
                pair.Name, pair.Settings.Enabled ? "" : " (disabled: no new entries)", pair.Spec.Stock.Symbol,
                string.Join(",", pair.Settings.Futures.Select(f => f.Symbol)), o.ExitMode, o.MinEntryAnnualYield,
                o.MinHoldAnnualYield, o.MinRollAnnualYield, o.MaxUnits, Describe(pair.Position));
        }
        if (settings.DryRun)
            log.LogWarning("DRY RUN: decisions are logged, no orders are sent");
        if (killSwitch.IsTripped)
            log.LogWarning("Kill switch is ACTIVE: {Reason}. New entries and rolls are blocked. " +
                           "Reset: dotnet run --project src/CashCarry.Host -- --reset-kill-switch", killSwitch.Reason);

        using var timer = new PeriodicTimer(settings.PollInterval, time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
                _consecutiveErrors = 0;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _consecutiveErrors++;
                log.LogError(ex, "Tick failed ({Count} in a row)", _consecutiveErrors);
                if (_consecutiveErrors >= MaxConsecutiveErrors)
                    killSwitch.Trip($"{_consecutiveErrors} consecutive tick errors, last: {ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(TradingSchedule.ToMoscow(now).DateTime);
        if (_lastSeenDate != today)
            OnNewDay(today);

        await feed.UpdateAsync(now, ct);

        bool open = schedule.IsOpen(now);
        if (open != _wasOpen)
            log.LogInformation(open ? "Trading window opened" : "Trading window closed");
        _wasOpen = open;
        if (!open)
            return;

        var account = await broker.GetAccountAsync(ct);
        var ticks = new List<PairTick>();
        foreach (var pair in pairs.Where(p => p.IsActive))
        {
            // Сбой одной пары (нет котировки, неверный символ) не должен останавливать остальные.
            try
            {
                if (await EvaluateAsync(pair, today, account, ct) is { } tick)
                    ticks.Add(tick);
                pair.ConsecutiveErrors = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                pair.ConsecutiveErrors++;
                log.LogError(ex, "Pair {Pair} evaluation failed ({Count} in a row)", pair.Name, pair.ConsecutiveErrors);
                if (pair.ConsecutiveErrors >= MaxConsecutiveErrors)
                    killSwitch.Trip($"pair {pair.Name}: {pair.ConsecutiveErrors} consecutive errors, last: {ex.Message}");
            }
        }

        decimal reserve = pairs.Sum(p => p.Reserve);
        var margin = MarginGuard.Assess(account, reserve);
        if (margin == MarginStatus.Critical)
            killSwitch.Trip($"free cash negative: {account.FreeCash:N0}");
        else if (margin == MarginStatus.Warning)
            log.LogWarning("Free cash {Free:N0} below variation margin reserve {Reserve:N0} — entries blocked",
                account.FreeCash, reserve);

        LogStatus(now, ticks, account, reserve);

        // Выходы и переносы освобождают деньги и снимают риск — первыми. Входы — от самой доходной пары:
        // денег на все может не хватить.
        var ordered = ticks
            .Where(t => t.Decision.Kind is DecisionKind.CloseAll or DecisionKind.Roll)
            .Concat(ticks.Where(t => t.Decision.Kind == DecisionKind.Open)
                .OrderByDescending(t => t.Decision.AnnualYield ?? 0m));
        foreach (var tick in ordered)
            await ExecuteAsync(now, tick, margin, ct);
    }

    private async Task<PairTick?> EvaluateAsync(TradingPair pair, DateOnly today, AccountState account, CancellationToken ct)
    {
        var contracts = pair.Contracts(today);
        if (contracts is null)
        {
            log.LogWarning("Pair {Pair}: no active futures in Futures for {Today} — add the next contract", pair.Name, today);
            return null;
        }
        var (nearSettings, nextSettings) = contracts.Value;

        var near = await FuturesMarketAsync(nearSettings, ct);
        FuturesMarket? next = null;
        if (nextSettings is not null)
        {
            // Следующая серия нужна только для переноса и часто неликвидна: без её котировки пара работает дальше,
            // перенос просто откладывается до появления цены.
            try
            {
                next = await FuturesMarketAsync(nextSettings, ct);
            }
            catch (InvalidOperationException ex)
            {
                log.LogDebug("Pair {Pair}: no quote for next contract {Contract}: {Message}", pair.Name, nextSettings.Symbol, ex.Message);
            }
        }
        var stock = await broker.GetQuoteAsync(pair.Spec.Stock.Symbol, ct);
        var snapshot = new MarketSnapshot(today, stock, near, next, account.FreeCash, pair.Dividends);

        var hedge = pair.Hedge.Observe(pair.Position, account, near.Contract.Symbol);
        if (!hedge.IsConsistent)
            log.LogWarning("Pair {Pair}: broker positions differ from robot: stock lots {StockDiff}, futures {FuturesDiff}, net {Net} shares",
                pair.Name, hedge.StockLotsDiff, hedge.FuturesDiff, hedge.NetShares);

        pair.Reserve = pair.Margin.Reserve(pair.Position?.Units ?? 0, near.Quote.Ask);
        return new PairTick(pair, snapshot, pair.Strategy.Evaluate(snapshot, pair.Position));
    }

    private async Task ExecuteAsync(DateTimeOffset now, PairTick tick, MarginStatus margin, CancellationToken ct)
    {
        var (pair, snapshot, decision) = tick;
        if ((decision.Kind is DecisionKind.Open or DecisionKind.Roll) && killSwitch.IsTripped)
            return;
        if (decision.Kind == DecisionKind.Open && (margin != MarginStatus.Ok || !pair.Settings.Enabled))
            return;
        if (decision.Kind == DecisionKind.Open && settings.MaxCapital is { } limit)
        {
            var target = decision.Contract == snapshot.Next?.Contract ? snapshot.Next! : snapshot.Near;
            decimal used = pairs.Sum(p => p.CapitalInUse());
            int allowed = (int)Math.Floor(Math.Max(0m, limit - used) / pair.CapitalPerUnit(snapshot.Stock, target));
            if (allowed <= 0)
            {
                // Решение повторяется каждый тик — пишем не чаще раза в StatusLogInterval.
                if (now - pair.LastCapitalLimitLog >= settings.StatusLogInterval)
                {
                    pair.LastCapitalLimitLog = now;
                    log.LogInformation("Pair {Pair}: entry skipped, capital limit: in use {Used:N0} of {Limit:N0}, unit needs {Unit:N0}",
                        pair.Name, used, limit, pair.CapitalPerUnit(snapshot.Stock, target));
                }
                return;
            }
            if (allowed < decision.Units)
                decision = decision with { Units = allowed };
        }

        if (settings.DryRun)
        {
            LogDryRun(now, pair, decision);
            return;
        }

        log.LogInformation("Pair {Pair} decision {Kind}: {Reason}, units {Units}, contract {Contract}, yield {Yield:P2}",
            pair.Name, decision.Kind, decision.Reason, decision.Units, decision.Contract?.Symbol, decision.AnnualYield);

        decimal otherReserve = pairs.Where(p => p != pair).Sum(p => p.Reserve);
        var result = decision.Kind switch
        {
            DecisionKind.Open => await pair.Executor.OpenAsync(
                pair.Position, decision.Contract == snapshot.Next?.Contract ? snapshot.Next! : snapshot.Near,
                decision.Units, otherReserve, ct),
            DecisionKind.CloseAll => await pair.Executor.CloseAsync(pair.Position!, ct),
            DecisionKind.Roll => await pair.Executor.RollAsync(pair.Position!, decision.Contract!, ct),
            _ => throw new InvalidOperationException($"Unexpected decision {decision.Kind}"),
        };

        Apply(now, pair, decision.Kind.ToString(), result);
        // Следующие входы этого тика должны видеть резерв под только что открытые контракты.
        pair.Reserve = pair.Margin.Reserve(pair.Position?.Units ?? 0, snapshot.Near.Quote.Ask);
    }

    /// <summary>В сухом прогоне позиция не меняется, и стратегия повторяет решение каждый тик — пишем его при смене или раз в StatusLogInterval.</summary>
    private void LogDryRun(DateTimeOffset now, TradingPair pair, StrategyDecision decision)
    {
        string key = $"{decision.Kind}|{decision.Contract?.Symbol}|{decision.Units}";
        if (key == pair.LastDryRunKey && now - pair.LastDryRunLog < settings.StatusLogInterval)
            return;
        pair.LastDryRunKey = key;
        pair.LastDryRunLog = now;
        log.LogInformation("DRY RUN — pair {Pair} would {Kind}: {Reason}, units {Units}, contract {Contract}, yield {Yield:P2}",
            pair.Name, decision.Kind, decision.Reason, decision.Units, decision.Contract?.Symbol, decision.AnnualYield);
    }

    private void Apply(DateTimeOffset now, TradingPair pair, string operation, ExecutionResult result)
    {
        store.Journal(now, pair.Name, operation, result);
        var position = result.Position;

        if (position is { Units: 0 })
        {
            decimal total = position.NetPnlIfClosed(pair.Spec, new Quote(1m, 1m), new Quote(1m, 1m), pair.Commissions);
            log.LogInformation("Pair {Pair} position closed. Total net PnL {Pnl:N2} (realized {Realized:N2}, dividends {Dividends:N2}, commissions {Commissions:N2})",
                pair.Name, total, position.RealizedPnl, position.DividendsAccrued, position.CommissionsPaid);
            position = null;
        }

        pair.Position = position;
        store.SavePosition(pair.Name, position);
        log.LogInformation("Pair {Pair} {Operation} → {Status}: {Message}. Position: {Position}",
            pair.Name, operation, result.Status, result.Message, Describe(position));
    }

    /// <summary>Дивиденды, последний день покупки которых прошёл, пока позиция была открыта.</summary>
    private void OnNewDay(DateOnly today)
    {
        if (_lastSeenDate is { } previous)
        {
            foreach (var pair in pairs.Where(p => p.Position is { Units: > 0 }))
            {
                foreach (var dividend in pair.Dividends.Where(d => d.LastBuyDate >= previous && d.LastBuyDate < today))
                {
                    decimal net = dividend.AmountPerShare * (1m - pair.Settings.Strategy.DividendTaxRate);
                    pair.Position = pair.Position!.AccrueDividend(pair.Spec, net);
                    log.LogInformation("Pair {Pair} dividend accrued: {Net:N2} per share net, last buy date {Date}",
                        pair.Name, net, dividend.LastBuyDate);
                }
                store.SavePosition(pair.Name, pair.Position);
            }
        }

        _lastSeenDate = today;
        store.SaveLastSeenDate(today);
    }

    private async Task<FuturesMarket> FuturesMarketAsync(FuturesSettings futures, CancellationToken ct) =>
        new(futures.ToContract(), await broker.GetQuoteAsync(futures.Symbol, ct), futures.InitialMargin);

    private void LogStatus(DateTimeOffset now, IReadOnlyList<PairTick> ticks, AccountState account, decimal reserve)
    {
        if (now - _lastStatusLog < settings.StatusLogInterval)
            return;
        _lastStatusLog = now;

        foreach (var (pair, m, decision) in ticks)
        {
            decimal openBasis = Basis.ToOpen(pair.Spec, m.Stock, m.Near.Quote);
            decimal closeBasis = Basis.ToClose(pair.Spec, m.Stock, m.Near.Quote);
            decimal? entryYield = m.Near.Contract.LastTradingDay > m.Today ? pair.Strategy.EntryAnnualYield(m, m.Near) : null;
            decimal? pnl = pair.Position?.NetPnlIfClosed(pair.Spec, m.Stock, m.Near.Quote, pair.Commissions);

            log.LogInformation(
                "{Pair}: {Stock} {SBid}/{SAsk} | {Fut} {FBid}/{FAsk} | basis open {Open:N0} close {Close:N0} | entry yield {Yield:P2} | " +
                "position {Position} pnl {Pnl:N2} | {Decision}: {Reason}",
                pair.Name, pair.Spec.Stock.Symbol, m.Stock.Bid, m.Stock.Ask, m.Near.Contract.Symbol, m.Near.Quote.Bid,
                m.Near.Quote.Ask, openBasis, closeBasis, entryYield, Describe(pair.Position), pnl, decision.Kind, decision.Reason);
        }
        if (settings.MaxCapital is { } limit)
            log.LogInformation("Account: free cash {Free:N0}, variation margin reserve {Reserve:N0}, capital in use {Used:N0} of limit {Limit:N0}",
                account.FreeCash, reserve, pairs.Sum(p => p.CapitalInUse()), limit);
        else
            log.LogInformation("Account: free cash {Free:N0}, variation margin reserve {Reserve:N0}", account.FreeCash, reserve);
    }

    private static string Describe(PairPosition? p) => p is null
        ? "flat"
        : string.Create(CultureInfo.InvariantCulture,
            $"{p.Units}u {p.Futures.Symbol} stock@{p.StockEntryPrice:0.##} fut@{p.FuturesEntryPrice:0.##}");

    private sealed record PairTick(TradingPair Pair, MarketSnapshot Snapshot, StrategyDecision Decision);
}
