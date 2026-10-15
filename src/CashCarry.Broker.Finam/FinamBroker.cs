using System.Collections.Concurrent;
using CashCarry.Core.Broker;
using CashCarry.Core.Market;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Tradeapi.V1.Accounts;
using Grpc.Tradeapi.V1.Assets;
using Grpc.Tradeapi.V1.Marketdata;
using Grpc.Tradeapi.V1.Orders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static CashCarry.Broker.Finam.FinamConvert;
using CoreOrderState = CashCarry.Core.Broker.OrderState;
using Quote = CashCarry.Core.Market.Quote;
using CoreStatus = CashCarry.Core.Broker.OrderStatus;
using FinamOrderState = Grpc.Tradeapi.V1.Orders.OrderState;
using FinamSide = Grpc.Tradeapi.V1.Side;

namespace CashCarry.Broker.Finam;

/// <summary>
/// IBrokerGateway поверх Finam Trade API.
/// Единицы: у нас количество в лотах (акции) и контрактах (фьючерсы); у Финам для акций — в штуках
/// (перевод по lot_size), для фьючерсов — в контрактах (см. AssetInfo.PiecesPerLot). Цена и комиссия исполнения берутся из сделок по заявке
/// (OrderState их не содержит); если брокер ещё не начислил комиссию — оценка по CommissionSchedule.
/// Соответствие наших ClientOrderId и order_id Финам хранится в памяти процесса.
/// </summary>
public sealed class FinamBroker : IBrokerGateway
{
    private const int FillLookupAttempts = 3;
    private static readonly TimeSpan FillLookupDelay = TimeSpan.FromMilliseconds(300);

    private readonly OrdersService.OrdersServiceClient _orders;
    private readonly AccountsService.AccountsServiceClient _accounts;
    private readonly MarketDataService.MarketDataServiceClient _marketData;
    private readonly AssetsService.AssetsServiceClient _assets;
    private readonly FinamOptions _options;
    private readonly CommissionSchedule _commissions;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, TrackedOrder> _tracked = new();
    private readonly ConcurrentDictionary<string, AssetInfo> _assetInfo = new();

    public FinamBroker(
        FinamConnection connection, CommissionSchedule commissions, TimeProvider? time = null, ILogger<FinamBroker>? logger = null)
        : this(connection.Orders, connection.Accounts, connection.MarketData, connection.Assets,
            connection.Options, commissions, time, logger)
    {
    }

    internal FinamBroker(
        OrdersService.OrdersServiceClient orders,
        AccountsService.AccountsServiceClient accounts,
        MarketDataService.MarketDataServiceClient marketData,
        AssetsService.AssetsServiceClient assets,
        FinamOptions options,
        CommissionSchedule commissions,
        TimeProvider? time = null,
        ILogger<FinamBroker>? logger = null)
    {
        _orders = orders;
        _accounts = accounts;
        _marketData = marketData;
        _assets = assets;
        _options = options;
        _commissions = commissions;
        _time = time ?? TimeProvider.System;
        _log = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<CoreOrderState> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        if (_tracked.ContainsKey(request.ClientOrderId))
            throw new InvalidOperationException($"Заявка {request.ClientOrderId} уже существует.");

        var asset = await AssetAsync(request.Symbol, ct);
        var order = new Order
        {
            AccountId = _options.AccountId,
            Symbol = request.Symbol,
            Quantity = ToProto(request.Quantity * asset.PiecesPerLot),
            Side = request.Side == OrderSide.Buy ? FinamSide.Buy : FinamSide.Sell,
            Type = OrderType.Limit,
            TimeInForce = TimeInForce.Day,
            LimitPrice = ToProto(request.LimitPrice),
        };

        var tracked = new TrackedOrder(request, asset, _time.GetUtcNow());
        _tracked[request.ClientOrderId] = tracked;

        FinamOrderState placed;
        try
        {
            placed = await _orders.PlaceOrderAsync(order, Call(ct));
        }
        catch (RpcException ex) when (IsDefiniteReject(ex.StatusCode))
        {
            tracked.Last = Snapshot(tracked, CoreStatus.Rejected, $"{ex.StatusCode}: {ex.Status.Detail}");
            return tracked.Last;
        }

        tracked.FinamOrderId = placed.OrderId;
        return await UpdateAsync(tracked, placed, ct);
    }

    public async Task<CoreOrderState> CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        var tracked = Tracked(clientOrderId);
        if (tracked.Last.IsTerminal)
            return tracked.Last;

