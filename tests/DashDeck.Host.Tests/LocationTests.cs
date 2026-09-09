using DashDeck.Abstractions;
using DashDeck.Host.Location;
using DashDeck.Host.Sensors;

namespace DashDeck.Host.Tests;

/// <summary>
/// The phone-GPS feed (ADR-0027): the NMEA parser (the part with all the edge cases) and the
/// moving-heading rule. Both are pure enough to test without a phone or the shell (F7).
/// </summary>
public sealed class LocationTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    // --- NMEA parsing ---

    [Fact]
    public void A_valid_rmc_yields_position_speed_and_course()
    {
        Assert.True(NmeaParser.TryParse(
            "$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,,", At, out var fix));

        Assert.Equal(48.1173, fix.Latitude, 3);
        Assert.Equal(11.5167, fix.Longitude, 3);
        Assert.Equal(22.4 * 1.852, fix.GroundSpeedKmh, 2);   // knots → km/h
        Assert.Equal(84.4, fix.CourseDegrees, 2);
        Assert.Equal(SignalQuality.Live, fix.Quality);
        Assert.True(fix.HasFix);
    }

    [Fact]
    public void Southern_and_western_hemispheres_are_negative()
    {
        Assert.True(NmeaParser.TryParse(
            "$GPRMC,123519,A,4807.038,S,01131.000,W,000.0,000.0,230394,,", At, out var fix));

        Assert.True(fix.Latitude < 0);
        Assert.True(fix.Longitude < 0);
    }

    [Fact]
    public void A_void_rmc_parses_but_reports_no_fix()
    {
        Assert.True(NmeaParser.TryParse(
            "$GPRMC,123519,V,4807.038,N,01131.000,E,022.4,084.4,230394,,", At, out var fix));

        Assert.Equal(SignalQuality.Unavailable, fix.Quality);
        Assert.False(fix.HasFix);
    }

    [Fact]
    public void A_stationary_rmc_has_a_fix_but_no_course()
    {
        Assert.True(NmeaParser.TryParse(
            "$GPRMC,123519,A,4807.038,N,01131.000,E,,,230394,,", At, out var fix));

        Assert.True(fix.HasFix);
        Assert.True(double.IsNaN(fix.CourseDegrees));
    }

    [Theory]
    [InlineData("GP")]
    [InlineData("GN")]
    [InlineData("GL")]
    public void Any_talker_id_is_accepted(string talker)
    {
        Assert.True(NmeaParser.TryParse(
            $"${talker}RMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,,", At, out var fix));
        Assert.True(fix.HasFix);
    }

    [Fact]
    public void A_gga_yields_position_and_fix_from_its_quality_field()
    {
        Assert.True(NmeaParser.TryParse(
            "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,", At, out var withFix));
        Assert.True(withFix.HasFix);
        Assert.Equal(48.1173, withFix.Latitude, 3);

        Assert.True(NmeaParser.TryParse(
            "$GPGGA,123519,4807.038,N,01131.000,E,0,00,,,M,,M,,", At, out var noFix));
        Assert.False(noFix.HasFix);
    }

    [Theory]
    [InlineData("$GPGSV,3,1,11,03,03,111,00")]   // not a fix sentence
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("$")]
    public void Junk_and_other_sentences_do_not_parse(string line) =>
        Assert.False(NmeaParser.TryParse(line, At, out _));

    [Fact]
    public void Checksums_are_honoured()
    {
        var body = "GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,,";

        Assert.True(NmeaParser.TryParse(WithChecksum(body), At, out _));   // correct
        Assert.False(NmeaParser.TryParse("$" + body + "*00", At, out _));  // tampered
    }

    private static string WithChecksum(string body)
    {
        byte sum = 0;

        foreach (var c in body)
        {
            sum ^= (byte)c;
        }

        return $"${body}*{sum:X2}";
    }

    // --- The moving-heading rule ---

    private sealed class FakeSource(LocationFix? fix) : ILocationSource
    {
        public string Name => "fake";

        public LocationFix? Latest => fix;

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Clock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static SensorDefinition Gps(SensorChannel channel, double min, double max) =>
        new() { Id = channel.ToString(), Name = channel.ToString(), Source = SensorSource.Gps, Channel = channel, Min = min, Max = max };

    [Fact]
    public void A_moving_fix_gives_a_gps_heading_and_speed()
    {
        var now = DateTimeOffset.UnixEpoch;
        var fix = new LocationFix(49.0, -97.0, 60, 90, SignalQuality.Live, now);
        var phone = new PhoneLocationSensors(new FakeSource(fix), new Clock(now));

        Assert.True(phone.TryReadHeading(out var heading));
        Assert.Equal(90, heading.Value, 1);
        Assert.Equal("PHONE · GPS", heading.Source);

        var speed = phone.Read(Gps(SensorChannel.GroundSpeed, 0, 320), new MountReference());
        Assert.Equal(60, speed.Value, 1);
        Assert.True(speed.IsUsable);
    }

    [Fact]
    public void A_stopped_fix_declines_heading_so_the_magnetometer_can_answer()
    {
        var now = DateTimeOffset.UnixEpoch;
        var fix = new LocationFix(49.0, -97.0, 2, 173, SignalQuality.Live, now);   // below the threshold
        var phone = new PhoneLocationSensors(new FakeSource(fix), new Clock(now));

        Assert.False(phone.TryReadHeading(out _));
    }

    [Fact]
    public void No_fix_reads_as_no_fix_rather_than_throwing()
    {
        var phone = new PhoneLocationSensors(new FakeSource(null), new Clock(DateTimeOffset.UnixEpoch));

        Assert.False(phone.TryReadHeading(out _));

        var lat = phone.Read(Gps(SensorChannel.Latitude, -90, 90), new MountReference());
        Assert.False(lat.IsUsable);
        Assert.Equal("PHONE · NO FIX", lat.Source);
    }

    [Fact]
    public void A_fix_that_stops_arriving_goes_stale()
    {
        var taken = DateTimeOffset.UnixEpoch;
        var now = taken + TimeSpan.FromSeconds(30);
        var fix = new LocationFix(49.0, -97.0, 60, 90, SignalQuality.Live, taken);
        var phone = new PhoneLocationSensors(new FakeSource(fix), new Clock(now));

        var speed = phone.Read(Gps(SensorChannel.GroundSpeed, 0, 320), new MountReference());
        Assert.Equal(SignalQuality.Stale, speed.Quality);
        Assert.False(phone.TryReadHeading(out _));   // a stale course is not a heading
    }
}
