namespace CashCarry.Broker.Finam;

/// <summary>Секция "Finam". Secret — только из user-secrets или переменной окружения Finam__Secret.</summary>
public sealed class FinamOptions
{
    public string Endpoint { get; set; } = "https://api.finam.ru:443";
    /// <summary>Номер торгового счёта (account_id в Trade API).</summary>
    public string AccountId { get; set; } = "";
    /// <summary>Секретный ключ Trade API. Не хранить в appsettings.json.</summary>
    public string Secret { get; set; } = "";
    /// <summary>Разрешить реальные заявки. false — робот только пишет в лог, что сделал бы (сухой прогон).</summary>
    public bool TradingEnabled { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Ходить через системный прокси (HTTPS_PROXY / настройки Windows). По умолчанию — напрямую.</summary>
    public bool UseProxy { get; set; }

    public void Validate(bool requireAccount = true)
    {
        if (string.IsNullOrWhiteSpace(Secret))
            throw new InvalidOperationException(
                "Не задан секрет Finam Trade API. Выполните в папке src/CashCarry.Host:\n" +
                "  dotnet user-secrets set \"Finam:Secret\" \"<ваш секрет>\"\n" +
                "или задайте переменную окружения Finam__Secret.");
        if (requireAccount && string.IsNullOrWhiteSpace(AccountId))
            throw new InvalidOperationException(
                "Не задан Finam:AccountId. Узнать номер счёта: dotnet run --project src/CashCarry.Host -- --finam-check");
    }
}
