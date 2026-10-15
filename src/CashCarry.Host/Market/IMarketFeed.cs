namespace CashCarry.Host.Market;

/// <summary>
/// Обновляет котировки у брокера перед каждым тиком робота.
/// Для Paper — симулятор или (позже) живые цены Финам; для Finam котировки приходят от брокера сами.
/// </summary>
public interface IMarketFeed
{
    Task UpdateAsync(DateTimeOffset now, CancellationToken ct);
}

public sealed class NoMarketFeed : IMarketFeed
{
    public Task UpdateAsync(DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
}
