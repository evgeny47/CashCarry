namespace CashCarry.Core.Market;

/// <summary>Свеча. Time — начало интервала, UTC. Volume — в штуках (акции) или контрактах (фьючерсы).</summary>
public sealed record Candle(DateTimeOffset Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>Таймфреймы, которые отдаёт Финам для внутридневной истории.</summary>
public enum Timeframe { M1, M5, M15, M30, H1 }

public static class TimeframeExtensions
{
    public static TimeSpan Duration(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.H1 => TimeSpan.FromHours(1),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null),
    };
}