        FinamOrderState state;
        try
        {
            state = await _orders.CancelOrderAsync(
                new CancelOrderRequest { AccountId = _options.AccountId, OrderId = tracked.FinamOrderId }, Call(ct));
        }
        catch (RpcException ex) when (IsDefiniteReject(ex.StatusCode))
        {
            // Чаще всего заявка успела исполниться — берём актуальное состояние.
            _log.LogInformation("Cancel {OrderId} refused ({Status}), reloading order", tracked.FinamOrderId, ex.StatusCode);
            state = await GetFinamOrderAsync(tracked, ct);
        }
        return await UpdateAsync(tracked, state, ct);
    }

    public async Task<CoreOrderState> GetOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        var tracked = Tracked(clientOrderId);
        return tracked.Last.IsTerminal ? tracked.Last : await UpdateAsync(tracked, await GetFinamOrderAsync(tracked, ct), ct);
    }

    public async Task<Quote> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        var response = await _marketData.LastQuoteAsync(new QuoteRequest { Symbol = symbol }, Call(ct));
        decimal bid = ToDecimal(response.Quote?.Bid);
        decimal ask = ToDecimal(response.Quote?.Ask);
        if (bid <= 0 || ask <= 0 || ask < bid)
            throw new InvalidOperationException($"Нет двусторонней котировки {symbol}: bid {bid}, ask {ask}.");
        return new Quote(bid, ask);
    }

    /// <summary>
    /// FreeCash — собственные рубли, доступные для торговли (поле cash, без маржинальных средств):
    /// робот не должен опираться на плечо брокера.
    /// </summary>
    public async Task<AccountState> GetAccountAsync(CancellationToken ct = default)
    {
        var account = await _accounts.GetAccountAsync(new GetAccountRequest { AccountId = _options.AccountId }, Call(ct));

        decimal cash = account.Cash.Where(m => m.CurrencyCode == "RUB").Sum(ToDecimal);
        decimal initialMargin = account.PortfolioCase switch
        {
            GetAccountResponse.PortfolioOneofCase.PortfolioMc => ToDecimal(account.PortfolioMc.InitialMargin),
            GetAccountResponse.PortfolioOneofCase.PortfolioForts => ToDecimal(account.PortfolioForts.MoneyReserved),
            _ => 0m,
        };

        var positions = new Dictionary<string, int>();
        foreach (var position in account.Positions)
        {
            decimal pieces = ToDecimal(position.Quantity);
            if (pieces == 0)
                continue;
            var asset = await AssetAsync(position.Symbol, ct);
            decimal lots = pieces / asset.PiecesPerLot;
            if (lots != decimal.Truncate(lots))
                _log.LogWarning("Position {Symbol}: {Pieces} pcs is not a whole number of lots ({Lot})",
                    position.Symbol, pieces, asset.PiecesPerLot);
            positions[position.Symbol] = (int)decimal.Truncate(lots);
        }

        return new AccountState(cash, cash, initialMargin, ToDecimal(account.UnrealizedProfit), positions);
    }

    /// <summary>Параметры инструмента из Финам (кэшируются): лот, шаг цены, размер контракта, экспирация.</summary>
    public async Task<AssetInfo> AssetAsync(string symbol, CancellationToken ct = default)
    {
        if (_assetInfo.TryGetValue(symbol, out var cached))
            return cached;

        var asset = await _assets.GetAssetAsync(new GetAssetRequest { Symbol = symbol, AccountId = _options.AccountId }, Call(ct));
        decimal lot = ToDecimal(asset.LotSize);
        var info = new AssetInfo(
            symbol,
            lot > 0 ? lot : 1m,
            asset.MinStep / (decimal)Math.Pow(10, asset.Decimals),
            asset.FutureDetails is null ? null : ToDecimal(asset.FutureDetails.ContractSize),
            asset.FutureDetails?.ExpirationDate?.ToDateTimeOffset());
        _assetInfo[symbol] = info;
        return info;
    }

    /// <summary>ГО на продажу одного лота/контракта для этого счёта (для FORTS равно биржевому ГО).</summary>
    public async Task<decimal> ShortInitialMarginAsync(string symbol, CancellationToken ct = default)
    {
        var p = await _assets.GetAssetParamsAsync(
            new GetAssetParamsRequest { Symbol = symbol, AccountId = _options.AccountId }, Call(ct));
        return p.ShortInitialMargin is { } margin ? ToDecimal(margin) : 0m;
    }

    private async Task<FinamOrderState> GetFinamOrderAsync(TrackedOrder tracked, CancellationToken ct) =>
        await _orders.GetOrderAsync(
            new GetOrderRequest { AccountId = _options.AccountId, OrderId = tracked.FinamOrderId }, Call(ct));

    private async Task<CoreOrderState> UpdateAsync(TrackedOrder tracked, FinamOrderState state, CancellationToken ct)
    {
        int filledLots = ToLots(ToDecimal(state.ExecutedQuantity), tracked.Asset.PiecesPerLot);
        var status = MapStatus(state.Status, filledLots, tracked.Request.Quantity);
        if (status == CoreStatus.Filled)
            filledLots = tracked.Request.Quantity;

        if (filledLots > tracked.FilledLots)
            await LoadFillsAsync(tracked, filledLots, ct);

        string? reason = status == CoreStatus.Rejected ? state.StatusDescription ?? state.Status.ToString() : null;
        tracked.Last = Snapshot(tracked, status, reason);
        return tracked.Last;
    }

    /// <summary>Средняя цена и комиссия из сделок по заявке. Сделки могут появиться с задержкой — несколько попыток.</summary>
    private async Task LoadFillsAsync(TrackedOrder tracked, int filledLots, CancellationToken ct)
    {
        decimal expectedPieces = filledLots * tracked.Asset.PiecesPerLot;
        List<Grpc.Tradeapi.V1.AccountTrade> trades = [];

        for (int attempt = 1; attempt <= FillLookupAttempts; attempt++)
        {
            var response = await _accounts.TradesAsync(new TradesRequest
            {
                AccountId = _options.AccountId,
                Limit = 100,
                Interval = new Google.Type.Interval
                {
                    StartTime = Timestamp.FromDateTimeOffset(tracked.PlacedAt.AddMinutes(-1)),
                    EndTime = Timestamp.FromDateTimeOffset(_time.GetUtcNow().AddMinutes(1)),
                },
            }, Call(ct));
            trades = response.Trades.Where(t => t.OrderId == tracked.FinamOrderId).ToList();
            if (trades.Sum(t => ToDecimal(t.Size)) >= expectedPieces)
                break;
            if (attempt < FillLookupAttempts)
                await Task.Delay(FillLookupDelay, _time, ct);
        }

        decimal pieces = trades.Sum(t => ToDecimal(t.Size));
        if (pieces < expectedPieces)
        {
            _log.LogWarning("Order {OrderId}: trades cover {Pieces} of {Expected} pcs, using limit price for the rest",
                tracked.FinamOrderId, pieces, expectedPieces);
        }

        decimal notional = trades.Sum(t => ToDecimal(t.Price) * ToDecimal(t.Size))
            + (expectedPieces - Math.Min(pieces, expectedPieces)) * tracked.Request.LimitPrice;
        tracked.AvgPrice = notional / expectedPieces;

        decimal commission = trades.Sum(t => ToDecimal(t.Commission?.Value));
        tracked.Commission = commission > 0 ? commission : EstimateCommission(tracked, filledLots);
        tracked.FilledLots = filledLots;
    }

    private decimal EstimateCommission(TrackedOrder tracked, int filledLots) => tracked.Asset.IsFutures
        ? _commissions.ForFutures(filledLots)
        : _commissions.ForStock(tracked.AvgPrice, (int)(filledLots * tracked.Asset.LotSize));

    private int ToLots(decimal pieces, decimal lotSize)
    {
        decimal lots = pieces / lotSize;
        if (lots != decimal.Truncate(lots))
            _log.LogWarning("Executed {Pieces} pcs is not a whole number of lots ({Lot})", pieces, lotSize);
        return (int)decimal.Truncate(lots);
    }

    private static CoreOrderState Snapshot(TrackedOrder t, CoreStatus status, string? reason) => new(
        t.Request.ClientOrderId, t.Request.Symbol, t.Request.Side, t.Request.Quantity,
        t.FilledLots, t.AvgPrice, t.Commission, status, reason);

    private TrackedOrder Tracked(string clientOrderId) =>
        _tracked.TryGetValue(clientOrderId, out var t) ? t : throw new KeyNotFoundException($"Заявка {clientOrderId} не найдена.");

    private CallOptions Call(CancellationToken ct) =>
        new(deadline: DateTime.UtcNow + _options.RequestTimeout, cancellationToken: ct);

    private sealed class TrackedOrder(OrderRequest request, AssetInfo asset, DateTimeOffset placedAt)
    {
        public OrderRequest Request { get; } = request;
        public AssetInfo Asset { get; } = asset;
        public DateTimeOffset PlacedAt { get; } = placedAt;
        public string FinamOrderId { get; set; } = "";
        public int FilledLots { get; set; }
        public decimal AvgPrice { get; set; }
        public decimal Commission { get; set; }
        public CoreOrderState Last { get; set; } = new(
            request.ClientOrderId, request.Symbol, request.Side, request.Quantity, 0, 0m, 0m, CoreStatus.Working);
    }
}

/// <summary>Параметры инструмента у Финам. ContractSize и Expiration — только для фьючерсов.</summary>
public sealed record AssetInfo(string Symbol, decimal LotSize, decimal PriceStep, decimal? ContractSize, DateTimeOffset? Expiration)
{
    public bool IsFutures => ContractSize is not null;

    /// <summary>
    /// Сколько единиц quantity Финам в одном нашем лоте. Для акций — lot_size (штук в лоте).
    /// Для фьючерсов lot_size у Финам — множитель контракта (SR: 100 акций), а quantity задаётся
    /// в контрактах (trade_lot_size = 1). Умножение на lot_size превратило бы 1 контракт в 100.
    /// </summary>
    public decimal PiecesPerLot => IsFutures ? 1m : LotSize;
}
