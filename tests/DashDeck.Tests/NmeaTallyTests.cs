using DashDeck.Abstractions;
using DashDeck.Core.Location;

namespace DashDeck.Tests;

/// <summary>
/// What a stretch of a phone's NMEA feed adds up to (ADR-0027/0028), and how it reads on a
/// screen. The point of the tally is that "no GPS" is five different problems with five
/// different fixes, so each verdict is pinned to the feed that should produce it.
/// </summary>
public sealed class NmeaTallyTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    // A full RMC: fix valid, Winnipeg, 34.1 knots, course 182.4°. No checksum — the parser
    // treats a sentence without one as unchecked rather than invalid, which keeps these legible.
    private const string Rmc = "$GPRMC,181248.00,A,4953.7060,N,09708.3040,W,34.1,182.4,151026,,,A";

    private const string Gga = "$GPGGA,181248.00,4953.7060,N,09708.3040,W,1,09,0.9,232.1,M,,M,,";

    // Valid sentence, no fix: the receiver is talking and has not found the sky.
    private const string VoidRmc = "$GPRMC,181248.00,V,,,,,,,151026,,,N";

    [Fact]
    public void Nothing_at_all_is_silent_rather_than_no_fix()
    {
        var tally = new NmeaTally();

        Assert.Equal(NmeaVerdict.Silent, tally.Verdict);
        Assert.Contains("NO DATA", tally.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_position_is_the_verdict_that_says_this_port_is_the_one()
    {
        var tally = new NmeaTally();
        tally.Add(Rmc, At);

        Assert.Equal(NmeaVerdict.Fix, tally.Verdict);
        Assert.Equal(1, tally.Rmc);
        Assert.Equal(1, tally.Parsed);
        Assert.Contains("49.89510", tally.Describe(), StringComparison.Ordinal);
        // 34.1 knots is 63.15 km/h, and the readout rounds to one decimal.
        Assert.Contains("63.2 km/h", tally.Describe(), StringComparison.Ordinal);
        Assert.Contains("course 182", tally.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_gga_is_counted_as_a_gga_and_still_gives_a_position()
    {
        var tally = new NmeaTally();
        tally.Add(Gga, At);

        Assert.Equal(1, tally.Gga);
        Assert.Equal(0, tally.Rmc);
        Assert.Equal(NmeaVerdict.Fix, tally.Verdict);

        // GGA carries no speed or course, and the readout says so rather than showing a zero.
        Assert.Contains("—— km/h", tally.Describe(), StringComparison.Ordinal);
        Assert.Contains("course ———", tally.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_talking_receiver_with_no_fix_is_its_own_verdict()
    {
        var tally = new NmeaTally();

        for (var i = 0; i < 5; i++)
        {
            tally.Add(VoidRmc, At);
        }

        // Five sentences parsed, so the link is not the problem — the sky is.
        Assert.Equal(NmeaVerdict.NoFixYet, tally.Verdict);
        Assert.Equal(5, tally.Parsed);
        Assert.Null(tally.LatestWithPosition);
        Assert.Contains("NO FIX", tally.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sentences_the_dash_does_not_read_are_named_rather_than_called_junk()
    {
        var tally = new NmeaTally();
        tally.Add("$GPGSV,3,1,11,05,62,170,45,13,55,291,42,15,34,055,38,18,22,310,33", At);
        tally.Add("$GPGSA,A,3,05,13,15,18,,,,,,,,,1.8,0.9,1.5", At);
        tally.Add("$GPVTG,182.4,T,,M,34.1,N,63.1,K,A", At);

        Assert.Equal(NmeaVerdict.WrongSentences, tally.Verdict);
        Assert.Equal(3, tally.OtherSentences);
        Assert.Equal(0, tally.Junk);
        Assert.Equal(["GSV", "GSA", "VTG"], tally.UnreadTypes);
    }

    [Fact]
    public void A_type_seen_fifty_times_is_named_once()
    {
        var tally = new NmeaTally();

        for (var i = 0; i < 50; i++)
        {
            tally.Add("$GPGSV,3,1,11,05,62,170,45,13,55,291,42,15,34,055,38,18,22,310,33", At);
        }

        Assert.Single(tally.UnreadTypes);
        Assert.Equal(50, tally.OtherSentences);
    }

    [Fact]
    public void Bytes_that_are_not_nmea_are_the_wrong_port_not_a_missing_fix()
    {
        var tally = new NmeaTally();
        tally.Add("ELM327 v1.5", At);
        tally.Add("41 0C 1A F8", At);
        tally.Add("", At);

        Assert.Equal(NmeaVerdict.NotNmea, tally.Verdict);
        Assert.Equal(3, tally.Junk);
        Assert.Equal(0, tally.OtherSentences);
    }

    [Theory]
    // A deliberately wrong checksum, and a truncated sentence. Both are RMC or GGA that will not
    // decode, which is a different problem from a sentence type the dash simply does not read.
    [InlineData("$GPRMC,181248.00,A,4953.7060,N,09708.3040,W,34.1,182.4,151026,,,A*00")]
    [InlineData("$GPRMC,181248.00,A")]
    [InlineData("$GPGGA,181248.00,4953")]
    public void An_rmc_or_gga_that_will_not_decode_is_counted_apart(string line)
    {
        var tally = new NmeaTally();
        tally.Add(line, At);

        Assert.Equal(1, tally.Rejected);
        Assert.Equal(0, tally.Junk);
        Assert.Equal(0, tally.OtherSentences);
        Assert.Equal(0, tally.Parsed);
    }

    [Fact]
    public void A_stream_of_damaged_sentences_reads_as_garbled_rather_than_as_not_a_gps()
    {
        // Every line is a recognisable RMC with a checksum that does not match — what a link at
        // the edge of range looks like. "None of it NMEA" would send someone to the wrong port
        // when the port is right and the radio is not.
        var tally = new NmeaTally();

        for (var i = 0; i < 6; i++)
        {
            tally.Add(Rmc + "*00", At);
        }

        Assert.Equal(6, tally.Rejected);
        Assert.Contains("damaged", tally.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("none of it NMEA", tally.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fix_that_is_lost_keeps_the_last_known_position_and_says_the_fix_went()
    {
        var tally = new NmeaTally();
        tally.Add(Rmc, At);
        tally.Add(VoidRmc, At.AddSeconds(1));

        // Latest is the loss — that is what the dash must act on. LatestWithPosition is still
        // the last real one, which is what a person wants to see while working out why.
        Assert.False(tally.Latest!.Value.HasFix);
        Assert.True(tally.LatestWithPosition!.Value.HasFix);
        Assert.Equal(NmeaVerdict.Fix, tally.Verdict);
    }

    [Fact]
    public void Every_line_is_counted_even_the_ones_that_mean_nothing()
    {
        var tally = new NmeaTally();
        tally.Add(Rmc, At);
        tally.Add("$GPGSV,1,1,00", At);
        tally.Add("rubbish", At);

        Assert.Equal(3, tally.Lines);
    }
}

/// <summary>
/// The one-line readout the console probe and Settings share, so the dash says the same thing
/// in both places.
/// </summary>
public sealed class LocationStatusTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static LocationFix Fix(
        SignalQuality quality = SignalQuality.Live,
        double speed = 63.1,
        double course = 182.4) =>
        new(49.8951, -97.1384, speed, course, quality, At);

    [Fact]
    public void Quality_leads_because_a_position_without_it_is_what_this_dash_never_shows()
    {
        var text = LocationStatus.Describe(Fix());

        Assert.StartsWith("FIX —", text, StringComparison.Ordinal);
        Assert.Contains("49.89510, -97.13840", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_fix_is_labelled_stale_rather_than_shown_as_live()
    {
        Assert.StartsWith("STALE", LocationStatus.Describe(Fix(SignalQuality.Stale)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_quality_with_no_position_says_so_instead_of_printing_zeroes()
    {
        // An Unavailable fix carries whatever numbers were in the sentence, which may be anything.
        Assert.Equal("NO FIX — no position", LocationStatus.Describe(Fix(SignalQuality.Unavailable)));
    }

    [Fact]
    public void Absent_speed_and_course_read_as_absent()
    {
        var text = LocationStatus.Describe(Fix(speed: double.NaN, course: double.NaN));

        Assert.Contains("—— km/h", text, StringComparison.Ordinal);
        Assert.Contains("course ———", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_synthetic_source_is_labelled_simulated_so_a_desk_demo_is_never_mistaken_for_a_drive()
    {
        Assert.StartsWith("SIMULATED", LocationStatus.Describe(Fix(SignalQuality.Simulated)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(1, "just now")]
    [InlineData(4, "4 s ago")]
    [InlineData(59, "59 s ago")]
    [InlineData(120, "2 min ago")]
    public void Age_is_how_a_glance_reads_it(int seconds, string expected)
    {
        Assert.Equal(expected, LocationStatus.Age(Fix(), At.AddSeconds(seconds)));
    }

    [Fact]
    public void A_phone_clock_that_runs_ahead_reads_just_now_rather_than_the_future()
    {
        // The fix is stamped with the phone's time, so a phone a few seconds ahead of the tablet
        // would otherwise produce a negative age — and "-3 s ago" reads like a fault that is not.
        Assert.Equal("just now", LocationStatus.Age(Fix(), At.AddSeconds(-3)));
    }
}
