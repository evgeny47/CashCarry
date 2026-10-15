using CashCarry.Core.Broker;
using CashCarry.Core.Instruments;
using CashCarry.Core.Market;
using CashCarry.Core.Positions;
using CashCarry.Core.Strategy;
using CashCarry.Risk;
using CashCarry.Risk.Execution;

namespace CashCarry.Host;

/// <summary>
/// Пара в работе: настройки, стратегия, исполнитель, сверка хеджа и текущая позиция.
/// Деньги счёта и kill switch общие для всех пар, остальное — своё у каждой.
/// </summary>
public sealed class TradingPair
{
    public TradingPair(
        PairSettings settings,
        CommissionSchedule commissions,
        IBrokerGateway broker,
        IKillSwitch killSwitch,
        TradingSchedule schedule,
        RiskLimits limits,
        ExecutionOptions execution,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        Settings = settings;
        Spec = settings.ToPairSpec();
        Commissions = commissions;
        Dividends = settings.DividendEvents();
        Strategy = new CashAndCarryStrategy(Spec, commissions, settings.Strategy);
        Margin = new MarginGuard(Spec, commissions, limits);
        Hedge = new HedgeMonitor(Spec, killSwitch, limits);
        Executor = new PairExecutor(broker, Spec, killSwitch, schedule, Margin, execution, time,
            loggers.CreateLogger<PairExecutor>());
    }

    public PairSettings Settings { get; }
    public string Name => Settings.Name;
    public PairSpec Spec { get; }
    public CommissionSchedule Commissions { get; }
    public IReadOnlyList<DividendEvent> Dividends { get; }
    public CashAndCarryStrategy Strategy { get; }
    public MarginGuard Margin { get; }
    public HedgeMonitor Hedge { get; }
    public PairExecutor Executor { get; }

    public PairPosition? Position { get; set; }

    /// <summary>Резерв под ВМ по открытой позиции на последней известной цене фьючерса.</summary>
    public decimal Reserve { get; set; }

    /// <summary>Отключённая пара без позиции не торгуется и не запрашивает котировки.</summary>
    public bool IsActive => Settings.Enabled || Position is { Units: > 0 };

    /// <summary>Ошибки получения котировок и расчёта подряд — только по этой паре.</summary>
    public int ConsecutiveErrors { get; set; }

    public string? LastDryRunKey { get; set; }
    public DateTimeOffset LastDryRunLog { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastCapitalLimitLog { get; set; } = DateTimeOffset.MinValue;

    /// <summary>Деньги в позиции для лимита Robot:MaxCapital: акции по цене входа + ГО + резерв под ВМ.</summary>
    public decimal CapitalInUse()
    {
        if (Position is not { Units: > 0 } p)
            return 0m;
        decimal margin = Settings.Find(p.Futures.Symbol)?.InitialMargin ?? 0m;
        return p.Units * (p.StockEntryPrice * Spec.SharesPerContract + margin) + Reserve;
    }

    /// <summary>Сколько денег займёт ещё одна единица в контракте futures по текущим ценам.</summary>
    public decimal CapitalPerUnit(Quote stock, FuturesMarket futures)
    {
        int k = Spec.SharesPerContract;
        return stock.Ask * k + Commissions.ForStock(stock.Ask, k) + futures.InitialMargin
            + Commissions.ForFutures(1) + Margin.Reserve(1, futures.Quote.Ask);
    }

    /// <summary>Near — контракт позиции (если есть), иначе ближайший активный; Next — следующий за ним.</summary>
    public (FuturesSettings Near, FuturesSettings? Next)? Contracts(DateOnly today)
    {
        var active = Settings.Active(today);
        FuturesSettings? near = Position is { } p
            ? Settings.Find(p.Futures.Symbol)
              ?? throw new InvalidOperationException($"Контракт позиции {p.Futures.Symbol} отсутствует в Futures пары {Name}.")
            : active.FirstOrDefault();
        if (near is null)
            return null;

        var next = active.FirstOrDefault(f => f.LastTradingDay > near.LastTradingDay);
        return (near, next);
    }

    /// <summary>Котировки, нужные стратегии: акция, ближний и следующий контракт. Дальние серии не запрашиваем — лимит API.</summary>
    public IEnumerable<string> QuoteSymbols(DateOnly today)
    {
        yield return Settings.StockSymbol;
        if (Contracts(today) is not { } contracts)
            yield break;
        yield return contracts.Near.Symbol;
        if (contracts.Next is { } next)
            yield return next.Symbol;
    }
}
