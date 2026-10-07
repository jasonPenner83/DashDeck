using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Discovery.Matching;
using DashDeck.Vehicle.Tap;

namespace DashDeck.Tests;

/// <summary>
/// The ID matcher's engine (ADR-0050): reading FORScan's traffic, fitting scalings to what
/// FORScan showed, and matching a PID log.
/// </summary>
public sealed class IdMatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 47, 11, TimeSpan.FromHours(-5));

    /// <summary>Feeds a scripted conversation, a line every 10 ms, and keeps what was observed.</summary>
    private sealed class Conversation
    {
        public TrafficReader Reader { get; } = new();

        public List<Observation> Seen { get; } = [];

        public DateTimeOffset Now { get; private set; } = T0;

        public Conversation() => Reader.Observed += Seen.Add;

        public Conversation Send(string command)
        {
            Reader.Feed(new TapLine(Tick(), TapDirection.ToAdapter, command));
            return this;
        }

        /// <summary>The adapter's answer lines, then its prompt.</summary>
        public Conversation Reply(params string[] lines)
        {
            foreach (var line in lines)
            {
                Reader.Feed(new TapLine(Tick(), TapDirection.FromAdapter, line));
            }

            Reader.Feed(new TapLine(Tick(), TapDirection.FromAdapter, "", Prompt: true));
            return this;
        }

        public Conversation Ask(string command, params string[] reply) => Send(command).Reply(reply);

        private DateTimeOffset Tick() => Now = Now.AddMilliseconds(10);
    }

    // ── Reading the traffic ───────────────────────────────────────────────────

    [Fact]
    public void A_mode_22_request_to_a_module_is_named_by_the_header_set_before_it()
    {
        // What FORScan sent for transmission temperature on 2026-10-04.
        var c = new Conversation()
            .Ask("ATTP6", "OK")
            .Ask("ATSH0007E0", "OK")
            .Ask("STCAFCP7E0,7E8", "OK")
            .Ask("221E1C1", "621E1C00F3");

        var seen = Assert.Single(c.Seen);
        Assert.Equal(new IdentifierKey(CanBus.Hs, 0x7E0, 0x22, 0x1E1C), seen.Key);
        Assert.Equal(new byte[] { 0x00, 0xF3 }, seen.Payload);
        Assert.Equal("7E0 22 1E1C (HS)", seen.Key.ToString());
    }

    [Fact]
    public void Pins_3_11_and_the_stn_request_command_are_followed()
    {
        var c = new Conversation()
            .Ask("STP53", "OK")
            .Ask("STPBR500000", "OK")
            .Ask("ATSH000720", "OK")
            .Ask("STPXd:22F113,r:1", "62F1134A4C33");

        var seen = Assert.Single(c.Seen);
        Assert.Equal(new IdentifierKey(CanBus.Ms, 0x720, 0x22, 0xF113), seen.Key);
        Assert.Equal("3/11", seen.Key.BusText);
        Assert.Equal("720", seen.Key.ModuleText);
    }

    [Fact]
    public void A_broadcast_mode_01_request_with_a_count_is_the_standard_pid()
    {
        var c = new Conversation()
            .Ask("ATSH0007DF", "OK")
            .Ask("ATCRA7E8", "OK")
            .Ask("01051", "410559");

        var seen = Assert.Single(c.Seen);
        Assert.Equal(new IdentifierKey(CanBus.Hs, IdentifierKey.Broadcast, 0x01, 0x05), seen.Key);
        Assert.Null(seen.Key.ModuleText);
        Assert.Equal(new byte[] { 0x59 }, seen.Payload);
    }

    [Theory]
    [InlineData("7F2231")]
    [InlineData("NO DATA")]
    [InlineData("CAN ERROR")]
    [InlineData("621E1D00F3")]   // answers a different PID
    [InlineData("41 0C 1A F8")] // a different mode
    public void Refusals_silence_and_mismatched_answers_observe_nothing(string reply)
    {
        var c = new Conversation().Ask("ATSH0007E0", "OK").Ask("221E1C1", reply);

        Assert.Empty(c.Seen);
    }

    [Fact]
    public void A_busy_reply_waits_for_the_real_answer()
    {
        var c = new Conversation().Ask("ATSH0007E0", "OK").Ask("221E1C", "7F2278", "621E1C00F5");

        Assert.Equal(new byte[] { 0x00, 0xF5 }, Assert.Single(c.Seen).Payload);
    }

    [Fact]
    public void A_multi_frame_answer_is_put_back_together()
    {
        var c = new Conversation().Ask("ATSH0007E0", "OK").Ask("22DE00", "00B", "0:62DE00010203", "1:0405060708");

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Assert.Single(c.Seen).Payload);
    }

    [Fact]
    public void Headers_on_are_stripped()
    {
        var c = new Conversation().Ask("ATH1", "OK").Ask("010C", "7E804410C1AF8");

        Assert.Equal(new byte[] { 0x1A, 0xF8 }, Assert.Single(c.Seen).Payload);
    }

    [Fact]
    public void Dtc_reads_voltage_and_settings_pass_by()
    {
        var c = new Conversation()
            .Ask("ATRV", "14.1V")
            .Ask("STPXd:19028F,r:1", "5902FB")
            .Ask("ATPPS", "00:FF F  01:FF F");

        Assert.Empty(c.Seen);
    }

    [Fact]
    public void A_reset_returns_to_the_broadcast_on_the_main_bus()
    {
        var c = new Conversation()
            .Ask("STP53", "OK")
            .Ask("ATSH000720", "OK")
            .Ask("ATZ", "ELM327 v1.4b")
            .Ask("010C", "410C1AF8");

        Assert.Equal(new IdentifierKey(CanBus.Hs, IdentifierKey.Broadcast, 0x01, 0x0C), Assert.Single(c.Seen).Key);
    }

    // ── A saved tap log reads back into the same traffic ──────────────────────

    [Fact]
    public void A_saved_tap_log_reads_back_and_matches_like_live_traffic()
    {
        // Stamped in this machine's time zone, as the tap writes them, under the header it writes.
        var clock = new SettableClock(new DateTimeOffset(2026, 10, 4, 12, 46, 0, TimeSpan.Zero));
        var started = clock.UtcNow.AddSeconds(-20).ToLocalTime();
        var lines = new List<string> { $"# SerialTap  COM7 @ 115200 baud  listening on 127.0.0.1:35000  started {started:yyyy-MM-dd HH:mm:ss zzz}" };
        var recorder = new TapRecorder(clock, lines.Add);

        recorder.Note("connected: 127.0.0.1:51024");
        recorder.Add(TapDirection.ToAdapter, "ATSH0007E0\r"u8);
        recorder.Add(TapDirection.FromAdapter, "OK\r\r>"u8);
        recorder.Add(TapDirection.ToAdapter, "221E1C1\r"u8);
        recorder.Add(TapDirection.FromAdapter, "621E1C00F3\r\r>"u8);

        var reader = new TrafficReader();
        var seen = new List<Observation>();
        reader.Observed += seen.Add;

        foreach (var line in TapLogFile.Read(string.Join("\r\n", lines)))
        {
            reader.Feed(line);
        }

        var observation = Assert.Single(seen);
        Assert.Equal(new IdentifierKey(CanBus.Hs, 0x7E0, 0x22, 0x1E1C), observation.Key);
        Assert.Equal(clock.UtcNow, observation.At);
    }

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    // ── Fitting scalings to what FORScan showed ───────────────────────────────

    private static Sample Shown(string typed, ShownUnit unit, params byte[] payload)
    {
        var number = double.Parse(typed, System.Globalization.CultureInfo.InvariantCulture);
        var (value, factor, _) = UnitConversion.ToMetric(number, unit);
        return new Sample(T0, payload, value, UnitConversion.Tolerance(typed, number) * factor);
    }

    [Fact]
    public void Coolant_typed_twice_is_one_byte_less_forty()
    {
        var candidates = ScalingFitter.FromSamples([
            Shown("49", ShownUnit.Celsius, 0x59),
            Shown("50", ShownUnit.Celsius, 0x5A),
        ]);

        var best = candidates[0];
        Assert.Equal(new RawWindow(0, 1, false), best.Window);
        Assert.Equal(1, best.Scale);
        Assert.Equal(-40, best.Offset);
        Assert.Equal("A − 40", best.Formula);
    }

    [Fact]
    public void Transmission_temperature_typed_in_fahrenheit_is_two_bytes_over_sixteen()
    {
        // The 2026-10-04 capture: 00F3 to 00FB, shown as about 59–60 °F if ÷16 is right.
        var candidates = ScalingFitter.FromSamples([
            Shown("59.3", ShownUnit.Fahrenheit, 0x00, 0xF3),
            Shown("59.9", ShownUnit.Fahrenheit, 0x00, 0xF8),
            Shown("60.2", ShownUnit.Fahrenheit, 0x00, 0xFB),
        ]);

        var best = candidates[0];
        Assert.Equal(2, best.Window.Length);
        Assert.Equal(0.0625, best.Scale);
        Assert.Equal(0, best.Offset);
        Assert.Equal(3, best.DistinctRaw);
        Assert.Equal("(A·256+B) ÷ 16", best.Formula);
    }

    [Fact]
    public void A_moving_value_puts_the_byte_that_moves_with_it_on_top()
    {
        // The first byte never changes; the second carries the value.
        var candidates = ScalingFitter.FromSamples([
            Shown("90", ShownUnit.Kpa, 0x32, 0x5A),
            Shown("120", ShownUnit.Kpa, 0x32, 0x78),
            Shown("60", ShownUnit.Kpa, 0x32, 0x3C),
        ]);

        Assert.Equal(new RawWindow(1, 1, false), candidates[0].Window);
        Assert.Equal(3, candidates[0].DistinctRaw);
        Assert.False(candidates[0].Fitted);
    }

    [Fact]
    public void An_unusual_scaling_is_fitted_from_the_samples()
    {
        // 0.3 per count plus 7: no usual scale and offset gives it.
        var candidates = ScalingFitter.FromSamples([
            Shown("37.0", ShownUnit.None, 100),
            Shown("67.0", ShownUnit.None, 200),
            Shown("52.0", ShownUnit.None, 150),
        ]);

        var fitted = candidates.First(c => c.Fitted);
        Assert.Equal(0.3, fitted.Scale, 6);
        Assert.Equal(7, fitted.Offset, 3);
    }

    [Theory]
    [InlineData("59", 59, 0.51)]
    [InlineData("5.3", 5.3, 0.051)]
    [InlineData("59.3", 59.3, 0.2965)]  // half a percent: FORScan's rounding and the moment it was read
    [InlineData("2500", 2500, 12.5)]   // half a percent beats half a digit
    public void Tolerance_is_half_the_last_digit_or_half_a_percent(string typed, double value, double expected) =>
        Assert.Equal(expected, UnitConversion.Tolerance(typed, value), 3);

    [Fact]
    public void Fahrenheit_psi_and_mph_are_converted_to_the_catalogs_units()
    {
        Assert.Equal(15.17, UnitConversion.ToMetric(59.3, ShownUnit.Fahrenheit).Value, 2);
        Assert.Equal(275.79, UnitConversion.ToMetric(40, ShownUnit.Psi).Value, 2);
        Assert.Equal(96.56, UnitConversion.ToMetric(60, ShownUnit.Mph).Value, 2);
    }

    // ── FORScan's PID log ─────────────────────────────────────────────────────

    [Fact]
    public void A_comma_csv_with_milliseconds_and_units_in_headings_is_read()
    {
        var log = ForscanCsv.Parse("Time (ms),TFT (°F),RPM\n0,59.3,700\n250,59.9,\n500,60.2,720\n");

        Assert.False(log.ClockTimes);
        var tft = log.Columns.Single(c => c.Name == "TFT");
        Assert.Equal("°F", tft.Unit);
        Assert.Equal(TimeSpan.FromMilliseconds(500), tft.Points[2].Time);
        Assert.Equal(2, log.Columns.Single(c => c.Name == "RPM").Points.Count);   // the empty cell is skipped
    }

    [Fact]
    public void A_semicolon_csv_with_decimal_commas_and_seconds_is_read()
    {
        var log = ForscanCsv.Parse("time;ECT\n0,0;49\n0,5;49,5\n1,0;50\n");

        var ect = Assert.Single(log.Columns);
        Assert.Equal(49.5, ect.Points[1].Value);
        Assert.Equal(TimeSpan.FromSeconds(0.5), ect.Points[1].Time);
    }

    [Fact]
    public void Times_of_day_are_recognised_and_a_stopwatch_is_not_taken_for_one()
    {
        Assert.True(ForscanCsv.Parse("Time,ECT\n07:47:11.456,49\n07:47:12.000,50\n").ClockTimes);
        Assert.False(ForscanCsv.Parse("Time,ECT\n00:00:00.000,49\n00:00:01.000,50\n").ClockTimes);
    }

    /// <summary>
    /// Traffic for three identifiers over 20 s: one that tracks the logged value (÷16), one that
    /// counts up unrelated, and one constant.
    /// </summary>
    private static (IdentifierTable Table, DateTimeOffset Start) Traffic(Func<int, double> celsius)
    {
        var table = new IdentifierTable();
        var tft = new IdentifierKey(CanBus.Hs, 0x7E0, 0x22, 0x1E1C);
        var counter = new IdentifierKey(CanBus.Hs, 0x7E0, 0x22, 0xDD00);
        var flat = new IdentifierKey(CanBus.Ms, 0x720, 0x22, 0x404C);
        var start = T0;

        for (var i = 0; i < 200; i++)
        {
            var at = start.AddMilliseconds(i * 100);
            var raw = (int)Math.Round(celsius(i) * 16);
            table.Add(new Observation(at, tft, [(byte)(raw >> 8), (byte)raw]));
            table.Add(new Observation(at.AddMilliseconds(30), counter, [0, 0, (byte)(i / 2), (byte)(i * 7)]));
            table.Add(new Observation(at.AddMilliseconds(60), flat, [0x12]));
        }

        return (table, start);
    }

    private static double Warming(int i) => 15 + (i * 0.05) + (Math.Sin(i / 7.0) * 0.4);

    [Fact]
    public void A_log_counted_from_its_start_is_lined_up_and_its_column_matched()
    {
        var (table, start) = Traffic(Warming);

        // FORScan's log begins with the traffic and counts in milliseconds, in °F.
        var rows = Enumerable.Range(0, 40).Select(r =>
        {
            var i = r * 5;
            var fahrenheit = (Warming(i) * 9 / 5) + 32;
            return $"{i * 100},{fahrenheit.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}";
        });
        var log = ForscanCsv.Parse("Time (ms),Transmission Fluid Temp (°F)\n" + string.Join("\n", rows));

        var result = LogMatcher.Match(log, table);
        var match = Assert.Single(result.Columns);

        Assert.Equal(start, result.LogStart);
        Assert.Equal(new IdentifierKey(CanBus.Hs, 0x7E0, 0x22, 0x1E1C), match.Best!.Value.Key);
        Assert.True(match.Best.Value.Scaling.R2 > 0.99);

        // In °F, the fitted line is the ÷16 in °C carried through the conversion.
        Assert.Equal(0.0625 * 9 / 5, match.Best.Value.Scaling.Scale, 3);
    }

    [Fact]
    public void A_log_with_times_of_day_lines_up_by_the_clock()
    {
        var (table, start) = Traffic(Warming);
        var local = start.ToLocalTime();
        var rows = Enumerable.Range(0, 40).Select(r =>
        {
            var i = r * 5;
            var time = (local.TimeOfDay + TimeSpan.FromMilliseconds(i * 100)).ToString(@"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture);
            return $"{time},{Warming(i).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";
        });
        var log = ForscanCsv.Parse("Time,TFT (°C)\n" + string.Join("\n", rows));

        var match = Assert.Single(LogMatcher.Match(log, table).Columns);

        Assert.Equal(0x1E1C, match.Best!.Value.Key.Pid);
        Assert.Equal(0.0625, match.Best.Value.Scaling.Scale, 4);
        Assert.Equal(0, match.Best.Value.Scaling.Offset, 2);
    }

    [Fact]
    public void A_fahrenheit_fit_carries_into_celsius_as_the_usual_sixteenth()
    {
        var fitted = new ScalingCandidate(new RawWindow(0, 2, false), 0.1125, 32.0004, 0.05, 40, 30, true, 0.9999);

        var metric = ScalingFitter.ToMetric(fitted, UnitConversion.Parse("°F"));

        Assert.Equal(0.0625, metric.Scale);
        Assert.Equal(0, metric.Offset);
        Assert.Same(fitted, ScalingFitter.ToMetric(fitted, UnitConversion.Parse("°C")));
    }

    [Fact]
    public void A_column_that_never_moved_is_reported_not_matched()
    {
        var (table, _) = Traffic(Warming);
        var log = ForscanCsv.Parse("Time (ms),Odometer\n0,61234\n1000,61234\n2000,61234\n");

        var match = Assert.Single(LogMatcher.Match(log, table).Columns);

        Assert.Null(match.Best);
        Assert.Contains("never changed", match.Problem, StringComparison.Ordinal);
    }

    // ── Switches: on and off ──────────────────────────────────────────────────

    [Theory]
    [InlineData("On", true)]
    [InlineData("OPEN", true)]
    [InlineData("Ajar", true)]
    [InlineData("yes", true)]
    [InlineData("Off", false)]
    [InlineData("Closed", false)]
    [InlineData("not lit", false)]
    [InlineData("12", null)]
    [InlineData("", null)]
    public void State_words_are_read_as_on_or_off(string typed, bool? expected) =>
        Assert.Equal(expected, UnitConversion.ParseState(typed));

    [Fact]
    public void A_switch_typed_both_ways_is_the_one_bit_that_followed_it()
    {
        // Door ajar is bit 3 of the first byte; bit 6 is always set and bit 0 wanders.
        var candidates = ScalingFitter.FromStates([
            ([0x48, 0x01], true),
            ([0x40, 0x01], false),
            ([0x49, 0x00], true),
            ([0x41, 0x00], false),
        ]);

        var best = Assert.Single(candidates);
        Assert.Equal(3, best.Bit);
        Assert.False(best.Inverted);
        Assert.Equal(8, best.Mask);
        Assert.Equal(2, best.DistinctRaw);
        Assert.Equal(1, best.Apply([0x48, 0x00]));
        Assert.Equal(0, best.Apply([0x47, 0x00]));
        Assert.Contains("bit 3 of A", best.Formula, StringComparison.Ordinal);
    }

    [Fact]
    public void One_state_fits_many_bits_and_an_inverted_switch_is_found()
    {
        var once = ScalingFitter.FromStates([([0x00], true)]);
        Assert.Equal(8, once.Count);
        Assert.All(once, c => Assert.True(c.Inverted));

        // A seatbelt that reads 1 when unfastened: on (fastened) is 0.
        var both = ScalingFitter.FromStates([([0x00], true), ([0x02], false)]);
        var best = Assert.Single(both);
        Assert.True(best.Inverted);
        Assert.Equal(1, best.Bit);
        Assert.Equal(1, best.Apply([0x00]));
        Assert.Equal(0, best.Apply([0x02]));
    }

    [Fact]
    public void A_switch_decode_reads_zero_or_one_and_loads_from_an_export()
    {
        var scaling = ScalingCandidate.ForBit(0, 3, false, 4, 2);
        var entries = MatchExport.ToPackEntries([
            new AcceptedMatch(new IdentifierKey(CanBus.Ms, 0x726, 0x22, 0xD100), "Driver Door Ajar", scaling, "", "4 switch states"),
        ]);

        Assert.Contains("\"mask\": 8", entries, StringComparison.Ordinal);
        var pack = VehiclePacks.Parse($$"""{ "name": "t", "match": { "make": "Ford" }, "signals": [ {{entries}} ] }""");

        var signal = Assert.Single(pack.Signals);
        Assert.Equal(8, signal.Decode.Mask);
        Assert.Equal(1, signal.Decode.Decode([0x4C]));
        Assert.Equal(0, signal.Decode.Decode([0x44]));
    }

    [Fact]
    public void A_logged_on_off_column_is_matched_to_the_bit_that_follows_it()
    {
        var table = new IdentifierTable();
        var door = new IdentifierKey(CanBus.Ms, 0x726, 0x22, 0xD100);
        var counter = new IdentifierKey(CanBus.Hs, 0x7E0, 0x22, 0xDD00);
        bool Open(int i) => i is >= 50 and < 120;

        for (var i = 0; i < 200; i++)
        {
            var at = T0.AddMilliseconds(i * 100);
            table.Add(new Observation(at, door, [(byte)(0x40 | (Open(i) ? 0x08 : 0) | (i & 1)), 0x00]));
            table.Add(new Observation(at.AddMilliseconds(30), counter, [0, (byte)(i * 3)]));
        }

        var rows = Enumerable.Range(0, 40).Select(r => $"{r * 500},{(Open(r * 5) ? "Open" : "Closed")}");
        var log = ForscanCsv.Parse("Time (ms),Door Ajar\n" + string.Join("\n", rows));

        var match = Assert.Single(LogMatcher.Match(log, table).Columns);

        Assert.Equal(door, match.Best!.Value.Key);
        Assert.Equal(3, match.Best.Value.Scaling.Bit);
        Assert.False(match.Best.Value.Scaling.Inverted);
        Assert.True(match.Best.Value.Scaling.R2 >= 0.97);
    }

    [Fact]
    public void A_switch_paired_with_a_placeholder_keeps_the_mask()
    {
        var target = TestCatalog.Load().Definitions.Single(d => d.Id == "warning.doorAjar");
        var match = new AcceptedMatch(new IdentifierKey(CanBus.Ms, 0x726, 0x22, 0xD100), "Door Ajar",
            ScalingCandidate.ForBit(0, 3, false, 4, 2), "", "4 switch states");

        var paired = SignalPairing.Pair(match, target);

        Assert.Equal("warning.doorAjar", paired.Id);
        Assert.False(paired.Placeholder);
        Assert.Equal(8, paired.Decode.Mask);
        Assert.Equal(1, paired.Decode.Decode([0x08]));
        Assert.Equal(0, paired.Decode.Decode([0xF7]));
    }

    // ── The export ────────────────────────────────────────────────────────────

    [Fact]
    public void The_export_loads_as_a_vehicle_pack_signal_and_decodes_what_was_matched()
    {
        var scaling = new ScalingCandidate(new RawWindow(0, 2, false), 0.0625, 0, 0.01, 3, 3, false);
        var entries = MatchExport.ToPackEntries([
            new AcceptedMatch(new IdentifierKey(CanBus.Hs, 0x7E0, 0x22, 0x1E1C), "Transmission Fluid Temp", scaling, "°C", "3 typed samples"),
        ]);

        var pack = VehiclePacks.Parse($$"""
            {
              "name": "test",
              "match": { "make": "Ford" },
              "signals": [
            {{entries}}
              ]
            }
            """);

        var signal = Assert.Single(pack.Signals);
        Assert.Equal("ford.transmissionFluidTemp", signal.Id);
        Assert.Equal(0x22, signal.Mode);
        Assert.Equal(0x1E1C, signal.Pid);
        Assert.Equal("7E0", signal.Module);
        Assert.Equal(15.1875, signal.Decode.Decode([0x00, 0xF3]));
    }
}

