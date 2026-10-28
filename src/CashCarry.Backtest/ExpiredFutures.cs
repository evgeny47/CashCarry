using CashCarry.Engine;

namespace CashCarry.Backtest;

/// <summary>
/// В настройках робота только будущие серии фьючерсов. Для прогона по прошлому дописываем истёкшие квартальные серии
/// по образцу настроенных: SRZ6@RTSX → SRU6, SRM6, SRH6, SRZ5… Последний день торгов — третий четверг месяца
/// экспирации (правило MOEX для фьючерсов на акции), ГО — как у ближайшей настроенной серии: исторического ГО Финам не отдаёт.
/// </summary>
public static class ExpiredFutures
{
    private static readonly (int Month, char Code)[] Quarters = [(3, 'H'), (6, 'M'), (9, 'U'), (12, 'Z')];

    /// <summary>Добавляет в пары серии, истекающие с <paramref name="from"/> до первой настроенной. Возвращает добавленные символы.</summary>
    public static List<string> AddTo(IEnumerable<PairSettings> pairs, DateOnly from)
    {
        var added = new List<string>();
        foreach (var pair in pairs)
        {
            var first = pair.Futures.OrderBy(f => f.LastTradingDay).First();
            string ticker = first.Symbol.Split('@')[0];
            string board = first.Symbol[ticker.Length..];
            if (ticker.Length < 3 || !char.IsDigit(ticker[^1]))
                continue;
            string prefix = ticker[..^2];

            for (int year = from.Year; year <= first.LastTradingDay.Year; year++)
            {
                foreach (var (month, code) in Quarters)
                {
                    var lastDay = ThirdThursday(year, month);
                    string symbol = $"{prefix}{code}{year % 10}{board}";
                    if (lastDay < from || lastDay >= first.LastTradingDay || pair.Find(symbol) is not null)
                        continue;
                    pair.Futures.Add(new FuturesSettings { Symbol = symbol, LastTradingDay = lastDay, InitialMargin = first.InitialMargin });
                    added.Add(symbol);
                }
            }
        }
        return added;
    }

    public static DateOnly ThirdThursday(int year, int month)
    {
        var day = new DateOnly(year, month, 1);
        int toThursday = ((int)DayOfWeek.Thursday - (int)day.DayOfWeek + 7) % 7;
        return day.AddDays(toThursday + 14);
    }
}
