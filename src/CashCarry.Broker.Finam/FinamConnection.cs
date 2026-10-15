using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Tradeapi.V1.Accounts;
using Grpc.Tradeapi.V1.Assets;
using Grpc.Tradeapi.V1.Auth;
using Grpc.Tradeapi.V1.Marketdata;
using Grpc.Tradeapi.V1.Orders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashCarry.Broker.Finam;

/// <summary>
/// gRPC-подключение к Finam Trade API. По секрету получает JWT (AuthService.Auth), подставляет его
/// в каждый запрос как "Authorization: Bearer ...", и сам обновляет за пару минут до истечения.
/// </summary>
public sealed class FinamConnection : IDisposable
{
    private static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FallbackLifetime = TimeSpan.FromMinutes(10);

    private readonly FinamOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly GrpcChannel _authChannel;
    private readonly GrpcChannel _channel;
    private readonly AuthService.AuthServiceClient _auth;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public FinamConnection(FinamOptions options, TimeProvider? time = null, ILogger<FinamConnection>? logger = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
        _log = (ILogger?)logger ?? NullLogger.Instance;

        _authChannel = GrpcChannel.ForAddress(options.Endpoint, new GrpcChannelOptions { HttpHandler = CreateHandler(options) });
        _auth = new AuthService.AuthServiceClient(_authChannel);

        var credentials = CallCredentials.FromInterceptor(async (context, metadata) =>
            metadata.Add("Authorization", "Bearer " + await GetTokenAsync(context.CancellationToken)));
        _channel = GrpcChannel.ForAddress(options.Endpoint, new GrpcChannelOptions
        {
            HttpHandler = CreateHandler(options),
            Credentials = ChannelCredentials.Create(ChannelCredentials.SecureSsl, credentials),
        });

        Accounts = new AccountsService.AccountsServiceClient(_channel);
        Orders = new OrdersService.OrdersServiceClient(_channel);
        MarketData = new MarketDataService.MarketDataServiceClient(_channel);
        Assets = new AssetsService.AssetsServiceClient(_channel);
    }

    public FinamOptions Options => _options;
    public AccountsService.AccountsServiceClient Accounts { get; }
    public OrdersService.OrdersServiceClient Orders { get; }
    public MarketDataService.MarketDataServiceClient MarketData { get; }
    public AssetsService.AssetsServiceClient Assets { get; }

    public CallOptions Call(CancellationToken ct = default) =>
        new(deadline: DateTime.UtcNow + _options.RequestTimeout, cancellationToken: ct);

    public async Task<TokenDetailsResponse> TokenDetailsAsync(CancellationToken ct = default) =>
        await _auth.TokenDetailsAsync(new TokenDetailsRequest { Token = await GetTokenAsync(ct) }, Call(ct));

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (IsTokenFresh())
            return _token!;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (IsTokenFresh())
                return _token!;

            var auth = await _auth.AuthAsync(new AuthRequest { Secret = _options.Secret }, Call(ct));
            _token = auth.Token;
            try
            {
                var details = await _auth.TokenDetailsAsync(new TokenDetailsRequest { Token = _token }, Call(ct));
                _expiresAt = details.ExpiresAt.ToDateTimeOffset();
            }
            catch (RpcException ex)
            {
                _expiresAt = _time.GetUtcNow() + FallbackLifetime;
                _log.LogWarning("Finam TokenDetails failed ({Status}), assuming token lifetime {Lifetime}",
                    ex.StatusCode, FallbackLifetime);
            }
            _log.LogInformation("Finam token obtained, expires at {ExpiresAt:u}", _expiresAt);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Без прокси — SocketsHttpHandler с UseProxy = false: иначе .NET подхватит системный прокси (VPN-клиент),
    /// а с ним балансировщик gRPC падает с "Unable to get subchannel". Через прокси — HttpClientHandler,
    /// с ним gRPC работает в простом режиме без балансировки.
    /// </summary>
    private static HttpMessageHandler CreateHandler(FinamOptions options) => options.UseProxy
        ? new HttpClientHandler()
        : new SocketsHttpHandler
        {
            UseProxy = false,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        };

    private bool IsTokenFresh() => _token is not null && _time.GetUtcNow() < _expiresAt - RefreshBefore;

    public void Dispose()
    {
        _channel.Dispose();
        _authChannel.Dispose();
        _tokenLock.Dispose();
    }
}
