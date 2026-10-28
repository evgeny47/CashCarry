using CashCarry.Core.Market;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Tradeapi.V1.Marketdata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static CashCarry.Broker.Finam.FinamConvert;
using FinamTimeFrame = Grpc.Tradeapi.V1.Marketdata.TimeFrame;

namespace CashCarry.Broker.Finam;

/// <summary>
/// Исторические свечи Финам (MarketDataService.Bars). Один запрос покрывает ограниченный интервал
/// (минутки — 7 дней, остальное — 30), поэтому период режется на куски. На лимит запросов отвечаем паузой и повтором.
/// </summary>
public sealed class FinamHistory(FinamConnection connection, ILogger<FinamHistory>? logger = null)
{
    private const int MaxAttempts = 6;
    private static readonly TimeSpan PauseBetweenRequests = TimeSpan.FromMilliseconds(250);

    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<IReadOnlyList<Candle>> CandlesAsync(
        string symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var (frame, window) = Map(timeframe);
        // Последняя свеча ещё формируется — в историю её не кладём, иначе тестер увидит недостроенную цену.
        var completeBefore = DateTimeOffset.UtcNow - timeframe.Duration();
        if (to > completeBefore)
            to = completeBefore;
        var candles = new SortedDictionary<DateTimeOffset, Candle>();
        for (var start = from; start < to; start += window)
        {
            var end = start + window < to ? start + window : to;
            var request = new BarsRequest
            {
                Symbol = symbol,
                Timeframe = frame,
                Interval = new Google.Type.Interval { StartTime = Timestamp.FromDateTimeOffset(start), EndTime = Timestamp.FromDateTimeOffset(end) },
            };
            var response = await WithRetryAsync(symbol, () => connection.MarketData.BarsAsync(request, connection.Call(ct)).ResponseAsync, ct);
            foreach (var bar in response.Bars)
            {
                var time = bar.Timestamp.ToDateTimeOffset();
                if (time >= from && time < to)
                    candles[time] = new Candle(time, ToDecimal(bar.Open), ToDecimal(bar.High), ToDecimal(bar.Low),
                        ToDecimal(bar.Close), ToDecimal(bar.Volume));
            }
            await Task.Delay(PauseBetweenRequests, ct);
        }
        return candles.Values.ToList();
    }

    private async Task<T> WithRetryAsync<T>(string symbol, Func<Task<T>> call, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (RpcException ex) when (attempt < MaxAttempts && ex.StatusCode is
                                              StatusCode.ResourceExhausted or StatusCode.Unavailable or StatusCode.DeadlineExceeded)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _log.LogWarning("Finam bars {Symbol}: {Status}, retry {Attempt} in {Delay}", symbol, ex.StatusCode, attempt, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static (FinamTimeFrame Frame, TimeSpan Window) Map(Timeframe timeframe) => timeframe switch
    {
        Timeframe.M1 => (FinamTimeFrame.M1, TimeSpan.FromDays(7)),
        Timeframe.M5 => (FinamTimeFrame.M5, TimeSpan.FromDays(30)),
        Timeframe.M15 => (FinamTimeFrame.M15, TimeSpan.FromDays(30)),
        Timeframe.M30 => (FinamTimeFrame.M30, TimeSpan.FromDays(30)),
        Timeframe.H1 => (FinamTimeFrame.H1, TimeSpan.FromDays(30)),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null),
    };
}
