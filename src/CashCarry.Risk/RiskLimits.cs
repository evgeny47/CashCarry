namespace CashCarry.Risk;

public sealed class RiskLimits
{
    /// <summary>
    /// Резерв свободных денег под вариационную маржу, доля номинала шорта фьючерса.
    /// При росте акции шорт фьючерса ежедневно платит ВМ деньгами, а прибыль по акциям остаётся на бумаге —
    /// без резерва рост на 10 % может привести к маржин-коллу при полностью захеджированной позиции.
    /// </summary>
    public decimal VariationMarginReserve { get; set; } = 0.10m;

    /// <summary>Сколько проверок подряд допускается расхождение позиций с брокером до срабатывания kill switch.</summary>
    public int MaxHedgeMismatchChecks { get; set; } = 3;
}
