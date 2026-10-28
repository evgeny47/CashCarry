using CashCarry.Core.Instruments;
using CashCarry.Core.Market;
using CashCarry.Core.Strategy;

namespace CashCarry.Engine;

public enum BrokerMode { Paper, Finam }

/// <summary>Секция "Robot".</summary>
public sealed class RobotSettings
{
    public BrokerMode Mode { get; set; } = BrokerMode.Paper;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Как часто писать в лог строку состояния (базис, доходность, позиция).</summary>
    public TimeSpan StatusLogInterval { get; set; } = TimeSpan.FromMinutes(5);
    public string DatabasePath { get; set; } = "data/cashcarry.db";
    /// <summary>Торговать вне расписания MOEX. Только для симуляции.</summary>
    public bool IgnoreTradingSchedule { get; set; }
    public List<DateOnly> Holidays { get; set; } = [];
    /// <summary>Сухой прогон: решения пишутся в лог, заявки не выставляются. В режиме Finam включается сам, пока Finam:TradingEnabled = false.</summary>
    public bool DryRun { get; set; }
    /// <summary>
    /// Лимит денег робота, ₽, по всем парам: акции по цене входа + ГО + резерв под ВМ.
    /// Ограничивает только новые входы; уже открытые позиции не сокращает. null — без лимита.
    /// </summary>
    public decimal? MaxCapital { get; set; }

    public void Validate()
    {
        if (MaxCapital is <= 0)
            throw new InvalidOperationException("Robot:MaxCapital должен быть > 0 (или не задан).");
    }
}

public enum PriceSource { Simulated, Finam }

/// <summary>
/// Элемент секции "Pairs": акция и фьючерсы на неё. Подсекция "Strategy" пары переопределяет
/// только указанные в ней поля общей секции "Strategy".
/// </summary>
public sealed class PairSettings
{
    /// <summary>Имя пары в логах и базе. По умолчанию — тикер акции без площадки (SBER@MISX → SBER).</summary>
    public string Name { get; set; } = "";
    /// <summary>false — новых входов нет; открытую позицию робот продолжает вести до закрытия.</summary>
    public bool Enabled { get; set; } = true;
    public string StockSymbol { get; set; } = "";
    public int StockLotSize { get; set; } = 1;
    public decimal StockPriceStep { get; set; } = 0.01m;
    public int SharesPerContract { get; set; } = 100;
    public decimal PointValue { get; set; } = 1m;
    public decimal FuturesPriceStep { get; set; } = 1m;
    public List<FuturesSettings> Futures { get; set; } = [];
    /// <summary>Объявленные дивиденды по акции пары.</summary>
    public List<DividendSettings> Dividends { get; set; } = [];
    /// <summary>Общая секция "Strategy" с переопределениями пары. Заполняется в <see cref="PairsConfig.Load"/>.</summary>
    public StrategyOptions Strategy { get; set; } = new();

    public PairSpec ToPairSpec() => new(
        new StockSpec(StockSymbol, StockLotSize, StockPriceStep), SharesPerContract, PointValue, FuturesPriceStep);

    public FuturesSettings? Find(string symbol) => Futures.FirstOrDefault(f => f.Symbol == symbol);

    /// <summary>Контракты, ещё не прошедшие последний день торгов, по возрастанию экспирации.</summary>
    public IReadOnlyList<FuturesSettings> Active(DateOnly today) =>
        Futures.Where(f => f.LastTradingDay >= today).OrderBy(f => f.LastTradingDay).ToList();

    public IReadOnlyList<DividendEvent> DividendEvents() => Dividends.Select(d => d.ToEvent()).ToList();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StockSymbol))
            throw new InvalidOperationException("не задан StockSymbol.");
        ToPairSpec();
        if (Futures.Count == 0)
            throw new InvalidOperationException("Futures пуст — укажите хотя бы один контракт.");
        if (Futures.Select(f => f.Symbol).Distinct().Count() != Futures.Count)
            throw new InvalidOperationException("Futures содержит повторяющиеся символы.");
        if (Futures.Any(f => f.InitialMargin <= 0))
            throw new InvalidOperationException("Futures: InitialMargin (ГО) должно быть > 0.");
        Strategy.Validate();
    }
}

