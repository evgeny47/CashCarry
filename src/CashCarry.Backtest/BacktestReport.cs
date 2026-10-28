using System.Globalization;
using System.Text;
using CashCarry.Risk;
using Microsoft.Data.Sqlite;

namespace CashCarry.Backtest;

/// <summary>Сводка прогона в консоль и CSV (сделки, капитал по дням) в папку прогона.</summary>
public static class BacktestReport
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static void Print(BacktestResult r, BacktestSettings settings, IReadOnlyDictionary<string, int> problems, TextWriter o)
    {
        double days = Math.Max(1, (r.LastTick - r.FirstTick).TotalDays);
        o.WriteLine();
        o.WriteLine($"Период:         {Msk(r.FirstTick)} — {Msk(r.LastTick)} МСК ({days:0} дн.), свечи {settings.Timeframe}, спред {settings.SpreadTicks} шаг.");
        o.WriteLine($"Тиков:          {r.Ticks:N0}, из них в торговые окна {r.TradingTicks:N0}" +
                    (r.WarmupTicks > 0 ? $", ожидание котировок {r.WarmupTicks:N0} ({string.Join(", ", r.WarmupSymbols)})" : ""));
        o.WriteLine();
        o.WriteLine($"Деньги:         {Rub(r.InitialCash)} → {Rub(r.FinalEquity)}");
        o.WriteLine($"Результат:      {Signed(r.Pnl)} ({Percent(r.Pnl / r.InitialCash)} от стартовых денег)");
        if (r.MaxCapitalInUse > 0)
        {
            decimal onCapital = r.Pnl / r.MaxCapitalInUse;
            o.WriteLine($"На капитал:     {Percent(onCapital)} на макс. задействованный капитал {Rub(r.MaxCapitalInUse)}, " +
                        $"≈ {Percent(onCapital * 365m / (decimal)days)} годовых");
        }
        o.WriteLine($"Просадка макс.: {Rub(r.MaxDrawdown)} ({Percent(r.MaxDrawdownPercent)} от пика)");
        o.WriteLine($"В позиции:      {Percent(r.ExposureShare)} торгового времени");
        o.WriteLine($"Сделок:         {r.Fills.Count}, комиссии {Rub(r.Commissions)}");

        o.WriteLine();
        o.WriteLine("По парам:");
        var operations = Operations(r.JournalPath);
        foreach (var p in r.Pairs)
        {
            o.Write($"  {p.Name,-6} результат {Signed(p.Pnl),14}, сделок {p.Fills,4}");
            if (p.Dividends != 0)
                o.Write($", в т.ч. дивиденды {Rub(p.Dividends)}");
            if (operations.TryGetValue(p.Name, out string? ops))
                o.Write($" | {ops}");
            o.WriteLine();
            if (p.OpenPosition is { } open)
                o.WriteLine($"         в конце открыто: {open.Units} ед. {open.Futures}, если закрыть — {Signed(open.UnrealizedPnl)}");
        }

        if (r.KillSwitchReason is not null)
        {
            o.WriteLine();
            o.WriteLine($"!!! Сработал kill switch: {r.KillSwitchReason}. После этого новых входов и переносов не было.");
        }
        if (problems.Count > 0)
        {
            o.WriteLine();
            o.WriteLine("Предупреждения робота (сколько раз):");
            foreach (var (message, count) in problems.OrderByDescending(p => p.Value).Take(10))
                o.WriteLine($"  {count,6} × {message}");
        }
    }

    public static (string Trades, string Equity) WriteCsv(BacktestResult r, string directory)
    {
        string trades = Path.Combine(directory, "trades.csv");
        var sb = new StringBuilder("time_msk;symbol;side;quantity;price;commission\n");
        foreach (var f in r.Fills)
            sb.AppendLine(string.Join(';', Msk(f.Time), f.Symbol, f.Side, f.Quantity, Num(f.Price), Num(f.Commission)));
        File.WriteAllText(trades, sb.ToString(), new UTF8Encoding(true));

        string equity = Path.Combine(directory, "equity.csv");
        sb = new StringBuilder("date;equity;day_pnl;capital_in_use\n");
        decimal previous = r.InitialCash;
        foreach (var d in r.Daily)
        {
            sb.AppendLine(string.Join(';', d.Date.ToString("dd.MM.yyyy"), Num(d.Equity), Num(d.Equity - previous), Num(d.CapitalInUse)));
            previous = d.Equity;
        }
        File.WriteAllText(equity, sb.ToString(), new UTF8Encoding(true));
        return (trades, equity);
    }

    /// <summary>Операции робота по паре из журнала: «Open 3, CloseAll 1 (ошибок 1)».</summary>
    private static Dictionary<string, string> Operations(string journalPath)
    {
        var result = new Dictionary<string, string>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = journalPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pair, operation, COUNT(*), SUM(CASE WHEN status = 'Completed' THEN 0 ELSE 1 END)
            FROM journal GROUP BY pair, operation ORDER BY pair, operation
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<(string Pair, string Text)>();
        while (reader.Read())
        {
            long failed = reader.GetInt64(3);
            rows.Add((reader.GetString(0), $"{reader.GetString(1)} {reader.GetInt64(2)}" + (failed > 0 ? $" (не полностью {failed})" : "")));
        }
        foreach (var group in rows.GroupBy(r => r.Pair))
            result[group.Key] = string.Join(", ", group.Select(g => g.Text));
        return result;
    }

    private static string Msk(DateTimeOffset time) => TradingSchedule.ToMoscow(time).ToString("dd.MM.yyyy HH:mm");
    private static string Rub(decimal value) => value.ToString("N0", Ru) + " ₽";
    private static string Signed(decimal value) => (value >= 0 ? "+" : "−") + Math.Abs(value).ToString("N2", Ru) + " ₽";
    private static string Percent(decimal value) => (value * 100).ToString("0.00", Ru) + " %";
    private static string Num(decimal value) => value.ToString("0.####", Ru);
}
