using CashCarry.Core.Broker;
using CashCarry.Core.Instruments;
using CashCarry.Core.Market;
using CashCarry.Core.Positions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashCarry.Risk.Execution;

public sealed class ExecutionOptions
{
    /// <summary>Насколько хуже лучшей цены ставим «агрессивный лимит», доля цены.</summary>
    public decimal MaxSlippage { get; set; } = 0.002m;
    public TimeSpan OrderTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    public int MaxAttemptsPerLeg { get; set; } = 3;
}

public enum ExecutionStatus
{
    Completed,
    /// <summary>Исполнено меньше единиц, чем просили; всё исполненное захеджировано.</summary>
    Partial,
    /// <summary>Ничего не исполнено (или откатано); позиция прежняя.</summary>
    Failed,
    /// <summary>Остались непокрытые ноги или состояние неизвестно. Kill switch сработал, нужна ручная сверка.</summary>
    Broken,
}

/// <summary>
/// Итог операции. Position — позиция после неё (Units = 0 — закрыта полностью).
/// UnwindPnl — чистый результат вынужденных откатов лишней ноги (обычно убыток на спреде и комиссиях).
/// </summary>
public sealed record ExecutionResult(
    ExecutionStatus Status, PairPosition? Position, int Units, decimal UnwindPnl, string Message);

