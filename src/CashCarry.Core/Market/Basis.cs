using CashCarry.Core.Instruments;

namespace CashCarry.Core.Market;

/// <summary>
/// Базис в рублях на одну единицу пары: B = F × PointValue − S × K.
/// Считается по исполнимым ценам, а не по mid: спред — реальные издержки входа/выхода.
/// Прибыль пары без комиссий = B(вход) − B(выход).
/// </summary>
public static class Basis
{
    /// <summary>Вход: покупаем акцию по ask, продаём фьючерс по bid.</summary>
    public static decimal ToOpen(PairSpec pair, Quote stock, Quote futures) =>
        futures.Bid * pair.PointValue - stock.Ask * pair.SharesPerContract;

    /// <summary>Выход: продаём акцию по bid, откупаем фьючерс по ask.</summary>
    public static decimal ToClose(PairSpec pair, Quote stock, Quote futures) =>
        futures.Ask * pair.PointValue - stock.Bid * pair.SharesPerContract;
}