/// <summary>Managing DashDeck's own IDs in the matcher (ADR-0050): what needs one, and pairing.</summary>
public sealed class SignalPairingTests
{
    private static SignalCatalog Standard() => TestCatalog.Load();

    [Fact]
    public void The_shipped_placeholders_are_marked_and_need_an_id()
    {
        var standings = SignalPairing.Stand(Standard(), [], []);

        var needs = standings.Where(s => s.NeedsId).Select(s => s.Definition.Id).ToList();
        Assert.Contains("fuel.economy", needs);
        Assert.Contains("tire.frontLeft.pressure", needs);
        Assert.Contains("hvac.cabinTemp", needs);
        Assert.Contains("warning.doorAjar", needs);
        Assert.Contains("body.tailgate", needs);
        Assert.DoesNotContain("engine.rpm", needs);
        Assert.True(standings[0].NeedsId);   // listed first
    }

    [Fact]
    public void Standard_pids_the_truck_says_it_lacks_need_a_ford_id()
    {
        // The engine computer's real supported-PID answers, heard through the tap on 2026-10-04.
        var table = new IdentifierTable();
        void Bitmap(int pid, string hex) => table.Add(new Observation(
            DateTimeOffset.UnixEpoch, new IdentifierKey(CanBus.Hs, IdentifierKey.Broadcast, 0x01, (ushort)pid), Convert.FromHexString(hex)));
        Bitmap(0x00, "BFBEA893");
        Bitmap(0x20, "A007B119");
        Bitmap(0x40, "FCD09501");
        Bitmap(0x60, "01A02001");

        var supported = SignalPairing.SupportedPids(table)!;
        var standings = SignalPairing.Stand(Standard(), [], [], supported).ToDictionary(s => s.Definition.Id);

        // Q4: neither fuel rate (5E) nor MAF (10); and no oil temperature (5C).
        Assert.Equal(PairingStatus.NotOnThisTruck, standings["engine.fuelRate"].Status);
        Assert.Equal(PairingStatus.NotOnThisTruck, standings["engine.mafRate"].Status);
        Assert.Equal(PairingStatus.NotOnThisTruck, standings["engine.oilTemp"].Status);
        Assert.Equal(PairingStatus.Standard, standings["engine.rpm"].Status);
        Assert.Equal(PairingStatus.Standard, standings["engine.coolantTemp"].Status);
    }

