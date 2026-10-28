using CashCarry.Core.Broker;
using CashCarry.Core.Market;

namespace CashCarry.Backtest;

public sealed record Fill(DateTimeOffset Time, string Symbol, OrderSide Side, int Quantity, decimal Price, decimal Commission);

/// <summary>Обёртка брокера, которая записывает каждое исполнение (приращение FilledQuantity заявки) с временем тестера.</summary>
public sealed class RecordingBroker(IBrokerGateway inner, TimeProvider clock) : IBrokerGateway
{
    private readonly Dictionary<string, OrderState> _seen = new();

    public List<Fill> Fills { get; } = [];

    public async Task<OrderState> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
        Record(await inner.PlaceOrderAsync(request, ct));

    public async Task<OrderState> CancelOrderAsync(string clientOrderId, CancellationToken ct = default) =>
        Record(await inner.CancelOrderAsync(clientOrderId, ct));

    public async Task<OrderState> GetOrderAsync(string clientOrderId, CancellationToken ct = default) =>
        Record(await inner.GetOrderAsync(clientOrderId, ct));

    public Task<Quote> GetQuoteAsync(string symbol, CancellationToken ct = default) => inner.GetQuoteAsync(symbol, ct);

    public Task<AccountState> GetAccountAsync(CancellationToken ct = default) => inner.GetAccountAsync(ct);

    private OrderState Record(OrderState state)
    {
        int filledBefore = 0;
        decimal valueBefore = 0m, commissionBefore = 0m;
        if (_seen.TryGetValue(state.ClientOrderId, out var previous))
        {
            filledBefore = previous.FilledQuantity;
            valueBefore = previous.AvgFillPrice * previous.FilledQuantity;
            commissionBefore = previous.Commission;
        }
        _seen[state.ClientOrderId] = state;

        int qty = state.FilledQuantity - filledBefore;
        if (qty > 0)
            Fills.Add(new Fill(clock.GetUtcNow(), state.Symbol, state.Side, qty,
                (state.AvgFillPrice * state.FilledQuantity - valueBefore) / qty, state.Commission - commissionBefore));
        return state;
    }
}
