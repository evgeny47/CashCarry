using CashCarry.Risk;
using Microsoft.Extensions.Logging;

namespace CashCarry.Backtest;

/// <summary>
/// Логи робота во время прогона: в консоль — со временем тестера (а не системным), от уровня consoleLevel.
/// Предупреждения и ошибки считаются по шаблону сообщения, даже если в консоль не выводятся, — для итогового отчёта.
/// </summary>
public sealed class BacktestLog(BacktestClock clock, LogLevel consoleLevel, TextWriter output) : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly LogLevel _consoleLevel = consoleLevel;

    public Dictionary<string, int> Problems { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private void Write<TState>(LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_sync)
        {
            if (level >= LogLevel.Warning)
            {
                string template = state is IReadOnlyList<KeyValuePair<string, object?>> values &&
                                  values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value is string t ? t : formatter(state, exception);
                string key = $"{Short(level)} {template}";
                Problems[key] = Problems.GetValueOrDefault(key) + 1;
            }
            if (level < _consoleLevel)
                return;
            output.WriteLine($"{TradingSchedule.ToMoscow(clock.Now):dd.MM HH:mm} [{Short(level)}] {formatter(state, exception)}" +
                             (exception is null ? "" : $" — {exception.Message}"));
        }
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "CRT",
    };

    private sealed class Logger(BacktestLog owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning || logLevel >= owner._consoleLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                owner.Write(logLevel, state, exception, formatter);
        }
    }
}
