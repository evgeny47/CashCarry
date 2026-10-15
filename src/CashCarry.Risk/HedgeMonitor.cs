using CashCarry.Core.Broker;
using CashCarry.Core.Instruments;
using CashCarry.Core.Positions;

namespace CashCarry.Risk;

/// <summary>
/// Расхождение позиций брокера с учётом робота. NetShares — непокрытая экспозиция в акциях
/// (&gt; 0 — лишний лонг, &lt; 0 — лишний шорт).
/// </summary>
public sealed record HedgeReport(int StockLotsDiff, int FuturesDiff, int NetShares)
{
    public bool IsConsistent => StockLotsDiff == 0 && FuturesDiff == 0;
}

/// <summary>
/// Сверяет позиции брокера с PairPosition одной пары. Считается, что акция и фьючерсы пары на счёте принадлежат только роботу.
/// Разовое расхождение допускается (брокер может отдавать позиции с задержкой после сделки);
/// MaxHedgeMismatchChecks расхождений подряд — срабатывает kill switch.
/// </summary>
public sealed class HedgeMonitor(PairSpec pair, IKillSwitch killSwitch, RiskLimits limits)
{
    private int _mismatches;

    public HedgeReport Reconcile(PairPosition? position, AccountState account, string futuresSymbol)
    {
        int units = position?.Units ?? 0;
        int stockDiff = account.PositionOf(pair.Stock.Symbol) - units * pair.StockLotsPerUnit;
        int futuresDiff = account.PositionOf(futuresSymbol) + units;
        int netShares = stockDiff * pair.Stock.LotSize + futuresDiff * pair.SharesPerContract;
        return new HedgeReport(stockDiff, futuresDiff, netShares);
    }

    public HedgeReport Observe(PairPosition? position, AccountState account, string futuresSymbol)
    {
        var report = Reconcile(position, account, futuresSymbol);
        if (report.IsConsistent)
        {
            _mismatches = 0;
            return report;
        }

        if (++_mismatches >= limits.MaxHedgeMismatchChecks)
            killSwitch.Trip(
                $"hedge mismatch {_mismatches} checks in a row: stock lots {report.StockLotsDiff:+#;-#;0}, " +
                $"futures {report.FuturesDiff:+#;-#;0}, net {report.NetShares:+#;-#;0} shares");
        return report;
    }
}
