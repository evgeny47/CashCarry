using System.Globalization;
using Grpc.Core;
using CoreStatus = CashCarry.Core.Broker.OrderStatus;
using FinamStatus = Grpc.Tradeapi.V1.Orders.OrderStatus;

namespace CashCarry.Broker.Finam;

internal static class FinamConvert
{
    public static decimal ToDecimal(Google.Type.Decimal? value) =>
        value is null || string.IsNullOrEmpty(value.Value)
            ? 0m
            : decimal.Parse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static Google.Type.Decimal ToProto(decimal value) =>
        new() { Value = value.ToString(CultureInfo.InvariantCulture) };

    public static decimal ToDecimal(Google.Type.Money money) => money.Units + money.Nanos / 1_000_000_000m;

    /// <summary>
    /// Статус Финам → наш. Снятая или отклонённая с частичным исполнением — Cancelled:
    /// исполненная часть реальна и должна быть учтена.
    /// </summary>
    public static CoreStatus MapStatus(FinamStatus status, int filledLots, int quantityLots) => status switch
    {
        FinamStatus.Filled or FinamStatus.Executed => CoreStatus.Filled,
        FinamStatus.Canceled or FinamStatus.Expired or FinamStatus.DoneForDay or FinamStatus.Replaced =>
            filledLots >= quantityLots ? CoreStatus.Filled : CoreStatus.Cancelled,
        FinamStatus.Rejected or FinamStatus.Failed or FinamStatus.DeniedByBroker or FinamStatus.RejectedByExchange =>
            filledLots > 0 ? CoreStatus.Cancelled : CoreStatus.Rejected,
        _ => CoreStatus.Working,
    };

    /// <summary>
    /// Ошибки, после которых заявка точно не выставлена. Остальные (таймаут, обрыв, внутренняя ошибка)
    /// означают, что состояние неизвестно — их пробрасываем, исполнитель включит kill switch.
    /// </summary>
    public static bool IsDefiniteReject(StatusCode code) => code is
        StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.PermissionDenied or
        StatusCode.OutOfRange or StatusCode.NotFound or StatusCode.ResourceExhausted or
        StatusCode.AlreadyExists or StatusCode.Unauthenticated;
}
