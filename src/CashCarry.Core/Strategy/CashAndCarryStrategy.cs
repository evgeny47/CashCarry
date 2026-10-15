using CashCarry.Core.Instruments;
using CashCarry.Core.Market;
using CashCarry.Core.Positions;

namespace CashCarry.Core.Strategy;

/// <summary>
/// Чистая (без побочных эффектов) логика cash-and-carry: по срезу рынка и позиции возвращает решение.
/// Исполнение ордеров, контроль ног и ГО — вне Core.
/// </summary>
public sealed class CashAndCarryStrategy
{
    private const decimal DaysInYear = 365m;

    private readonly PairSpec _pair;
    private readonly CommissionSchedule _commissions;
    private readonly StrategyOptions _options;

    public CashAndCarryStrategy(PairSpec pair, CommissionSchedule commissions, StrategyOptions options)
    {
        options.Validate();
        _pair = pair;
        _commissions = commissions;
        _options = options;
    }

    public StrategyDecision Evaluate(MarketSnapshot market, PairPosition? position)
    {
        if (position is null || position.Units == 0)
            return EvaluateEntry(market, heldUnits: 0, onlyNear: false);

        if (position.Futures != market.Near.Contract)
            throw new ArgumentException(
                $"Near ({market.Near.Contract.Symbol}) должен совпадать с контрактом позиции ({position.Futures.Symbol}).",
                nameof(market));

        int daysLeft = DaysBetween(market.Today, position.Futures.LastTradingDay);
        if (daysLeft <= _options.RollWindowDays)
            return EvaluateRollOrClose(market, position);

        if (_options.ExitMode != ExitMode.Expiry && EvaluateEarlyExit(market, position, daysLeft) is { } exit)
            return exit;

        if (position.Units < _options.MaxUnits)
        {
            var add = EvaluateEntry(market, position.Units, onlyNear: true);
            if (add.Kind == DecisionKind.Open)
                return add;
        }

        return StrategyDecision.Hold("holding position");
    }

    /// <summary>
    /// Ожидаемый результат удержания до экспирации на одну единицу:
    /// базис входа + дивиденды до экспирации − комиссии входа и выхода.
    /// Капитал = акции + комиссия + ГО.
    /// </summary>
    public decimal EntryAnnualYield(MarketSnapshot market, FuturesMarket futures)
    {
        int days = DaysBetween(market.Today, futures.Contract.LastTradingDay);
        if (days <= 0)
            throw new ArgumentException("Контракт уже экспирировался.", nameof(futures));

        return Annualize(EntryProfitPerUnit(market, futures), EntryCapitalPerUnit(market, futures), days);
    }

    private StrategyDecision EvaluateEntry(MarketSnapshot market, int heldUnits, bool onlyNear)
    {
        var futures = PickEntryContract(market, onlyNear);
        if (futures is null)
            return StrategyDecision.Hold("no contract far enough from expiry");

        decimal yield = EntryAnnualYield(market, futures);
        if (yield < _options.MinEntryAnnualYield)
            return StrategyDecision.Hold("entry yield below threshold", yield);

        decimal capitalPerUnit = EntryCapitalPerUnit(market, futures);
        int affordable = (int)Math.Floor(market.FreeCash / capitalPerUnit);
        int units = Math.Min(Math.Min(_options.UnitsPerEntry, _options.MaxUnits - heldUnits), affordable);
        if (units <= 0)
            return StrategyDecision.Hold("not enough free cash", yield);

        return StrategyDecision.Open(futures.Contract, units, yield);
    }

    private FuturesMarket? PickEntryContract(MarketSnapshot market, bool onlyNear)
    {
        if (IsFarEnough(market.Today, market.Near))
            return market.Near;
        if (!onlyNear && market.Next is { } next && IsFarEnough(market.Today, next))
            return next;
        return null;
    }

    private bool IsFarEnough(DateOnly today, FuturesMarket futures) =>
        DaysBetween(today, futures.Contract.LastTradingDay) >= _options.MinDaysToExpiryForEntry;

