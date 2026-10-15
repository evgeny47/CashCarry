using CashCarry.Core.Broker;
using CashCarry.Core.Market;
using Grpc.Core;
using Grpc.Tradeapi.V1;
using Grpc.Tradeapi.V1.Accounts;
using Grpc.Tradeapi.V1.Orders;
using static CashCarry.Broker.Finam.Tests.Calls;
using CoreStatus = CashCarry.Core.Broker.OrderStatus;
using FinamQuote = Grpc.Tradeapi.V1.Marketdata.Quote;
using FinamState = Grpc.Tradeapi.V1.Orders.OrderState;
using FinamStatus = Grpc.Tradeapi.V1.Orders.OrderStatus;

namespace CashCarry.Broker.Finam.Tests;

public class FinamBrokerTests
{
    private const string Sber = "SBER@MISX";
    private const string Sr = "SRZ6@RTSX";

    private readonly FakeOrders _orders = new();
    private readonly FakeAccounts _accounts = new();
    private readonly FakeMarketData _marketData = new();
    private readonly FakeAssets _assets = new();
    private readonly FinamBroker _broker;

    public FinamBrokerTests()
    {
        _broker = new FinamBroker(_orders, _accounts, _marketData, _assets,
            new FinamOptions { AccountId = "A1" }, new CommissionSchedule(0.0004m, 2m));
    }

    private static FinamState State(string id, FinamStatus status, string executedPieces) =>
        new() { OrderId = id, Status = status, ExecutedQuantity = Dec(executedPieces) };

    private static AccountTrade Trade(string orderId, string price, string size, string commission = "0") => new()
    {
        OrderId = orderId,
        Price = Dec(price),
        Size = Dec(size),
        Commission = new AccountTrade.Types.Commission { Value = Dec(commission), Currency = "RUB" },
    };

    private Task<Core.Broker.OrderState> Place(string symbol, OrderSide side, int lots, decimal price, string id = "c1") =>
        _broker.PlaceOrderAsync(new OrderRequest(id, symbol, side, lots, price));

    [Fact]
    public async Task Lots_become_pieces_and_fill_price_comes_from_trades()
    {
        _orders.OnPlace = _ => Ok(State("o1", FinamStatus.Filled, "20"));
        _accounts.TradeList.AddRange([Trade("o1", "300.10", "10", "1.2"), Trade("o1", "300.20", "10", "1.2"), Trade("other", "1", "1")]);

        var state = await Place(Sber, OrderSide.Buy, 2, 300.50m);

        var sent = Assert.Single(_orders.Placed);
        Assert.Equal("A1", sent.AccountId);
        Assert.Equal("20", sent.Quantity.Value); // 2 лота × 10 шт.
        Assert.Equal("300.50", sent.LimitPrice.Value);
        Assert.Equal(Side.Buy, sent.Side);
        Assert.Equal(OrderType.Limit, sent.Type);
        Assert.Equal(TimeInForce.Day, sent.TimeInForce);

        Assert.Equal(CoreStatus.Filled, state.Status);
        Assert.Equal(2, state.FilledQuantity);
        Assert.Equal(300.15m, state.AvgFillPrice);
        Assert.Equal(2.4m, state.Commission);
    }

    [Fact]
    public async Task Commission_is_estimated_while_broker_has_not_charged_it()
    {
        _orders.OnPlace = _ => Ok(State("o1", FinamStatus.Filled, "3"));
        _accounts.TradeList.Add(Trade("o1", "31000", "3"));

        var state = await Place(Sr, OrderSide.Sell, 3, 30900m);

        Assert.Equal("3", _orders.Placed[0].Quantity.Value); // 3 контракта, а не 3 × lot_size(100)
        Assert.Equal(6m, state.Commission);                  // 3 × 2 ₽
    }

    [Fact]
    public async Task Definite_rejection_is_a_rejected_state_not_an_exception()
    {
        _orders.OnPlace = _ => Fail<FinamState>(StatusCode.FailedPrecondition);

        var state = await Place(Sber, OrderSide.Buy, 1, 300m);
        var again = await _broker.GetOrderAsync("c1");

        Assert.Equal(CoreStatus.Rejected, state.Status);
        Assert.Contains("FailedPrecondition", state.RejectReason);
        Assert.Equal(CoreStatus.Rejected, again.Status);
    }