    [Fact]
    public void A_paired_signal_in_the_overlay_no_longer_needs_an_id()
    {
        var target = Standard().Definitions.Single(d => d.Id == "fuel.economy");
        var match = new AcceptedMatch(new IdentifierKey(CanBus.Ms, 0x720, 0x22, 0x404C), "Instant economy",
            new ScalingCandidate(new RawWindow(0, 2, false), 0.1, 0, 0, 3, 3, false), "L/100km", "3 typed samples");

        var paired = SignalPairing.Pair(match, target);
        var standings = SignalPairing.Stand(Standard(), [], [paired]).ToDictionary(s => s.Definition.Id);

        Assert.Equal(PairingStatus.Yours, standings["fuel.economy"].Status);
        Assert.False(paired.Placeholder);
        Assert.Equal("fuel.economy", paired.Id);
        Assert.Equal("720", paired.Module);
        Assert.Equal(0x22, paired.Mode);
        Assert.Equal(0x404C, paired.Pid);
        Assert.Equal(CanBus.Ms, paired.Bus);
        Assert.Equal(target.Max, paired.Max);
    }

    [Fact]
    public void A_match_in_kpa_fills_a_signal_kept_in_psi()
    {
        var target = Standard().Definitions.Single(d => d.Id == "tire.frontLeft.pressure");
        var match = new AcceptedMatch(new IdentifierKey(CanBus.Ms, 0x726, 0x22, 0x2813), "LF tire",
            new ScalingCandidate(new RawWindow(0, 1, false), 2, 0, 0, 3, 3, false), "kPa", "PID log");

        var paired = SignalPairing.Pair(match, target);

        Assert.Equal("psi", paired.Decode.Unit);
        Assert.Equal(240 / 6.894757, paired.Decode.Decode([120])!.Value, 3);   // 120 counts = 240 kPa = 34.8 psi
    }

