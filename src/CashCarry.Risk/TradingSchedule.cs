namespace CashCarry.Risk;

/// <summary>Окно торговли по московскому времени, [Start; End).</summary>
public sealed record TradingWindow(TimeOnly Start, TimeOnly End);

/// <summary>
/// Когда паре можно торговать: обе ноги должны быть в основной сессии, вне клирингов и аукционов.
/// Москва — UTC+3 без перехода на летнее время.
/// </summary>
public sealed class TradingSchedule
{
    private static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    private readonly IReadOnlyList<TradingWindow> _windows;
    private readonly HashSet<DateOnly> _holidays;
    private readonly bool _weekends;

    public TradingSchedule(
        IEnumerable<TradingWindow> windows, IEnumerable<DateOnly>? holidays = null, bool weekends = false)
    {
        _windows = windows.ToList();
        _holidays = holidays?.ToHashSet() ?? [];
        _weekends = weekends;
    }

    /// <summary>Круглосуточно, включая выходные — только для симуляции.</summary>
    public static TradingSchedule AlwaysOpen() =>
        new([new TradingWindow(TimeOnly.MinValue, TimeOnly.MaxValue)], weekends: true);

    /// <summary>
    /// Основная сессия MOEX с запасом: 10:05–13:55 и 14:10–18:35.
    /// Исключены открытие, дневной клиринг FORTS (14:00–14:05) и аукцион закрытия акций (с 18:40).
    /// Вечерняя сессия и выходные исключены: ликвидность ниже, ноги расходятся сильнее.
    /// </summary>
    public static TradingSchedule MoexMainSession(IEnumerable<DateOnly>? holidays = null) => new(
        [
            new TradingWindow(new TimeOnly(10, 5), new TimeOnly(13, 55)),
            new TradingWindow(new TimeOnly(14, 10), new TimeOnly(18, 35)),
        ],
        holidays);

    public static DateTimeOffset ToMoscow(DateTimeOffset time) => time.ToOffset(MoscowOffset);

    public bool IsOpen(DateTimeOffset now)
    {
        var msk = ToMoscow(now);
        var date = DateOnly.FromDateTime(msk.DateTime);
        if ((!_weekends && msk.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) || _holidays.Contains(date))
            return false;

        var time = TimeOnly.FromDateTime(msk.DateTime);
        return _windows.Any(w => time >= w.Start && time < w.End);
    }
}