    [Fact]
    public async Task Unknown_outcome_propagates_so_kill_switch_can_trip()
    {
        _orders.OnPlace = _ => Fail<FinamState>(StatusCode.Unavailable);

        await Assert.ThrowsAsync<RpcException>(() => Place(Sber, OrderSide.Buy, 1, 300m));
    }

    [Fact]
    public async Task Partial_fill_then_cancel_keeps_executed_part()
    {
        _orders.OnPlace = _ => Ok(State("o1", FinamStatus.PartiallyFilled, "10"));
        CancelOrderRequest? cancelRequest = null;
        _orders.OnCancel = r => { cancelRequest = r; return State(r.OrderId, FinamStatus.Canceled, "10"); };
        _accounts.TradeList.Add(Trade("o1", "300.10", "10", "1.2"));

        var placed = await Place(Sber, OrderSide.Buy, 2, 300.50m);
        var cancelled = await _broker.CancelOrderAsync("c1");

        Assert.Equal(CoreStatus.Working, placed.Status);
        Assert.Equal(1, placed.FilledQuantity);
        Assert.Equal(CoreStatus.Cancelled, cancelled.Status);
        Assert.Equal(1, cancelled.FilledQuantity);
        Assert.Equal(300.10m, cancelled.AvgFillPrice);
        Assert.Equal("o1", cancelRequest!.OrderId); // отмена по номеру заявки Финам, не по нашему ClientOrderId
        Assert.Equal("A1", cancelRequest.AccountId);
    }

    [Fact]
    public async Task Account_positions_are_in_lots_and_free_cash_is_own_rubles()
    {
        _accounts.Account = new GetAccountResponse
        {
            Cash = { new Google.Type.Money { CurrencyCode = "RUB", Units = 100_000, Nanos = 500_000_000 },
                     new Google.Type.Money { CurrencyCode = "USD", Units = 10 } },
            Positions =
            {
                new Position { Symbol = Sber, Quantity = Dec("200") },
                new Position { Symbol = Sr, Quantity = Dec("-2") },
            },
        };

        var account = await _broker.GetAccountAsync();

        Assert.Equal(100_000.5m, account.FreeCash);
        Assert.Equal(20, account.PositionOf(Sber));
        Assert.Equal(-2, account.PositionOf(Sr));
    }

    [Fact]
    public async Task Quote_without_two_sides_is_an_error()
    {
        _marketData.Quotes[Sber] = new FinamQuote { Bid = Dec("0"), Ask = Dec("300.1") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _broker.GetQuoteAsync(Sber));
    }

    [Fact]
    public async Task Asset_info_reads_lot_step_and_contract_size()
    {
        var stock = await _broker.AssetAsync(Sber);
        var futures = await _broker.AssetAsync(Sr);

        Assert.Equal(10m, stock.LotSize);
        Assert.Equal(0.01m, stock.PriceStep);
        Assert.False(stock.IsFutures);
        Assert.Equal(100m, futures.ContractSize);
        Assert.Equal(1m, futures.PriceStep);
    }

    [Theory]
    [InlineData(FinamStatus.New, 0, 2, CoreStatus.Working)]
    [InlineData(FinamStatus.PartiallyFilled, 1, 2, CoreStatus.Working)]
    [InlineData(FinamStatus.Filled, 2, 2, CoreStatus.Filled)]
    [InlineData(FinamStatus.Executed, 2, 2, CoreStatus.Filled)]
    [InlineData(FinamStatus.Canceled, 0, 2, CoreStatus.Cancelled)]
    [InlineData(FinamStatus.Canceled, 2, 2, CoreStatus.Filled)]
    [InlineData(FinamStatus.Expired, 1, 2, CoreStatus.Cancelled)]
    [InlineData(FinamStatus.RejectedByExchange, 0, 2, CoreStatus.Rejected)]
    [InlineData(FinamStatus.DeniedByBroker, 0, 2, CoreStatus.Rejected)]
    [InlineData(FinamStatus.Rejected, 1, 2, CoreStatus.Cancelled)]
    public void Status_mapping(FinamStatus finam, int filled, int quantity, CoreStatus expected)
    {
        Assert.Equal(expected, FinamConvert.MapStatus(finam, filled, quantity));
    }
}
