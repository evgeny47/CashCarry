using System.Text.Json;
using CashCarry.Core.Instruments;
using CashCarry.Core.Positions;
using Microsoft.Data.Sqlite;

namespace CashCarry.Host.Tests;

public sealed class RobotStoreTests : IDisposable
{
    private static readonly FuturesContract Dec26 = new("SRZ6@RTSX", new DateOnly(2026, 12, 17));

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cashcarry-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    /// <summary>База в формате одиночной пары: позиция под ключом "position", журнал без колонки pair.</summary>
    private void CreateLegacyDatabase(PairPosition position)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE journal (
                id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, operation TEXT NOT NULL, status TEXT NOT NULL,
                units INTEGER NOT NULL, unwind_pnl TEXT NOT NULL, message TEXT NOT NULL, position TEXT);
            INSERT INTO state (key, value) VALUES ('position', $position);
            """;
        command.Parameters.AddWithValue("$position", JsonSerializer.Serialize(position));
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Legacy_position_moves_to_pair_that_owns_its_contract()
    {
        var position = PairPosition.Open(Dec26, 2, 300m, 31000m, 30m);
        CreateLegacyDatabase(position);
        var store = new RobotStore(_dbPath);

        string? owner = store.MigrateLegacyPosition(p => p.Futures.Symbol == Dec26.Symbol ? "SBER" : null);

        Assert.Equal("SBER", owner);
        Assert.Equal(position, store.LoadPosition("SBER"));
        Assert.Equal(["SBER"], store.LoadPositions().Keys);
        Assert.Null(store.MigrateLegacyPosition(_ => "SBER")); // повторный запуск — переносить нечего
    }

    [Fact]
    public void Legacy_position_without_owner_is_an_error()
    {
        CreateLegacyDatabase(PairPosition.Open(Dec26, 1, 300m, 31000m, 0m));
        var store = new RobotStore(_dbPath);

        Assert.Throws<InvalidOperationException>(() => store.MigrateLegacyPosition(_ => null));
    }

    [Fact]
    public void Journal_of_legacy_database_gets_pair_column()
    {
        CreateLegacyDatabase(PairPosition.Open(Dec26, 1, 300m, 31000m, 0m));
        var store = new RobotStore(_dbPath);

        store.Journal(DateTimeOffset.UnixEpoch, "SBER", "Open",
            new Risk.Execution.ExecutionResult(Risk.Execution.ExecutionStatus.Completed, null, 1, 0m, "ok"));

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pair FROM journal";
        Assert.Equal("SBER", command.ExecuteScalar());
    }
}
