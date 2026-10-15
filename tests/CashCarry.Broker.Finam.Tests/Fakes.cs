using Grpc.Core;
using Grpc.Tradeapi.V1;
using Grpc.Tradeapi.V1.Accounts;
using Grpc.Tradeapi.V1.Assets;
using Grpc.Tradeapi.V1.Marketdata;
using Grpc.Tradeapi.V1.Orders;

namespace CashCarry.Broker.Finam.Tests;

internal static class Calls
{
    public static AsyncUnaryCall<T> Ok<T>(T response) => new(
        Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

    public static AsyncUnaryCall<T> Fail<T>(StatusCode code) => new(
        Task.FromException<T>(new RpcException(new Status(code, "test failure"))),
        Task.FromResult(new Metadata()), () => new Status(code, "test failure"), () => new Metadata(), () => { });

    public static Google.Type.Decimal Dec(string value) => new() { Value = value };
}

internal sealed class FakeOrders : OrdersService.OrdersServiceClient
{
    public List<Order> Placed { get; } = [];
    public Func<Order, AsyncUnaryCall<OrderState>> OnPlace { get; set; } = _ => throw new NotImplementedException();
    public Func<GetOrderRequest, OrderState> OnGet { get; set; } = _ => throw new NotImplementedException();
    public Func<CancelOrderRequest, OrderState> OnCancel { get; set; } = _ => throw new NotImplementedException();

    public override AsyncUnaryCall<OrderState> PlaceOrderAsync(Order request, CallOptions options)
    {
        Placed.Add(request);
        return OnPlace(request);
    }

    public override AsyncUnaryCall<OrderState> GetOrderAsync(GetOrderRequest request, CallOptions options) =>
        Calls.Ok(OnGet(request));

    public override AsyncUnaryCall<OrderState> CancelOrderAsync(CancelOrderRequest request, CallOptions options) =>
        Calls.Ok(OnCancel(request));
}

internal sealed class FakeAccounts : AccountsService.AccountsServiceClient
{
    public GetAccountResponse Account { get; set; } = new();
    public List<AccountTrade> TradeList { get; } = [];

    public override AsyncUnaryCall<GetAccountResponse> GetAccountAsync(GetAccountRequest request, CallOptions options) =>
        Calls.Ok(Account);

    public override AsyncUnaryCall<TradesResponse> TradesAsync(TradesRequest request, CallOptions options) =>
        Calls.Ok(new TradesResponse { Trades = { TradeList } });
}

internal sealed class FakeMarketData : MarketDataService.MarketDataServiceClient
{
    public Dictionary<string, Quote> Quotes { get; } = [];

    public override AsyncUnaryCall<QuoteResponse> LastQuoteAsync(QuoteRequest request, CallOptions options) =>
        Calls.Ok(new QuoteResponse { Symbol = request.Symbol, Quote = Quotes[request.Symbol] });
}

internal sealed class FakeAssets : AssetsService.AssetsServiceClient
{
    public Dictionary<string, GetAssetResponse> AssetMap { get; } = new()
    {
        ["SBER@MISX"] = new GetAssetResponse { Ticker = "SBER", LotSize = Calls.Dec("10"), Decimals = 2, MinStep = 1 },
        // Как отдаёт реальный Финам: lot_size фьючерса = множитель контракта, а quantity — в контрактах.
        ["SRZ6@RTSX"] = new GetAssetResponse
        {
            Ticker = "SRZ6",
            LotSize = Calls.Dec("100.0"),
            Decimals = 0,
            MinStep = 1,
            FutureDetails = new GetAssetResponse.Types.FutureDetails { ContractSize = Calls.Dec("100") },
        },
    };

    public override AsyncUnaryCall<GetAssetResponse> GetAssetAsync(GetAssetRequest request, CallOptions options) =>
        Calls.Ok(AssetMap[request.Symbol]);
}
