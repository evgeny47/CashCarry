using System.Text;
using CashCarry.Core.Strategy;
using Microsoft.Extensions.Configuration;

namespace CashCarry.Engine.Tests;

public class PairsConfigTests
{
    private static IConfiguration Config(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private const string Futures = """
        "Futures": [ { "Symbol": "SRZ6@RTSX", "LastTradingDay": "2026-12-17", "InitialMargin": 5300 } ]
        """;

    [Fact]
    public void Pair_strategy_overrides_only_given_fields_of_common_section()
    {
        var pairs = PairsConfig.Load(Config($$"""
            {
              "Strategy": { "ExitMode": "Expiry", "MinEntryAnnualYield": 0.15, "MinHoldAnnualYield": 0.05, "MaxUnits": 2 },
              "Pairs": [
                { "StockSymbol": "SBER@MISX", {{Futures}}, "Strategy": { "MaxUnits": 5 } },
                { "StockSymbol": "GAZP@MISX", "Futures": [ { "Symbol": "GZZ6@RTSX", "LastTradingDay": "2026-12-17", "InitialMargin": 2500 } ] }
              ]
            }
            """));

        Assert.Equal(["SBER", "GAZP"], pairs.Select(p => p.Name));
        Assert.Equal(5, pairs[0].Strategy.MaxUnits);
        Assert.Equal(2, pairs[1].Strategy.MaxUnits);
        Assert.All(pairs, p => Assert.Equal(0.15m, p.Strategy.MinEntryAnnualYield));
        Assert.All(pairs, p => Assert.Equal(ExitMode.Expiry, p.Strategy.ExitMode));
        Assert.NotSame(pairs[0].Strategy, pairs[1].Strategy);
    }

    [Fact]
    public void Same_futures_in_two_pairs_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PairsConfig.Load(Config($$"""
            {
              "Pairs": [
                { "Name": "A", "StockSymbol": "SBER@MISX", {{Futures}} },
                { "Name": "B", "StockSymbol": "SBERP@MISX", {{Futures}} }
              ]
            }
            """)));

        Assert.Contains("SRZ6@RTSX", ex.Message);
    }

    [Fact]
    public void Invalid_pair_strategy_names_the_pair()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PairsConfig.Load(Config($$"""
            { "Pairs": [ { "StockSymbol": "SBER@MISX", {{Futures}}, "Strategy": { "MinHoldAnnualYield": 0.5 } } ] }
            """)));

        Assert.Contains("SBER", ex.Message);
    }

    [Theory]
    [InlineData("""{ "Instruments": { "StockSymbol": "SBER@MISX" } }""")]
    [InlineData("""{ "Dividends": [ { "LastBuyDate": "2027-07-17", "AmountPerShare": 30 } ] }""")]
    [InlineData("""{ "Pairs": [] }""")]
    public void Old_single_pair_format_or_empty_pairs_is_rejected(string json)
    {
        Assert.Throws<InvalidOperationException>(() => PairsConfig.Load(Config(json)));
    }
}