    [Fact]
    public void A_match_in_celsius_fills_a_signal_kept_in_fahrenheit()
    {
        Assert.Equal((9.0 / 5, 32.0), SignalPairing.FromMetric("°C", "°F"));
        Assert.Null(SignalPairing.FromMetric("°C", "°C"));
        Assert.False(SignalPairing.UnitsAgree("°C", "%"));
    }

    [Fact]
    public void Pairings_export_as_pack_entries_that_load()
    {
        var target = Standard().Definitions.Single(d => d.Id == "fuel.range");
        var match = new AcceptedMatch(new IdentifierKey(CanBus.Ms, 0x720, 0x22, 0x4044), "DTE",
            new ScalingCandidate(new RawWindow(0, 2, false), 1, 0, 0, 3, 3, false), "km", "PID log, R² 0.9990");
        var paired = SignalPairing.Pair(match, target);

        var entries = MatchExport.ToPackEntries([(paired, "720 22 4044 — matched to FORScan's DTE")]);
        var pack = VehiclePacks.Parse($$"""{ "name": "t", "match": { "make": "Ford" }, "signals": [ {{entries}} ] }""");

        var signal = Assert.Single(pack.Signals);
        Assert.Equal("fuel.range", signal.Id);
        Assert.False(signal.Placeholder);
        Assert.Equal(2000, signal.Max);
    }
}