public static class PairsConfig
{
    /// <summary>
    /// Читает секцию "Pairs". Каждой паре достаётся своя копия общей секции "Strategy",
    /// поверх которой ложится подсекция "Strategy" пары.
    /// </summary>
    public static List<PairSettings> Load(IConfiguration config)
    {
        if (config.GetSection("Instruments").Exists())
            throw new InvalidOperationException(
                "секция Instruments заменена списком Pairs — перенесите акцию, фьючерсы и дивиденды в Pairs (см. docs/ИНСТРУКЦИЯ.md).");
        if (config.GetSection("Dividends").GetChildren().Any())
            throw new InvalidOperationException("общая секция Dividends больше не используется — перенесите дивиденды в Dividends своей пары.");

        var pairs = new List<PairSettings>();
        foreach (var section in config.GetSection("Pairs").GetChildren())
        {
            var pair = new PairSettings();
            config.GetSection("Strategy").Bind(pair.Strategy);
            section.Bind(pair);
            if (string.IsNullOrWhiteSpace(pair.Name))
                pair.Name = pair.StockSymbol.Split('@')[0];
            pairs.Add(pair);
        }

        if (pairs.Count == 0)
            throw new InvalidOperationException("Pairs пуст — укажите хотя бы одну пару.");
        for (int i = 0; i < pairs.Count; i++)
        {
            try
            {
                pairs[i].Validate();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                throw new InvalidOperationException($"Pairs[{i}] {pairs[i].Name}: {ex.Message}", ex);
            }
        }

        RequireUnique(pairs.Select(p => p.Name), "имя пары");
        // Сверка с брокером считает всю позицию по символу своей — один символ не может быть в двух парах.
        RequireUnique(pairs.Select(p => p.StockSymbol), "акция");
        RequireUnique(pairs.SelectMany(p => p.Futures).Select(f => f.Symbol), "фьючерс");
        return pairs;
    }

    private static void RequireUnique(IEnumerable<string> values, string what)
    {
        if (values.GroupBy(v => v).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new InvalidOperationException($"Pairs: {what} {duplicate.Key} встречается в нескольких парах.");
    }
}

public sealed class FuturesSettings
{
    public string Symbol { get; set; } = "";
    public DateOnly LastTradingDay { get; set; }
    /// <summary>ГО на контракт, руб. Меняется биржей — держите актуальным.</summary>
    public decimal InitialMargin { get; set; }

    public FuturesContract ToContract() => new(Symbol, LastTradingDay);
}

/// <summary>Секция "Commissions".</summary>
public sealed class CommissionSettings
{
    public decimal StockRate { get; set; } = 0.0004m;
    public decimal FuturesPerContract { get; set; } = 2m;

    public CommissionSchedule ToSchedule() => new(StockRate, FuturesPerContract);
}

/// <summary>Элемент секции "Dividends".</summary>
public sealed class DividendSettings
{
    public DateOnly LastBuyDate { get; set; }
    public decimal AmountPerShare { get; set; }

    public DividendEvent ToEvent() => new(LastBuyDate, AmountPerShare);
}

/// <summary>Секция "Paper": стартовые деньги и параметры симулятора цен.</summary>
public sealed class PaperSettings
{
    /// <summary>Откуда брать цены: Simulated — синтетический рынок, Finam — живые котировки (нужен секрет).</summary>
    public PriceSource PriceSource { get; set; } = PriceSource.Simulated;
    public decimal InitialCash { get; set; } = 500_000m;
    /// <summary>Стартовая цена акции, если пары нет в StartPrices.</summary>
    public decimal StartStockPrice { get; set; } = 300m;
    /// <summary>Стартовые цены акций по имени пары: { "SBER": 300, "GAZP": 105 }.</summary>
    public Dictionary<string, decimal> StartPrices { get; set; } = [];
    /// <summary>Средняя годовая ставка, заложенная в цену фьючерса.</summary>
    public decimal AnnualCarry { get; set; } = 0.20m;
    /// <summary>Шум ставки за тик (годовая доля); вокруг AnnualCarry колеблется по Орнштейну–Уленбеку.</summary>
    public decimal CarryNoisePerTick { get; set; } = 0.01m;
    /// <summary>Скорость возврата ставки к среднему за тик, (0; 1].</summary>
    public decimal CarryReversionPerTick { get; set; } = 0.05m;
    /// <summary>Стандартное отклонение цены акции за тик, доля.</summary>
    public decimal StockVolatilityPerTick { get; set; } = 0.0005m;
    /// <summary>Фиксированное зерно для воспроизводимости; null — случайное.</summary>
    public int? Seed { get; set; }
}
