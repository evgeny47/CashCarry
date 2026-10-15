namespace CashCarry.Core.Strategy;

public enum ExitMode
{
    /// <summary>Только досрочный выход по сужению базиса; у экспирации — закрытие без переноса.</summary>
    Early,
    /// <summary>Держим до экспирации, затем перенос или закрытие.</summary>
    Expiry,
    /// <summary>Досрочный выход, если выгодно; иначе — перенос или закрытие у экспирации.</summary>
    Both,
}

/// <summary>
/// Параметры стратегии. Доходности — годовые, в долях (0.18 = 18 %),
/// считаются на капитал = стоимость акций + ГО по фьючерсу.
/// </summary>
public sealed class StrategyOptions
{
    public ExitMode ExitMode { get; set; } = ExitMode.Both;

    /// <summary>Минимальная ожидаемая доходность для входа (базис + дивиденды − комиссии туда-обратно).</summary>
    public decimal MinEntryAnnualYield { get; set; } = 0.20m;

    /// <summary>
    /// Досрочный выход, если оставшийся базис (то, что ещё можно заработать, держа до экспирации)
    /// даёт меньше этой доходности. Должна быть ниже MinEntryAnnualYield, иначе вход и выход «дребезжат».
    /// </summary>
    public decimal MinHoldAnnualYield { get; set; } = 0.10m;

    /// <summary>Минимальный чистый PnL на единицу (руб.) для досрочного выхода.</summary>
    public decimal EarlyExitMinProfitPerUnit { get; set; } = 0m;

    /// <summary>Минимальная доходность календарного спреда для переноса на следующую серию.</summary>
    public decimal MinRollAnnualYield { get; set; } = 0.18m;

    /// <summary>Не входить в контракт, до последнего дня торгов которого меньше N дней.</summary>
    public int MinDaysToExpiryForEntry { get; set; } = 14;

    /// <summary>За сколько календарных дней до последнего дня торгов переносить или закрывать.</summary>
    public int RollWindowDays { get; set; } = 3;

    public int UnitsPerEntry { get; set; } = 1;
    public int MaxUnits { get; set; } = 1;

    public decimal DividendTaxRate { get; set; } = 0.13m;

    public void Validate()
    {
        if (MinHoldAnnualYield >= MinEntryAnnualYield)
            throw new InvalidOperationException("MinHoldAnnualYield должна быть меньше MinEntryAnnualYield.");
        if (RollWindowDays < 1)
            throw new InvalidOperationException("RollWindowDays должен быть >= 1.");
        if (MinDaysToExpiryForEntry <= RollWindowDays)
            throw new InvalidOperationException("MinDaysToExpiryForEntry должен быть больше RollWindowDays.");
        if (UnitsPerEntry < 1 || MaxUnits < UnitsPerEntry)
            throw new InvalidOperationException("Нужно 1 <= UnitsPerEntry <= MaxUnits.");
        if (DividendTaxRate is < 0m or >= 1m)
            throw new InvalidOperationException("DividendTaxRate должна быть в [0; 1).");
    }
}
