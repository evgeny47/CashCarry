using System.Globalization;
using System.Text.Json;
using CashCarry.Core.Positions;
using CashCarry.Risk.Execution;
using Microsoft.Data.Sqlite;

namespace CashCarry.Host;

/// <summary>
/// Состояние робота в SQLite: позиции по парам, kill switch, последняя обработанная дата (для дивидендов),
/// журнал операций. Каждое изменение пишется сразу — после падения робот продолжает с того же места.
/// </summary>
public sealed class RobotStore
{
    /// <summary>Ключ позиции до появления нескольких пар — переносится в <see cref="MigrateLegacyPosition"/>.</summary>
    private const string LegacyPositionKey = "position";
    private const string PositionPrefix = "position:";
    private const string KillSwitchKey = "kill_switch";
    private const string LastSeenDateKey = "last_seen_date";

    private readonly string _connectionString;

    public RobotStore(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        Execute("""
            CREATE TABLE IF NOT EXISTS state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS journal (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                time TEXT NOT NULL,
                operation TEXT NOT NULL,
                status TEXT NOT NULL,
                units INTEGER NOT NULL,
                unwind_pnl TEXT NOT NULL,
                message TEXT NOT NULL,
                position TEXT);
            """);
        if (!HasColumn("journal", "pair"))
            Execute("ALTER TABLE journal ADD COLUMN pair TEXT");
    }

    public PairPosition? LoadPosition(string pair) => Get<PairPosition>(PositionPrefix + pair);
    public void SavePosition(string pair, PairPosition? position) => Set(PositionPrefix + pair, position);

    /// <summary>Все сохранённые позиции по имени пары.</summary>
    public IReadOnlyDictionary<string, PairPosition> LoadPositions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM state WHERE key LIKE $prefix";
        command.Parameters.AddWithValue("$prefix", PositionPrefix + "%");
        var positions = new Dictionary<string, PairPosition>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            positions[reader.GetString(0)[PositionPrefix.Length..]] = JsonSerializer.Deserialize<PairPosition>(reader.GetString(1))!;
        return positions;
    }

    /// <summary>
    /// База одиночной пары хранила позицию без имени. Переносим её к паре, которой принадлежит контракт позиции.
    /// Возвращает имя пары или null, если старой позиции не было.
    /// </summary>
    public string? MigrateLegacyPosition(Func<PairPosition, string?> ownerOf)
    {
        if (Get<PairPosition>(LegacyPositionKey) is not { } legacy)
            return null;
        string owner = ownerOf(legacy) ?? throw new InvalidOperationException(
            $"в базе позиция по {legacy.Futures.Symbol}, но этого контракта нет ни в одной паре Pairs.");
        if (LoadPosition(owner) is not null)
            throw new InvalidOperationException($"в базе две позиции пары {owner}: старого и нового формата.");
        SavePosition(owner, legacy);
        Set<PairPosition>(LegacyPositionKey, null);
        return owner;
    }

    public string? LoadKillSwitch() => Get<string>(KillSwitchKey);
    public void SaveKillSwitch(string? reason) => Set(KillSwitchKey, reason);

    public DateOnly? LoadLastSeenDate() => Get<DateOnly?>(LastSeenDateKey);
    public void SaveLastSeenDate(DateOnly date) => Set(LastSeenDateKey, date);

    public void Journal(DateTimeOffset time, string pair, string operation, ExecutionResult result)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO journal (time, pair, operation, status, units, unwind_pnl, message, position)
            VALUES ($time, $pair, $operation, $status, $units, $unwind, $message, $position)
            """;
        command.Parameters.AddWithValue("$time", time.ToString("O"));
        command.Parameters.AddWithValue("$pair", pair);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$status", result.Status.ToString());
        command.Parameters.AddWithValue("$units", result.Units);
        command.Parameters.AddWithValue("$unwind", result.UnwindPnl.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$message", result.Message);
        command.Parameters.AddWithValue("$position",
            result.Position is null ? DBNull.Value : JsonSerializer.Serialize(result.Position));
        command.ExecuteNonQuery();
    }

    private T? Get<T>(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM state WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) : default;
    }

    private void Set<T>(string key, T? value)
    {
        if (value is null)
        {
            Execute("DELETE FROM state WHERE key = $key", ("$key", key));
            return;
        }
        Execute("INSERT INTO state (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", JsonSerializer.Serialize(value)));
    }

    private bool HasColumn(string table, string column)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
        command.Parameters.AddWithValue("$column", column);
        return (long)command.ExecuteScalar()! > 0;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
