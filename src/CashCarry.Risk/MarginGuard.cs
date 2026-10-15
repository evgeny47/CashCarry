using CashCarry.Core.Broker;
using CashCarry.Core.Instruments;
using CashCarry.Core.Market;

namespace CashCarry.Risk;

public enum MarginStatus
{
    Ok,
    /// <summary>Свободных денег меньше резерва под ВМ: новые входы запрещены.</summary>
    Warning,
    /// <summary>Свободные деньги отрицательны: риск принудительного закрытия брокером.</summary>
    Critical,
}

/// <summary>
/// Резерв под вариационную маржу для одной пары. Деньги на счёте общие для всех пар,
/// поэтому резерв остальных пар передаётся снаружи (otherReserve).
/// </summary>
public sealed class MarginGuard(PairSpec pair, CommissionSchedule commissions, RiskLimits limits)
{
    /// <summary>Сколько свободных денег держать под ВМ для шорта contracts контрактов этой пары.</summary>
    public decimal Reserve(int contracts, decimal futuresPrice) =>
        Math.Abs(contracts) * futuresPrice * pair.PointValue * limits.VariationMarginReserve;

    /// <summary>
    /// Сколько единиц можно открыть, оставив резерв под ВМ и для уже открытых, и для новых контрактов.
    /// FreeCash брокера уже учитывает ГО по открытым позициям. otherReserve — резерв остальных пар.
    /// </summary>
    public int MaxUnitsToOpen(
        AccountState account, Quote stock, Quote futures, decimal initialMargin, int heldUnits, decimal otherReserve = 0m)
    {
        int k = pair.SharesPerContract;
        decimal reservePerUnit = Reserve(1, futures.Ask);
        decimal perUnit = stock.Ask * k + commissions.ForStock(stock.Ask, k)
            + initialMargin + commissions.ForFutures(1) + reservePerUnit;
        decimal available = account.FreeCash - heldUnits * reservePerUnit - otherReserve;
        return available <= 0 ? 0 : (int)Math.Floor(available / perUnit);
    }

    /// <summary>Свободные деньги против суммарного резерва всех пар.</summary>
    public static MarginStatus Assess(AccountState account, decimal requiredReserve)
    {
        if (account.FreeCash < 0)
            return MarginStatus.Critical;
        return account.FreeCash < requiredReserve ? MarginStatus.Warning : MarginStatus.Ok;
    }
}
