using CashCarry.Core.Broker;
using CashCarry.Core.Market;

namespace CashCarry.Broker.Paper.Tests;

public class PaperBrokerTests
{
    private const string Sber = "SBER";
    private const string Sbrf = "SBRF-12.26";

    private static PaperBroker Broker(decimal cash = 100_000m)
    {
        var broker = new PaperBroker(
            [
                new PaperInstrument(Sber, InstrumentKind.Stock, LotSize: 10),
                new PaperInstrument(Sbrf, InstrumentKind.Futures, InitialMargin: 5000m),
            ],
            new CommissionSchedule(stockRate: 0.0004m, futuresPerContract: 2m),
            cash);
        broker.SetQuote(Sber, new Quote(300.00m, 300.10m));
        broker.SetQuote(Sbrf, new Quote(31000m, 31010m));
        return broker;
    }

    private static Task<OrderState> Place(PaperBroker b, string symbol, OrderSide side, int qty, decimal price) =>
        b.PlaceOrderAsync(new OrderRequest(Guid.NewGuid().ToString("N"), symbol, side, qty, price));

    [Fact]
    public async Task Marketable_buy_fills_at_ask_and_pays_commission()
    {
        var broker = Broker();

        var order = await Place(broker, Sber, OrderSide.Buy, 10, 301m);
        var account = await broker.GetAccountAsync();

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(300.10m, order.AvgFillPrice);
        Assert.Equal(12.004m, order.Commission);
        Assert.Equal(100_000m - 30_010m - 12.004m, account.Cash);
        Assert.Equal(10, account.PositionOf(Sber));
    }

    [Fact]
    public async Task Passive_limit_rests_until_quote_crosses()
    {
        var broker = Broker();

        var order = await Place(broker, Sber, OrderSide.Buy, 1, 299m);
        Assert.Equal(OrderStatus.Working, order.Status);

        broker.SetQuote(Sber, new Quote(298.90m, 299.00m));
        var filled = await broker.GetOrderAsync(order.ClientOrderId);

        Assert.Equal(OrderStatus.Filled, filled.Status);
        Assert.Equal(299.00m, filled.AvgFillPrice);
    }

    [Fact]
    public async Task Limited_liquidity_gives_partial_fill_and_cancel_keeps_it()
    {
        var broker = Broker();
        broker.SetQuote(Sber, new Quote(300.00m, 300.10m), askLots: 5);

        var order = await Place(broker, Sber, OrderSide.Buy, 10, 301m);
        var cancelled = await broker.CancelOrderAsync(order.ClientOrderId);

        Assert.Equal(OrderStatus.Working, order.Status);
        Assert.Equal(5, order.FilledQuantity);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Equal(5, (await broker.GetAccountAsync()).PositionOf(Sber));
    }

    [Fact]
    public async Task Rejects_buy_without_funds()
    {
        var order = await Place(Broker(cash: 1000m), Sber, OrderSide.Buy, 10, 301m);

        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal("insufficient funds", order.RejectReason);
    }

    [Fact]
    public async Task Rejects_stock_short_selling()
    {
        var order = await Place(Broker(), Sber, OrderSide.Sell, 1, 299m);

        Assert.Equal(OrderStatus.Rejected, order.Status);
    }

    [Fact]
    public async Task Rejects_futures_short_without_initial_margin()
    {
        var order = await Place(Broker(cash: 4000m), Sbrf, OrderSide.Sell, 1, 30900m);

        Assert.Equal("insufficient margin", order.RejectReason);
    }

    [Fact]
    public async Task Futures_short_blocks_margin_and_settles_variation_margin_at_clearing()
    {
        var broker = Broker();
        await Place(broker, Sbrf, OrderSide.Sell, 2, 30900m);

        var afterTrade = await broker.GetAccountAsync();
        Assert.Equal(-2, afterTrade.PositionOf(Sbrf));
        Assert.Equal(99_996m, afterTrade.Cash);
        Assert.Equal(10_000m, afterTrade.InitialMarginBlocked);
        Assert.Equal(-10m, afterTrade.VariationMargin); // продали по 31000, mid 31005
        Assert.Equal(99_996m - 10m - 10_000m, afterTrade.FreeCash);

        broker.SetQuote(Sbrf, new Quote(31100m, 31110m));
        broker.RunClearing();
        var afterClearing = await broker.GetAccountAsync();

        Assert.Equal(99_996m - 210m, afterClearing.Cash);
        Assert.Equal(0m, afterClearing.VariationMargin);
    }

    [Fact]
    public async Task Injected_rejection_applies_once()
    {
        var broker = Broker();
        broker.RejectNext(Sbrf, "test");

        var first = await Place(broker, Sbrf, OrderSide.Sell, 1, 30900m);
        var second = await Place(broker, Sbrf, OrderSide.Sell, 1, 30900m);

        Assert.Equal("test", first.RejectReason);
        Assert.Equal(OrderStatus.Filled, second.Status);
    }
}
