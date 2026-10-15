using CashCarry.Core.Instruments;

namespace CashCarry.Core.Strategy;

public enum DecisionKind
{
    Hold,
    /// <summary>Открыть/добрать Units единиц в Contract.</summary>
    Open,
    /// <summary>Закрыть обе ноги целиком.</summary>
    CloseAll,
    /// <summary>Перенести шорт фьючерса в Contract.</summary>
    Roll,
}

/// <summary>Решение стратегии. AnnualYield — расчётная доходность, на которой оно основано (для логов).</summary>
public sealed record StrategyDecision(
    DecisionKind Kind,
    string Reason,
    int Units = 0,
    FuturesContract? Contract = null,
    decimal? AnnualYield = null)
{
    public static StrategyDecision Hold(string reason, decimal? yield = null) =>
        new(DecisionKind.Hold, reason, AnnualYield: yield);

    public static StrategyDecision Open(FuturesContract contract, int units, decimal yield) =>
        new(DecisionKind.Open, "entry yield above threshold", units, contract, yield);

    public static StrategyDecision CloseAll(string reason, decimal? yield = null) =>
        new(DecisionKind.CloseAll, reason, AnnualYield: yield);

    public static StrategyDecision Roll(FuturesContract next, decimal yield) =>
        new(DecisionKind.Roll, "roll spread above threshold", Contract: next, AnnualYield: yield);
}