    private decimal EntryProfitPerUnit(MarketSnapshot market, FuturesMarket futures)
    {
        int k = _pair.SharesPerContract;
        decimal basis = Basis.ToOpen(_pair, market.Stock, futures.Quote);
        decimal dividends = k * Dividends.NetPerShare(
            market.Dividends, market.Today, futures.Contract.LastTradingDay, _options.DividendTaxRate);
        // Выход оцениваем по цене входа акции: к экспирации цена неизвестна, а ошибка — доли процента от комиссии.
        decimal costs = 2 * _commissions.ForStock(market.Stock.Ask, k) + 2 * _commissions.ForFutures(1);
        return basis + dividends - costs;
    }

    private decimal EntryCapitalPerUnit(MarketSnapshot market, FuturesMarket futures)
    {
        int k = _pair.SharesPerContract;
        return market.Stock.Ask * k + _commissions.ForStock(market.Stock.Ask, k) + futures.InitialMargin;
    }

    /// <summary>
    /// Досрочный выход. Держать дальше имеет смысл ради оставшегося базиса (он сойдётся к нулю
    /// к экспирации) и дивидендов впереди. Если это даёт меньше MinHoldAnnualYield на капитал,
    /// а текущий чистый PnL не ниже порога — закрываемся и освобождаем капитал.
    /// </summary>
    private StrategyDecision? EvaluateEarlyExit(MarketSnapshot market, PairPosition position, int daysLeft)
    {
        decimal pnlNow = position.NetPnlIfClosed(_pair, market.Stock, market.Near.Quote, _commissions);
        if (pnlNow < _options.EarlyExitMinProfitPerUnit * position.Units)
            return null;

        int k = _pair.SharesPerContract;
        decimal remainingPerUnit = Basis.ToClose(_pair, market.Stock, market.Near.Quote)
            + k * Dividends.NetPerShare(
                market.Dividends, market.Today, position.Futures.LastTradingDay, _options.DividendTaxRate);
        decimal capitalPerUnit = market.Stock.Bid * k + market.Near.InitialMargin;
        decimal remainingYield = Annualize(remainingPerUnit, capitalPerUnit, daysLeft);

        return remainingYield < _options.MinHoldAnnualYield
            ? StrategyDecision.CloseAll("basis converged: remaining carry below hold threshold", remainingYield)
            : null;
    }

    /// <summary>
    /// У экспирации: перенос, если календарный спред (следующий bid − ближний ask) плюс дивиденды
    /// между экспирациями минус три фьючерсные комиссии (закрыть ближний, открыть и потом закрыть
    /// следующий) дают не ниже MinRollAnnualYield. Комиссия продажи акций в сравнении не участвует —
    /// она платится в обоих вариантах.
    /// </summary>
    private StrategyDecision EvaluateRollOrClose(MarketSnapshot market, PairPosition position)
    {
        if (_options.ExitMode == ExitMode.Early)
            return StrategyDecision.CloseAll("expiry approaching, roll disabled in Early mode");
        if (market.Next is not { } next)
            return StrategyDecision.CloseAll("expiry approaching, no next contract");

        int days = DaysBetween(market.Today, next.Contract.LastTradingDay);
        if (days <= _options.RollWindowDays)
            return StrategyDecision.CloseAll("expiry approaching, next contract too close");

        int k = _pair.SharesPerContract;
        decimal spread = (next.Quote.Bid - market.Near.Quote.Ask) * _pair.PointValue;
        decimal dividends = k * Dividends.NetPerShare(
            market.Dividends, position.Futures.LastTradingDay.AddDays(1), next.Contract.LastTradingDay,
            _options.DividendTaxRate);
        decimal costs = 3 * _commissions.ForFutures(1);
        decimal capitalPerUnit = market.Stock.Bid * k + next.InitialMargin;
        decimal yield = Annualize(spread + dividends - costs, capitalPerUnit, days);

        return yield >= _options.MinRollAnnualYield
            ? StrategyDecision.Roll(next.Contract, yield)
            : StrategyDecision.CloseAll("expiry approaching, roll yield below threshold", yield);
    }

    private static int DaysBetween(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber;

    private static decimal Annualize(decimal profit, decimal capital, int days) =>
        profit / capital * DaysInYear / days;
}