/// <summary>
/// Исполняет решения стратегии двумя ногами с контролем ноги.
/// Порядок: сначала акция, затем фьючерс ровно на исполненное количество целых единиц;
/// лишние лоты акции (неполная единица или недохедж) сразу откатываются обратной сделкой.
/// Если откат не удался — Broken и kill switch.
/// Отмена через CancellationToken действует только до первой заявки: начатую пару не бросаем.
/// </summary>
public sealed class PairExecutor
{
    private readonly IBrokerGateway _broker;
    private readonly PairSpec _pair;
    private readonly IKillSwitch _killSwitch;
    private readonly TradingSchedule _schedule;
    private readonly MarginGuard _marginGuard;
    private readonly ExecutionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public PairExecutor(
        IBrokerGateway broker,
        PairSpec pair,
        IKillSwitch killSwitch,
        TradingSchedule schedule,
        MarginGuard marginGuard,
        ExecutionOptions options,
        TimeProvider? time = null,
        ILogger<PairExecutor>? logger = null)
    {
        _broker = broker;
        _pair = pair;
        _killSwitch = killSwitch;
        _schedule = schedule;
        _marginGuard = marginGuard;
        _options = options;
        _time = time ?? TimeProvider.System;
        _log = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Открыть или добрать units единиц. otherReserve — резерв под ВМ остальных пар: деньги на счёте общие.</summary>
    public async Task<ExecutionResult> OpenAsync(
        PairPosition? position, FuturesMarket futures, int units, decimal otherReserve = 0m, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(units);
        if (position is { Units: > 0 } && position.Futures != futures.Contract)
            throw new ArgumentException("Добор возможен только в контракт открытой позиции.", nameof(futures));
        if (Blocked(newRisk: true) is { } reason)
            return Failed(reason, position);

        var account = await _broker.GetAccountAsync(ct);
        var stockQuote = await _broker.GetQuoteAsync(_pair.Stock.Symbol, ct);
        var futuresQuote = await _broker.GetQuoteAsync(futures.Contract.Symbol, ct);
        int allowed = _marginGuard.MaxUnitsToOpen(
            account, stockQuote, futuresQuote, futures.InitialMargin, position?.Units ?? 0, otherReserve);
        if (allowed <= 0)
            return Failed("insufficient free cash incl. variation margin reserve", position);
        if (allowed < units)
            _log.LogWarning("Open {Requested} units limited by margin to {Allowed}", units, allowed);
        units = Math.Min(units, allowed);

        return await GuardAsync("open", position, async () =>
        {
            var fill = await TradePairAsync(OrderSide.Buy, units, futures.Contract.Symbol);
            var result = position;
            if (fill.Units > 0)
                result = result is { Units: > 0 }
                    ? result.Add(fill.Units, fill.StockPrice, fill.FuturesPrice, fill.Commission)
                    : PairPosition.Open(futures.Contract, fill.Units, fill.StockPrice, fill.FuturesPrice, fill.Commission);
            return Finish("open", units, fill, result);
        });
    }

    /// <summary>Закрыть позицию целиком. Kill switch закрытие не блокирует.</summary>
    public async Task<ExecutionResult> CloseAsync(PairPosition position, CancellationToken ct = default)
    {
        if (position.Units <= 0)
            return Failed("nothing to close", position);
        if (Blocked(newRisk: false) is { } reason)
            return Failed(reason, position);
        ct.ThrowIfCancellationRequested();

        return await GuardAsync("close", position, async () =>
        {
            var fill = await TradePairAsync(OrderSide.Sell, position.Units, position.Futures.Symbol);
            var result = fill.Units > 0
                ? position.Reduce(_pair, fill.Units, fill.StockPrice, fill.FuturesPrice, fill.Commission)
                : position;
            return Finish("close", position.Units, fill, result);
        });
    }

    /// <summary>
    /// Перенос шорта в следующую серию. Всё или ничего: при сбое любой ноги возвращаемся к шорту ближнего,
    /// чтобы позиция не размазалась по двум сериям.
    /// </summary>
    public async Task<ExecutionResult> RollAsync(PairPosition position, FuturesContract next, CancellationToken ct = default)
    {
        if (position.Units <= 0)
            return Failed("nothing to roll", position);
        if (Blocked(newRisk: true) is { } reason)
            return Failed(reason, position);
        ct.ThrowIfCancellationRequested();

        int units = position.Units;
        string nearSymbol = position.Futures.Symbol;
        decimal step = _pair.FuturesPriceStep;

        return await GuardAsync("roll", position, async () =>
        {
            var near = await ExecuteLegAsync(nearSymbol, OrderSide.Buy, units, step);
            if (near.Filled < units)
            {
                var restore = await ExecuteLegAsync(nearSymbol, OrderSide.Sell, near.Filled, step);
                var restored = position.RecordUnwind(
                    FuturesSpread(restore.AvgPrice, near.AvgPrice, restore.Filled), near.Commission + restore.Commission);
                return restore.Filled < near.Filled
                    ? Broken($"roll: near leg bought {near.Filled}/{units}, restored only {restore.Filled}", restored)
                    : Failed($"roll: near leg bought {near.Filled}/{units}, restored", restored);
            }

            var sold = await ExecuteLegAsync(next.Symbol, OrderSide.Sell, units, step);
            if (sold.Filled < units)
            {
                var undoNext = await ExecuteLegAsync(next.Symbol, OrderSide.Buy, sold.Filled, step);
                var restoreNear = await ExecuteLegAsync(nearSymbol, OrderSide.Sell, units, step);
                var restored = position.RecordUnwind(
                    FuturesSpread(restoreNear.AvgPrice, near.AvgPrice, restoreNear.Filled)
                        + FuturesSpread(sold.AvgPrice, undoNext.AvgPrice, undoNext.Filled),
                    near.Commission + sold.Commission + undoNext.Commission + restoreNear.Commission);
                return undoNext.Filled < sold.Filled || restoreNear.Filled < units
                    ? Broken($"roll: next leg sold {sold.Filled}/{units}, rollback incomplete " +
                             $"(next bought back {undoNext.Filled}, near resold {restoreNear.Filled})", restored)
                    : Failed($"roll: next leg sold {sold.Filled}/{units}, rolled back", restored);
            }

            var rolled = position.Roll(_pair, next, near.AvgPrice, sold.AvgPrice, near.Commission + sold.Commission);
            _log.LogInformation("Rolled {Units} units {Near} -> {Next}: bought {NearPrice}, sold {NextPrice}",
                units, nearSymbol, next.Symbol, near.AvgPrice, sold.AvgPrice);
            return new ExecutionResult(ExecutionStatus.Completed, rolled, units, 0m, "roll completed");
        });
    }

    private string? Blocked(bool newRisk)
    {
        if (newRisk && _killSwitch.IsTripped)
            return $"kill switch: {_killSwitch.Reason}";
        if (!_schedule.IsOpen(_time.GetUtcNow()))
            return "outside trading window";
        return null;
    }

    /// <summary>Ведущая нога — акция, ведомая — фьючерс в противоположную сторону, затем откат излишка акции.</summary>
    private async Task<PairFill> TradePairAsync(OrderSide stockSide, int units, string futuresSymbol)
    {
        int lotsPerUnit = _pair.StockLotsPerUnit;
        string stockSymbol = _pair.Stock.Symbol;
        var reverse = Opposite(stockSide);

        var stock = await ExecuteLegAsync(stockSymbol, stockSide, units * lotsPerUnit, _pair.Stock.PriceStep);
        int matched = stock.Filled / lotsPerUnit;
        var futures = await ExecuteLegAsync(futuresSymbol, reverse, matched, _pair.FuturesPriceStep);

        int hedged = futures.Filled;
        int excessLots = stock.Filled - hedged * lotsPerUnit;
        var unwind = await ExecuteLegAsync(stockSymbol, reverse, excessLots, _pair.Stock.PriceStep);

        decimal hedgedShare = stock.Filled == 0 ? 0m : (decimal)(hedged * lotsPerUnit) / stock.Filled;
        int sign = stockSide == OrderSide.Buy ? 1 : -1;
        decimal unwindPnl = sign * unwind.Filled * _pair.Stock.LotSize * (unwind.AvgPrice - stock.AvgPrice);

        return new PairFill(
            hedged,
            stock.AvgPrice,
            futures.AvgPrice,
            stock.Commission * hedgedShare + futures.Commission,
            unwindPnl,
            unwind.Commission + stock.Commission * (1m - hedgedShare),
            NakedLots: excessLots - unwind.Filled);
    }

    private ExecutionResult Finish(string operation, int requested, PairFill fill, PairPosition? position)
    {
        if (position is not null && (fill.UnwindPnl != 0m || fill.UnwindCommission != 0m))
            position = position.RecordUnwind(fill.UnwindPnl, fill.UnwindCommission);
        decimal unwindNet = fill.UnwindPnl - fill.UnwindCommission;

        if (fill.NakedLots > 0)
            return Broken($"{operation}: {fill.NakedLots} stock lots left unhedged after failed unwind", position,
                fill.Units, unwindNet);

        var status = fill.Units == requested ? ExecutionStatus.Completed
            : fill.Units > 0 ? ExecutionStatus.Partial
            : ExecutionStatus.Failed;
        string message = $"{operation}: {fill.Units}/{requested} units";
        if (status == ExecutionStatus.Completed)
            _log.LogInformation("{Message}, stock {StockPrice}, futures {FuturesPrice}", message, fill.StockPrice, fill.FuturesPrice);
        else
            _log.LogWarning("{Message}, unwind result {UnwindPnl}", message, unwindNet);
        return new ExecutionResult(status, position, fill.Units, unwindNet, message);
    }

    /// <summary>Любое исключение посреди пары — состояние ног неизвестно: kill switch и Broken.</summary>
    private async Task<ExecutionResult> GuardAsync(
        string operation, PairPosition? position, Func<Task<ExecutionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Operation} failed mid-execution", operation);
            return Broken($"{operation}: execution error, leg state unknown: {ex.Message}", position);
        }
    }

    /// <summary>Исполнить ногу «агрессивными лимитами» с повторами на неисполненный остаток.</summary>
    private async Task<LegFill> ExecuteLegAsync(string symbol, OrderSide side, int quantity, decimal step)
    {
        int filled = 0;
        decimal notional = 0m, commission = 0m;

        for (int attempt = 1; attempt <= _options.MaxAttemptsPerLeg && filled < quantity; attempt++)
        {
            var quote = await _broker.GetQuoteAsync(symbol);
            decimal price = side == OrderSide.Buy
                ? Math.Ceiling(quote.Ask * (1m + _options.MaxSlippage) / step) * step
                : Math.Max(step, Math.Floor(quote.Bid * (1m - _options.MaxSlippage) / step) * step);

            var request = new OrderRequest(Guid.NewGuid().ToString("N"), symbol, side, quantity - filled, price);
            var state = await WaitTerminalAsync(await _broker.PlaceOrderAsync(request));

            filled += state.FilledQuantity;
            notional += state.FilledQuantity * state.AvgFillPrice;
            commission += state.Commission;
            _log.LogInformation("{Side} {Symbol} {Qty}@{Limit} attempt {Attempt}: {Status}, filled {Filled}",
                side, symbol, request.Quantity, price, attempt, state.Status, state.FilledQuantity);

            if (state.Status == OrderStatus.Rejected)
            {
                _log.LogWarning("Order rejected: {Reason}", state.RejectReason);
                break;
            }
        }

        return new LegFill(filled, filled > 0 ? notional / filled : 0m, commission);
    }

    private async Task<OrderState> WaitTerminalAsync(OrderState state)
    {
        long started = _time.GetTimestamp();
        while (!state.IsTerminal && _time.GetElapsedTime(started) < _options.OrderTimeout)
        {
            await Task.Delay(_options.PollInterval, _time);
            state = await _broker.GetOrderAsync(state.ClientOrderId);
        }
        if (state.IsTerminal)
            return state;

        state = await _broker.CancelOrderAsync(state.ClientOrderId);
        started = _time.GetTimestamp();
        while (!state.IsTerminal && _time.GetElapsedTime(started) < _options.OrderTimeout)
        {
            await Task.Delay(_options.PollInterval, _time);
            state = await _broker.GetOrderAsync(state.ClientOrderId);
        }
        return state.IsTerminal
            ? state
            : throw new TimeoutException($"Заявка {state.ClientOrderId} не снялась за {_options.OrderTimeout}.");
    }

    private decimal FuturesSpread(decimal sellPrice, decimal buyPrice, int contracts) =>
        contracts * _pair.PointValue * (sellPrice - buyPrice);

    private ExecutionResult Failed(string reason, PairPosition? position)
    {
        _log.LogWarning("Execution skipped: {Reason}", reason);
        return new ExecutionResult(ExecutionStatus.Failed, position, 0, 0m, reason);
    }

    private ExecutionResult Broken(string reason, PairPosition? position, int units = 0, decimal unwindPnl = 0m)
    {
        _killSwitch.Trip(reason);
        _log.LogCritical("Execution broken, kill switch tripped: {Reason}", reason);
        return new ExecutionResult(ExecutionStatus.Broken, position, units, unwindPnl, reason);
    }

    private static OrderSide Opposite(OrderSide side) => side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

    private sealed record LegFill(int Filled, decimal AvgPrice, decimal Commission);

    private sealed record PairFill(
        int Units,
        decimal StockPrice,
        decimal FuturesPrice,
        decimal Commission,
        decimal UnwindPnl,
        decimal UnwindCommission,
        int NakedLots);
}
