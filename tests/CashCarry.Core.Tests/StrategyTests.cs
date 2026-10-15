using CashCarry.Core.Market;
using CashCarry.Core.Positions;
using CashCarry.Core.Strategy;

namespace CashCarry.Core.Tests;

public class StrategyTests
{
    private static readonly Quote Stock = new(300.00m, 300.10m);

    private static CashAndCarryStrategy Strategy(Action<StrategyOptions>? configure = null)
    {
        var options = new StrategyOptions
        {
            MinEntryAnnualYield = 0.12m,
            MinHoldAnnualYield = 0.08m,
            MinRollAnnualYield = 0.08m,
            MinDaysToExpiryForEntry = 14,
            RollWindowDays = 3,
            UnitsPerEntry = 5,
            MaxUnits = 5,
        };
        configure?.Invoke(options);
        return new CashAndCarryStrategy(Sber.Pair, Sber.Commissions, options);
    }

    // Базис 31000 − 30010 = 990; комиссии туда-обратно 28.008; капитал 30010 + 12.004 + ГО 5000 = 35022.004;
    // (961.992 / 35022.004) × 365 / 71 ≈ 14,12 % годовых.
    private static MarketSnapshot EntrySnapshot(decimal freeCash = 100_000m) =>
        Sber.Snapshot(Sber.Today, Stock, Sber.Market(Sber.Dec26, 31000m, 31010m), freeCash: freeCash);

    [Fact]
    public void Entry_yield_matches_hand_calculation()
    {
        var snapshot = EntrySnapshot();

        decimal yield = Strategy().EntryAnnualYield(snapshot, snapshot.Near);

        Assert.InRange(yield, 0.1411m, 0.1413m);
    }

    [Fact]
    public void Opens_when_yield_above_threshold_limited_by_free_cash()
    {
        var decision = Strategy().Evaluate(EntrySnapshot(freeCash: 100_000m), position: null);

        Assert.Equal(DecisionKind.Open, decision.Kind);
        Assert.Equal(Sber.Dec26, decision.Contract);
        Assert.Equal(2, decision.Units); // 100 000 / 35 022 → 2 единицы
    }

    [Fact]
    public void Holds_when_yield_below_threshold()
    {
        var decision = Strategy(o => { o.MinEntryAnnualYield = 0.16m; o.MinHoldAnnualYield = 0.1m; })
            .Evaluate(EntrySnapshot(), position: null);

        Assert.Equal(DecisionKind.Hold, decision.Kind);
    }

    [Fact]
    public void Holds_when_cash_insufficient_for_one_unit()
    {
        var decision = Strategy().Evaluate(EntrySnapshot(freeCash: 30_000m), position: null);

        Assert.Equal(DecisionKind.Hold, decision.Kind);
    }

    [Fact]
    public void Enters_next_contract_when_near_is_too_close_to_expiry()
    {
        var today = new DateOnly(2026, 12, 10); // до Dec26 8 дней
        var snapshot = Sber.Snapshot(today, Stock,
            near: Sber.Market(Sber.Dec26, 30030m, 30040m),
            next: Sber.Market(Sber.Mar27, 31800m, 31810m));

        var decision = Strategy().Evaluate(snapshot, position: null);

        Assert.Equal(DecisionKind.Open, decision.Kind);
        Assert.Equal(Sber.Mar27, decision.Contract);
    }

    [Fact]
    public void Expected_dividend_makes_discounted_futures_attractive()
    {
        // Фьючерс дешевле акции на ожидаемый дивиденд: базис отрицательный, но дивиденд 30 ₽ (26,1 после налога) его перекрывает.
        var near = Sber.Market(Sber.Dec26, 28500m, 28510m);
        var dividend = new DividendEvent(new DateOnly(2026, 11, 20), 30m);

        var without = Strategy().Evaluate(Sber.Snapshot(Sber.Today, Stock, near), position: null);
        var with = Strategy().Evaluate(Sber.Snapshot(Sber.Today, Stock, near, dividends: [dividend]), position: null);

        Assert.Equal(DecisionKind.Hold, without.Kind);
        Assert.Equal(DecisionKind.Open, with.Kind);
    }

    private static readonly DateOnly Nov20 = new(2026, 11, 20); // до Dec26 28 дней
    private static readonly Quote StockUp = new(310.00m, 310.10m);
    private static PairPosition Held(int units = 1) =>
        PairPosition.Open(Sber.Dec26, units, 300.10m, 31000m, commission: 14.004m);

