using System.Globalization;
using CashCarry.Core.Market;
using Microsoft.Data.Sqlite;

namespace CashCarry.Backtest;

/// <summary>
/// Свечи в SQLite: (символ, таймфрейм, начало свечи) → OHLCV. Повторная загрузка того же периода перезаписывает
/// свечи, поэтому историю можно докачивать по кусочкам и копить дольше, чем её хранит Финам.
/// Цены хранятся текстом, чтобы decimal не терял точность.
/// </summary>
public sealed class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS candles (
                symbol TEXT NOT NULL,
                timeframe TEXT NOT NULL,
                time INTEGER NOT NULL,
                open TEXT NOT NULL,
                high TEXT NOT NULL,
                low TEXT NOT NULL,
                close TEXT NOT NULL,
                volume TEXT NOT NULL,
                PRIMARY KEY (symbol, timeframe, time));
            """;
        command.ExecuteNonQuery();
    }

    public void Save(string symbol, Timeframe timeframe, IEnumerable<Candle> candles)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO candles (symbol, timeframe, time, open, high, low, close, volume)
            VALUES ($symbol, $timeframe, $time, $open, $high, $low, $close, $volume)
            """;
        var parameters = new[] { "$symbol", "$timeframe", "$time", "$open", "$high", "$low", "$close", "$volume" }
            .ToDictionary(n => n, n => command.Parameters.Add(n, SqliteType.Text));
        parameters["$symbol"].Value = symbol;
        parameters["$timeframe"].Value = timeframe.ToString();
        foreach (var c in candles)
        {
            parameters["$time"].Value = c.Time.ToUnixTimeSeconds();
            parameters["$open"].Value = Text(c.Open);
            parameters["$high"].Value = Text(c.High);
            parameters["$low"].Value = Text(c.Low);
            parameters["$close"].Value = Text(c.Close);
            parameters["$volume"].Value = Text(c.Volume);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<Candle> Load(string symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT time, open, high, low, close, volume FROM candles
            WHERE symbol = $symbol AND timeframe = $timeframe AND time >= $from AND time < $to
            ORDER BY time
            """;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$timeframe", timeframe.ToString());
        command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());

        var candles = new List<Candle>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            candles.Add(new Candle(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), Number(reader.GetString(1)),
                Number(reader.GetString(2)), Number(reader.GetString(3)), Number(reader.GetString(4)), Number(reader.GetString(5))));
        return candles;
    }

    /// <summary>Сколько свечей есть по символу и за какой период — для сводки после загрузки.</summary>
    public (int Count, DateTimeOffset? First, DateTimeOffset? Last) Coverage(string symbol, Timeframe timeframe)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MIN(time), MAX(time) FROM candles WHERE symbol = $symbol AND timeframe = $timeframe";
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$timeframe", timeframe.ToString());
        using var reader = command.ExecuteReader();
        reader.Read();
        int count = reader.GetInt32(0);
        return count == 0
            ? (0, null, null)
            : (count, DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)));
    }

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal Number(string value) => decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