/// <summary>Adding, editing, hiding and removing signals from the matcher (ADR-0051).</summary>
public sealed class SignalWorkbenchTests
{
    private static SignalCatalog Standard() => TestCatalog.Load();

    private static SignalDefinition Maf => Standard().Definitions.Single(d => d.Id == "engine.mafRate");

    [Fact]
    public void Hiding_a_built_in_writes_a_hidden_copy_and_unhiding_removes_it()
    {
        var hidden = SignalWorkbench.Hide([], Maf);

        var entry = Assert.Single(hidden);
        Assert.True(entry.Hidden);
        Assert.Equal(Maf with { Hidden = true }, entry);
        Assert.True(SignalPairing.Stand(Standard(), [], hidden).Single(s => s.Definition.Id == "engine.mafRate").Hidden);

        Assert.Empty(SignalWorkbench.Unhide(hidden, Maf.Id, Maf));
    }

    [Fact]
    public void Unhiding_a_corrected_built_in_keeps_the_correction()
    {
        var corrected = Maf with { Name = "Air flow (MAF)", Hidden = true };

        var after = SignalWorkbench.Unhide([corrected], Maf.Id, Maf);

        var entry = Assert.Single(after);
        Assert.False(entry.Hidden);
        Assert.Equal("Air flow (MAF)", entry.Name);
    }

