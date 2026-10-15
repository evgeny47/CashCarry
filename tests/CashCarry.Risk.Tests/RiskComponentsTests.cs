using CashCarry.Core.Broker;
using CashCarry.Core.Instruments;
using CashCarry.Core.Market;
using CashCarry.Core.Positions;

namespace CashCarry.Risk.Tests;

public class RiskComponentsTests
{
    private static readonly PairSpec Pair = new(new StockSpec("SBER", 10), sharesPerContract: 100);
    private static readonly CommissionSchedule Commissions = new(0.0004m, 2m);
    private static readonly FuturesContract Dec26 = new("SBRF-12.26", new DateOnly(2026, 12, 18));

    private static AccountState Account(decimal freeCash, int sberLots = 0, int sbrf = 0) =>
        new(freeCash, freeCash, 0m, 0m, new Dictionary<string, int> { ["SBER"] = sberLots, [Dec26.Symbol] = sbrf });

    [Fact]
    public void Kill_switch_keeps_first_reason_and_fires_once()
    {
        var ks = new KillSwitch();
        var fired = new List<string>();
        ks.Tripped += fired.Add;

        ks.Trip("first");
        ks.Trip("second");

        Assert.True(ks.IsTripped);
        Assert.Equal("first", ks.Reason);
        Assert.Equal(["first"], fired);

        ks.Reset();
        Assert.False(ks.IsTripped);
    }

    [Theory]
    [InlineData("2026-10-12T11:00:00+03:00", true)]   // понедельник, основная сессия
    [InlineData("2026-10-12T08:00:00+00:00", true)]   // то же в UTC
    [InlineData("2026-10-12T10:01:00+03:00", false)]  // открытие
    [InlineData("2026-10-12T14:02:00+03:00", false)]  // дневной клиринг
    [InlineData("2026-10-12T18:38:00+03:00", false)]  // перед аукционом закрытия
    [InlineData("2026-10-12T20:00:00+03:00", false)]  // вечерняя сессия
    [InlineData("2026-10-10T11:00:00+03:00", false)]  // суббота
    [InlineData("2026-11-04T11:00:00+03:00", false)]  // праздник
    public void Moex_schedule(string time, bool open)
    {
        var schedule = TradingSchedule.MoexMainSession(holidays: [new DateOnly(2026, 11, 4)]);

        Assert.Equal(open, schedule.IsOpen(DateTimeOffset.Parse(time)));
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(1, 0, 1)]    // резерв под ВМ уже открытого контракта съедает часть свободных денег
    [InlineData(0, 3101, 1)] // так же — резерв другой пары: деньги счёта общие
    public void Margin_guard_sizes_units_with_variation_margin_reserve(int heldUnits, int otherReserve, int expected)
    {
        var guard = new MarginGuard(Pair, Commissions, new RiskLimits { VariationMarginReserve = 0.10m });

        // На единицу: 30010 + 12.004 + ГО 5000 + 2 + резерв 3101 = 38125.004
        int units = guard.MaxUnitsToOpen(
            Account(78_000m), new Quote(300m, 300.10m), new Quote(31000m, 31010m), 5000m, heldUnits, otherReserve);

        Assert.Equal(expected, units);
    }

    [Theory]
    [InlineData(-1, MarginStatus.Critical)]
    [InlineData(5000, MarginStatus.Warning)] // резерв на 2 контракта: 2 × 31000 × 10 % = 6200
    [InlineData(7000, MarginStatus.Ok)]
    public void Margin_guard_assesses_free_cash_against_reserve(decimal freeCash, MarginStatus expected)
    {
        var guard = new MarginGuard(Pair, Commissions, new RiskLimits { VariationMarginReserve = 0.10m });

        Assert.Equal(expected, MarginGuard.Assess(Account(freeCash), guard.Reserve(contracts: -2, futuresPrice: 31000m)));
    }

    [Fact]
    public void Hedge_monitor_trips_only_after_consecutive_mismatches()
    {
        var ks = new KillSwitch();
        var monitor = new HedgeMonitor(Pair, ks, new RiskLimits { MaxHedgeMismatchChecks = 3 });
        var position = PairPosition.Open(Dec26, 1, 300m, 31000m, 0m);
        var hedged = Account(0m, sberLots: 10, sbrf: -1);
        var naked = Account(0m, sberLots: 10, sbrf: 0);

        Assert.True(monitor.Observe(position, hedged, Dec26.Symbol).IsConsistent);

        var report = monitor.Observe(position, naked, Dec26.Symbol);
        monitor.Observe(position, naked, Dec26.Symbol);
        Assert.Equal(100, report.NetShares);
        Assert.False(ks.IsTripped);

        monitor.Observe(position, hedged, Dec26.Symbol); // сброс счётчика
        monitor.Observe(position, naked, Dec26.Symbol);
        monitor.Observe(position, naked, Dec26.Symbol);
        Assert.False(ks.IsTripped);

        monitor.Observe(position, naked, Dec26.Symbol);
        Assert.True(ks.IsTripped);
    }
}
