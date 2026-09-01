using DashDeck.Abstractions;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// The parser is the reason the synthetic truck speaks ELM ASCII rather than handing back
/// decoded values: real adapter output is messy, and these are the shapes it comes in.
/// </summary>
public class ElmResponseParserTests
{
    private static readonly PidRequest Speed = new(0x01, 0x0D, CanBus.Hs);
    private static readonly PidRequest Rpm = new(0x01, 0x0C, CanBus.Hs);
    private static readonly DateTimeOffset At = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_unspaced_response()
    {
        var result = ElmResponseParser.Parse(Speed, "410D3C\r\r>", At);

        Assert.True(result.IsSuccess);
        Assert.Equal([0x3C], result.Data);
    }

    [Fact]
    public void Parses_spaced_response()
    {
        var result = ElmResponseParser.Parse(Rpm, "41 0C 1A F8\r\r>", At);

        Assert.True(result.IsSuccess);
        Assert.Equal([0x1A, 0xF8], result.Data);
    }

    [Fact]
    public void Ignores_echoed_command_and_searching_line()
    {
        var result = ElmResponseParser.Parse(Speed, "010D\rSEARCHING...\r410D50\r\r>", At);

        Assert.True(result.IsSuccess);
        Assert.Equal([0x50], result.Data);
    }

    [Theory]
    [InlineData("NO DATA\r\r>", PidFailure.NoData)]
    [InlineData("UNABLE TO CONNECT\r\r>", PidFailure.NoData)]
    [InlineData("BUS ERROR\r\r>", PidFailure.BusError)]
    [InlineData("CAN ERROR\r\r>", PidFailure.BusError)]
    [InlineData("", PidFailure.Timeout)]
    public void Classifies_adapter_errors(string raw, PidFailure expected)
    {
        Assert.Equal(expected, ElmResponseParser.Parse(Speed, raw, At).Failure);
    }

    [Fact]
    public void Rejects_response_for_a_different_pid()
    {
        // A stale response left in the buffer must not be accepted as the answer to a
        // question it was not asked. Misattributing a value puts a wrong number on the dash.
        var result = ElmResponseParser.Parse(Speed, "410C1AF8\r\r>", At);

        Assert.Equal(PidFailure.Malformed, result.Failure);
    }

    [Fact]
    public void Rejects_unparseable_line_rather_than_skipping_it()
    {
        // Silently skipping a corrupt line would turn partial garbage into a plausible
        // reading. Failing is the safe answer.
        var result = ElmResponseParser.Parse(Speed, "410D3C\rZZQQ\r\r>", At);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Strips_a_can_header_when_headers_are_enabled()
    {
        var result = ElmResponseParser.Parse(Speed, "7E8 41 0D 3C\r\r>", At);

        Assert.True(result.IsSuccess);
        Assert.Equal([0x3C], result.Data);
    }

    [Fact]
    public void Handles_multiline_iso_tp_frames()
    {
        var request = new PidRequest(0x01, 0x00, CanBus.Hs);
        var result = ElmResponseParser.Parse(request, "0: 41 00 BE 3E\r1: B8 11 00 00\r\r>", At);

        Assert.True(result.IsSuccess);
        Assert.Equal([0xBE, 0x3E, 0xB8, 0x11, 0x00, 0x00], result.Data);
    }
}
