using CashCarry.Core.Market;
using CashCarry.Engine;
using CashCarry.Risk;

namespace CashCarry.Backtest;

/// <summary>Секция "Backtest" (backtest.json и ключи командной строки).</summary>
public sealed class BacktestSettings
{
    public Timeframe Timeframe { get; set; } = Timeframe.M1;
    /// <summary>Первый день периода (по Москве), включительно. По умолчанию — сколько хранит Финам: 7 дней для M1, 30 для остальных.</summary>
    public DateOnly? From { get; set; }
    /// <summary>Последний день периода (по Москве), включительно. По умолчанию — сегодня.</summary>
    public DateOnly? To { get; set; }
    /// <summary>Пары через запятую (SBER,GAZP) или all. Пусто — включённые в настройках робота.</summary>
    public string Pairs { get; set; } = "";
    public decimal InitialCash { get; set; } = 500_000m;
    /// <summary>
    /// Спред, который тестер достраивает к цене закрытия свечи, в шагах цены: продать можно по close − ⌈n/2⌉ шага,
    /// купить — на n шагов дороже. 1 — как у ликвидных акций и фьючерсов в спокойный день.
    /// </summary>
    public int SpreadTicks { get; set; } = 1;
    /// <summary>
    /// Прошлые дивиденды по имени пары: в настройках робота только будущие, а без отсечки в периоде прогона
    /// акция «падает» на сумму дивиденда без компенсации и результат искажается.
    /// </summary>
    public Dictionary<string, List<DividendSettings>> Dividends { get; set; } = new();
    public string HistoryDatabase { get; set; } = "data/history.db";
    public string OutputDirectory { get; set; } = "data/backtest";

    public DateOnly FromDate => From ?? ToDate.AddDays(-(Timeframe == Timeframe.M1 ? 7 : 30));
    public DateOnly ToDate => To ?? DateOnly.FromDateTime(TradingSchedule.ToMoscow(DateTimeOffset.UtcNow).DateTime);

    /// <summary>Период в UTC: с полуночи первого дня по Москве до полуночи после последнего.</summary>
    public (DateTimeOffset From, DateTimeOffset To) Period() => (MoscowMidnight(FromDate), MoscowMidnight(ToDate.AddDays(1)));

    public void Validate()
    {
        if (FromDate > ToDate)
            throw new InvalidOperationException($"начало периода {FromDate} позже конца {ToDate}.");
        if (InitialCash <= 0)
            throw new InvalidOperationException("InitialCash должен быть > 0.");
        if (SpreadTicks < 1)
            throw new InvalidOperationException("SpreadTicks должен быть ≥ 1.");
    }

    /// <summary>Выбранные пары; выбранные явно включаются, даже если в настройках робота выключены.</summary>
    public List<PairSettings> Select(IReadOnlyList<PairSettings> all)
    {
        if (string.IsNullOrWhiteSpace(Pairs))
            return all.Where(p => p.Enabled).ToList();
        if (Pairs.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return all.Select(Enable).ToList();

        var names = Pairs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = names.Where(n => all.All(p => !p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"нет пар {string.Join(", ", unknown)} в Pairs. Есть: {string.Join(", ", all.Select(p => p.Name))}.");
        return all.Where(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).Select(Enable).ToList();
    }

    /// <summary>Добавляет к парам прошлые дивиденды из <see cref="Dividends"/> (кроме уже указанных в настройках робота).</summary>
    public void AddHistoricalDividends(IEnumerable<PairSettings> pairs)
    {
        foreach (var pair in pairs)
        {
            var known = pair.Dividends.Select(d => d.LastBuyDate).ToHashSet();
            var history = Dividends.FirstOrDefault(d => d.Key.Equals(pair.Name, StringComparison.OrdinalIgnoreCase)).Value ?? [];
            pair.Dividends.AddRange(history.Where(d => known.Add(d.LastBuyDate)));
        }
    }

    private static PairSettings Enable(PairSettings pair)
    {
        pair.Enabled = true;
        return pair;
    }

    private static DateTimeOffset MoscowMidnight(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).ToUniversalTime();
}