    [Fact]
    public void A_plain_built_in_cannot_be_removed_only_hidden()
    {
        Assert.Equal(RemoveKind.None, SignalWorkbench.CanRemove([], Maf.Id, isBuiltIn: true));
    }

    [Fact]
    public void Removing_a_correction_reverts_and_removing_yours_deletes()
    {
        var yours = Maf with { Id = "ford.transmissionTemp", Name = "TFT" };
        var corrected = Maf with { Name = "Air flow (MAF)" };
        var overlay = new[] { yours, corrected };

        Assert.Equal(RemoveKind.Remove, SignalWorkbench.CanRemove(overlay, yours.Id, isBuiltIn: false));
        Assert.Equal(RemoveKind.Revert, SignalWorkbench.CanRemove(overlay, corrected.Id, isBuiltIn: true));
        Assert.Equal([corrected], SignalWorkbench.Remove(overlay, yours.Id));
    }

    [Fact]
    public void Renaming_or_a_new_range_keeps_a_signal_measured()
    {
        var after = SignalWorkbench.Edited(Maf, Maf with { Name = "Air flow", Category = "Air", Max = 300, DefaultRateHz = 2 });

        Assert.False(after.Unconfirmed);
    }

    [Fact]
    public void Changing_where_it_comes_from_or_how_it_decodes_makes_it_unconfirmed()
    {
        var newPid = SignalWorkbench.Edited(Maf, Maf with { Mode = 0x22, Pid = 0x1E1C, Module = "7E0" });
        var newScale = SignalWorkbench.Edited(Maf, Maf with { Decode = Maf.Decode with { Scale = 0.02 } });

        Assert.True(newPid.Unconfirmed);
        Assert.True(newScale.Unconfirmed);
    }

    [Fact]
    public void A_placeholder_given_a_real_id_by_hand_stops_being_a_placeholder_and_waits_for_test()
    {
        var economy = Standard().Definitions.Single(d => d.Id == "fuel.economy");

        var typed = SignalWorkbench.Edited(economy, economy with { Mode = 0x22, Pid = 0x404C, Module = "720" });
        var standing = SignalPairing.Stand(Standard(), [], [typed]).Single(s => s.Definition.Id == "fuel.economy");

        Assert.False(typed.Placeholder);
        Assert.True(typed.Unconfirmed);
        Assert.Equal(PairingStatus.Unconfirmed, standing.Status);
        Assert.False(standing.NeedsId);
    }

    [Fact]
    public void Hidden_and_unconfirmed_survive_the_overlay_file_and_default_to_absent()
    {
        var json = SignalCatalog.ToJson([Maf with { Hidden = true, Unconfirmed = true }, Maf with { Id = "x.y" }]);

        var back = SignalCatalog.ParseList(json);

        Assert.True(back[0].Hidden);
        Assert.True(back[0].Unconfirmed);
        Assert.False(back[1].Hidden);
        Assert.DoesNotContain("\"hidden\": false", json, StringComparison.Ordinal);
    }
}
