namespace CashCarry.Risk;

/// <summary>
/// Аварийный стоп. После срабатывания новые входы и переносы запрещены, закрытие разрешено.
/// Сбрасывается только вручную. Хранение состояния между перезапусками — забота Host.
/// </summary>
public interface IKillSwitch
{
    bool IsTripped { get; }
    string? Reason { get; }
    void Trip(string reason);
    void Reset();
    event Action<string>? Tripped;
}

public sealed class KillSwitch : IKillSwitch
{
    private readonly object _sync = new();
    private string? _reason;

    public bool IsTripped { get { lock (_sync) return _reason is not null; } }
    public string? Reason { get { lock (_sync) return _reason; } }

    public event Action<string>? Tripped;

    /// <summary>Повторный вызов не меняет первую причину и не шлёт событие повторно.</summary>
    public void Trip(string reason)
    {
        lock (_sync)
        {
            if (_reason is not null)
                return;
            _reason = reason;
        }
        Tripped?.Invoke(reason);
    }

    public void Reset()
    {
        lock (_sync) _reason = null;
    }
}