    [Theory]
    [InlineData(ExitMode.Both)]
    [InlineData(ExitMode.Early)]
    public void Exits_early_when_basis_converged(ExitMode mode)
    {
        // Базис закрытия 31060 − 31000 = 60 → оставшаяся доходность ≈ 2 % < 8 %.
        var snapshot = Sber.Snapshot(Nov20, StockUp, Sber.Market(Sber.Dec26, 31050m, 31060m));

        var decision = Strategy(o => { o.ExitMode = mode; o.MaxUnits = 1; o.UnitsPerEntry = 1; })
            .Evaluate(snapshot, Held());

        Assert.Equal(DecisionKind.CloseAll, decision.Kind);
        Assert.InRange(decision.AnnualYield!.Value, 0.021m, 0.022m);
    }

    [Fact]
    public void Expiry_mode_never_exits_early()
    {
        var snapshot = Sber.Snapshot(Nov20, StockUp, Sber.Market(Sber.Dec26, 31050m, 31060m));

        var decision = Strategy(o => { o.ExitMode = ExitMode.Expiry; o.MaxUnits = 1; o.UnitsPerEntry = 1; })
            .Evaluate(snapshot, Held());

        Assert.Equal(DecisionKind.Hold, decision.Kind);
    }

    [Fact]
    public void Holds_while_remaining_basis_still_pays()
    {
        // Базис закрытия 810 → ≈ 29 % годовых на оставшиеся 28 дней.
        var snapshot = Sber.Snapshot(Nov20, StockUp, Sber.Market(Sber.Dec26, 31800m, 31810m));

        var decision = Strategy(o => { o.MaxUnits = 1; o.UnitsPerEntry = 1; }).Evaluate(snapshot, Held());

        Assert.Equal(DecisionKind.Hold, decision.Kind);
    }

    [Fact]
    public void Does_not_exit_early_below_min_profit()
    {
        var snapshot = Sber.Snapshot(Nov20, StockUp, Sber.Market(Sber.Dec26, 31050m, 31060m));

        // PnL сейчас ≈ 901.6 ₽ на единицу.
        var decision = Strategy(o => { o.EarlyExitMinProfitPerUnit = 1000m; o.MaxUnits = 1; o.UnitsPerEntry = 1; })
            .Evaluate(snapshot, Held());

        Assert.Equal(DecisionKind.Hold, decision.Kind);
    }

    [Fact]
    public void Adds_units_to_held_contract_up_to_max()
    {
        var decision = Strategy(o => { o.MaxUnits = 3; o.UnitsPerEntry = 3; o.ExitMode = ExitMode.Expiry; })
            .Evaluate(EntrySnapshot(freeCash: 1_000_000m), Held(units: 1));

        Assert.Equal(DecisionKind.Open, decision.Kind);
        Assert.Equal(2, decision.Units);
    }

    // До Dec26 2 дня. Спред 31300 − 30530 = 770, комиссии 6, капитал 30500 + 5000,
    // до Mar27 93 дня → 764 / 35500 × 365 / 93 ≈ 8,45 %.
    private static readonly DateOnly Dec16 = new(2026, 12, 16);

    private static MarketSnapshot RollSnapshot(bool withNext = true) =>
        Sber.Snapshot(Dec16, new Quote(305.00m, 305.10m),
            near: Sber.Market(Sber.Dec26, 30520m, 30530m),
            next: withNext ? Sber.Market(Sber.Mar27, 31300m, 31310m) : null);

    [Fact]
    public void Rolls_when_calendar_spread_pays()
    {
        var decision = Strategy().Evaluate(RollSnapshot(), Held());

        Assert.Equal(DecisionKind.Roll, decision.Kind);
        Assert.Equal(Sber.Mar27, decision.Contract);
        Assert.InRange(decision.AnnualYield!.Value, 0.0844m, 0.0846m);
    }

    [Fact]
    public void Closes_at_expiry_when_roll_does_not_pay()
    {
        var decision = Strategy(o => o.MinRollAnnualYield = 0.10m).Evaluate(RollSnapshot(), Held());

        Assert.Equal(DecisionKind.CloseAll, decision.Kind);
    }

    [Fact]
    public void Closes_at_expiry_without_next_contract()
    {
        var decision = Strategy().Evaluate(RollSnapshot(withNext: false), Held());

        Assert.Equal(DecisionKind.CloseAll, decision.Kind);
    }

    [Fact]
    public void Early_mode_never_rolls()
    {
        var decision = Strategy(o => o.ExitMode = ExitMode.Early).Evaluate(RollSnapshot(), Held());

        Assert.Equal(DecisionKind.CloseAll, decision.Kind);
    }

    [Fact]
    public void Rejects_snapshot_for_other_contract_than_held()
    {
        var snapshot = Sber.Snapshot(Sber.Today, Stock, Sber.Market(Sber.Mar27, 31800m, 31810m));

        Assert.Throws<ArgumentException>(() => Strategy().Evaluate(snapshot, Held()));
    }

    [Fact]
    public void Rejects_hold_threshold_not_below_entry_threshold()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Strategy(o => { o.MinEntryAnnualYield = 0.1m; o.MinHoldAnnualYield = 0.1m; }));
    }
}
