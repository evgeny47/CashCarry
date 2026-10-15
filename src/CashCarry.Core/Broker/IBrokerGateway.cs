using CashCarry.Core.Market;

namespace CashCarry.Core.Broker;

public enum OrderSide { Buy, Sell }

public enum OrderStatus
{
    /// <summary>Активна (может быть частично исполнена: FilledQuantity &gt; 0).</summary>
    Working,
    Filled,
    /// <summary>Снята; часть могла успеть исполниться.</summary>
    Cancelled,
    Rejected,
}

/// <summary>
/// Лимитная заявка. Quantity — в лотах для акций и в контрактах для фьючерсов.
/// Рыночных заявок нет намеренно: на срочном рынке их нет, а «агрессивный лимит» даёт контроль проскальзывания.
/// </summary>
public sealed record OrderRequest(string ClientOrderId, string Symbol, OrderSide Side, int Quantity, decimal LimitPrice);

public sealed record OrderState(
    string ClientOrderId,
    string Symbol,
    OrderSide Side,
    int Quantity,
    int FilledQuantity,
    decimal AvgFillPrice,
    decimal Commission,
    OrderStatus Status,
    string? RejectReason = null)
{
    public bool IsTerminal => Status != OrderStatus.Working;
}

/// <summary>
/// Состояние счёта. Positions — со знаком: акции в лотах, фьючерсы в контрактах (шорт &lt; 0).
/// FreeCash = деньги + вариационная маржа − заблокированное ГО.
/// </summary>
public sealed record AccountState(
    decimal Cash,
    decimal FreeCash,
    decimal InitialMarginBlocked,
    decimal VariationMargin,
    IReadOnlyDictionary<string, int> Positions)
{
    public int PositionOf(string symbol) => Positions.TryGetValue(symbol, out int qty) ? qty : 0;
}

/// <summary>
/// Шлюз брокера. Отклонение заявки — это OrderState со статусом Rejected, а не исключение;
/// исключение означает, что состояние заявки неизвестно (сеть, таймаут).
/// </summary>
public interface IBrokerGateway
{
    Task<OrderState> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    Task<OrderState> CancelOrderAsync(string clientOrderId, CancellationToken ct = default);
    Task<OrderState> GetOrderAsync(string clientOrderId, CancellationToken ct = default);
    Task<Quote> GetQuoteAsync(string symbol, CancellationToken ct = default);
    Task<AccountState> GetAccountAsync(CancellationToken ct = default);
}
