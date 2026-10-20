using System.Text;
using DashDeck.Core.Catalog;
using DashDeck.IdHunter;

namespace DashDeck.Tests;

/// <summary>The ID hunter's guide, driven end to end against the synthetic truck (ADR-0044).</summary>
public sealed class IdHunterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dashdeck-hunt-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A person reading from a script. <c>@hold</c> is someone not pressing Enter: a read while
    /// something else runs waits on it until cancelled, and a plain read skips it. Any other
    /// <c>@</c> line a plain read meets is handed to <see cref="Hook"/> — the test changing the world
    /// between two things the person types, the way a driver sets cruise between two readings.
    /// </summary>
    private sealed class Script(params string[] lines) : IHuntConsole
    {
        public Action<string>? Hook { get; init; }

        private readonly Queue<string> _lines = new(lines);
        private readonly StringBuilder _out = new();

        public string Output
        {
            get
            {
                lock (_out)
                {
                    return _out.ToString();
                }
            }
        }

        public void Write(string text)
        {
            lock (_out)
            {
                _out.Append(text);
            }
        }

        public void WriteLine(string text = "") => Write(text + "\n");

        public string? ReadLine()
        {
            while (_lines.TryPeek(out var next) && next.StartsWith('@'))
            {
                var item = _lines.Dequeue();
                if (item.StartsWith("@wait:", StringComparison.Ordinal))
                {
                    Wait(item);
                }
                else
                {
                    Hook?.Invoke(item);
                }
            }

            return _lines.TryDequeue(out var line) ? line : "q";
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            if (_lines.TryPeek(out var next) && next == "@hold")
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    _lines.Dequeue();
                }
            }

            if (_lines.TryPeek(out next) && next.StartsWith("@wait:", StringComparison.Ordinal))
            {
                await Task.Delay(int.Parse(next[6..], System.Globalization.CultureInfo.InvariantCulture), ct);
                _lines.Dequeue();
            }

            return _lines.TryDequeue(out var line) ? line : "q";
        }

        private static void Wait(string item)
        {
            if (item.StartsWith("@wait:", StringComparison.Ordinal))
            {
                Thread.Sleep(int.Parse(item[6..], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    private static string TargetsFile([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "DashDeck.IdHunter", "targets.json");

    private async Task<(Script Io, string Findings)> Run(params string[] script)
    {
        var (targets, problems) = HuntTargetFile.Load(TargetsFile());
        Assert.Empty(problems);

        var io = new Script(script);
        await using var session = await HuntSession.SimulateAsync(CancellationToken.None);
        var output = new HuntOutput(_folder, DashDeck.Abstractions.SystemClock.Instance);
        var wizard = new Wizard(io, session, targets, output, Array.Empty<VehiclePack>()) { HoldSeconds = 1, FollowMinutes = 0.2 };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await wizard.RunAsync(cts.Token);

        var findings = Path.Combine(_folder, "findings.csv");
        return (io, File.Exists(findings) ? await File.ReadAllTextAsync(findings) : "");
    }

    private static int Number(string id)
    {
        var (targets, _) = HuntTargetFile.Load(TargetsFile());
        return targets.ToList().FindIndex(t => t.Id == id) + 1;
    }

    [Fact]
    public void The_checklist_reads_cleanly_and_covers_each_way_of_finding()
    {
        var (targets, problems) = HuntTargetFile.Load(TargetsFile());

        Assert.Empty(problems);
        Assert.Contains(targets, t => t.Method == "listen");
        Assert.Contains(targets, t => t.Method == "follow");
        Assert.Contains(targets, t => t.Method == "match");
        Assert.All(targets.Where(t => t.Method != "listen"), t => Assert.NotEmpty(t.SimulateRanges));
        Assert.All(targets.SelectMany(t => t.Steps).SelectMany(s => s.Simulate.Keys),
            name => Assert.Contains(name, DashDeck.Simulator.SimulatedCabin.Names));
    }

    [Fact]
    public async Task Listening_for_the_driver_door_finds_its_bit_and_records_the_check()
    {
        var (io, findings) = await Run(
            Number("door.driver").ToString(System.Globalization.CultureInfo.InvariantCulture), "",
            "", "", "", "", "",
            "1", "@wait:3000", "", "y",
            "q");

        Assert.Contains("3B3       byte 0 bit 0", io.Output);
        Assert.Contains("3B3,,byte 0 bit 0,mask 01", findings);
        Assert.Contains(",confirmed,", findings);
        Assert.Single(Directory.GetFiles(_folder, "listen-door.driver-ms-*.csv"));
    }

    [Fact]
    public async Task Seat_levels_are_found_as_a_nibble_that_rises_with_them()
    {
        var (io, findings) = await Run(
            Number("seat.driver.heat").ToString(System.Globalization.CultureInfo.InvariantCulture), "",
            "", "", "", "", "",
            "", "b",
            "q");

        Assert.Contains("3B3       byte 2 low nibble     0 1 2 3 0", io.Output);
        Assert.Contains("unchecked", findings);
    }

    [Fact]
    public async Task Following_coolant_while_the_engine_warms_finds_the_temperatures()
    {
        var (io, findings) = await Run(
            Number("oil.temp").ToString(System.Globalization.CultureInfo.InvariantCulture), "",
            "", "", "",       // module 7E0, the simulated range, sweep
            "", "@hold",      // start watching; never press Enter, the time limit ends it
            "",               // no live check
            "q");

        var rows = findings.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Assert.True(rows.Count >= 2, io.Output);
        Assert.Matches("follow,HS-CAN,7E0,,410[12],", rows[0]);
        Assert.DoesNotContain(rows.Take(2), r => r.Contains(",4104,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Matching_the_tyre_pressures_puts_each_corner_first()
    {
        var (io, findings) = await Run(
            Number("tire.pressure").ToString(System.Globalization.CultureInfo.InvariantCulture), "",
            "7A0", "", "",    // the synthetic body module (the shipped checklist names none), the simulated range, sweep
            "35.5", "35", "27", "34.5",
            "",
            "q");

        Assert.Contains("FRONT LEFT — likeliest first:\n  #   ID     READ     DECODE                  GIVES     MATCHED\n  1   4301", io.Output.Replace("\r", "", StringComparison.Ordinal));
        Assert.Contains("match,pins 3/11 (125 kbit/s),7A0,,4303,REAR LEFT", findings);
    }

    [Fact]
    public async Task Moving_does_not_stop_the_guide()
    {
        // ADR-0069: the hunter no longer refuses to run on the road. A scan on a truck doing 100 km/h
        // asks the modules like any other.
        var (targets, _) = HuntTargetFile.Load(TargetsFile());
        var moving = new DashDeck.Simulator.SimulatedF150(DashDeck.Simulator.Drives.HighwayCruise);
        moving.Advance(TimeSpan.FromSeconds(60));
        var transport = new DashDeck.Simulator.SyntheticTransport(moving, faults: DashDeck.Simulator.SyntheticFaults.Perfect);
        await using var session = await HuntSession.ForAsync(transport, moving, CancellationToken.None);

        var io = new Script("S", "", "q");
        await new Wizard(io, session, targets, new HuntOutput(_folder, DashDeck.Abstractions.SystemClock.Instance), Array.Empty<VehiclePack>())
            .RunAsync(CancellationToken.None);

        Assert.Contains("SYNTH-ENGINE", io.Output);
        Assert.DoesNotContain("moving", io.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A truck that sits parked for ten minutes and then drives at 100 km/h, so a test can do things
    /// parked, on the road, or one then the other. <c>@drive</c> moves it on; <c>@cruise:N</c> sets
    /// the synthetic module's cruise set speed.
    /// </summary>
    private async Task<Script> RunOnTheRoad(params string[] script)
    {
        var (targets, _) = HuntTargetFile.Load(TargetsFile());
        var truck = new DashDeck.Simulator.SimulatedF150(new DashDeck.Simulator.ScriptedDrive(
            "parked-then-highway", "Parked, then 100 km/h.",
            [new DashDeck.Simulator.DriveSegment("parked", 600, 0), new DashDeck.Simulator.DriveSegment("highway", 3600, 100)]));
        var transport = new DashDeck.Simulator.SyntheticTransport(truck, faults: DashDeck.Simulator.SyntheticFaults.Perfect);
        await using var session = await HuntSession.ForAsync(transport, truck, CancellationToken.None);

        var io = new Script(script) { Hook = item => Act(truck, item) };
        await new Wizard(io, session, targets, new HuntOutput(_folder, DashDeck.Abstractions.SystemClock.Instance), Array.Empty<VehiclePack>())
            .RunAsync(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);

        return io;
    }

    private static void Act(DashDeck.Simulator.SimulatedF150 truck, string item)
    {
        if (item == "@drive")
        {
            // In the model's own small steps: past the parked ten minutes and twenty seconds up the road.
            for (var i = 0; i < 620 * 20; i++)
            {
                truck.Advance(TimeSpan.FromMilliseconds(50));
            }
        }
        else if (item.StartsWith("@cruise:", StringComparison.Ordinal))
        {
            truck.Cabin.Set("cruiseSet", double.Parse(item[8..], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static readonly string[] SweepTheEngine =
    [
        "M", "Cruise set speed", "",   // M, the name, Enter for the sweep's needs
        "7E0", "4100-41FF", "",         // the engine module, its invented range, sweep
    ];

    [Fact]
    public async Task A_number_you_name_is_found_from_two_readings_while_parked()
    {
        var io = await RunOnTheRoad([
            .. SweepTheEngine,
            "@cruise:60", "60",
            "@cruise:100", "100",
            "R",
            "",
            "q",
        ]);

        var output = io.Output.Replace("\r", "", StringComparison.Ordinal);
        Assert.Contains("Cruise set speed — likeliest first:\n  #   ID     READ     DECODE                  GIVES     MATCHED\n  1   4105", output);
        Assert.Contains("÷ 100", output);
        Assert.Contains("1 still fits all 2 readings", output);
        Assert.DoesNotContain("The truck is moving", output);

        var findings = await File.ReadAllTextAsync(Path.Combine(_folder, "findings.csv"));
        Assert.Contains("Cruise set speed,,match,HS-CAN,7E0,,4105,Cruise set speed", findings);
    }

    [Fact]
    public async Task A_number_you_name_is_swept_and_read_on_the_road()
    {
        // Cruise only sets while driving, so the whole match happens at 100 km/h: no prompt, no stop.
        var io = await RunOnTheRoad([
            "@drive",
            .. SweepTheEngine,
            "@cruise:60", "60",
            "@cruise:100", "100",
            "R",
            "",
            "q",
        ]);

        var output = io.Output.Replace("\r", "", StringComparison.Ordinal);

        Assert.DoesNotContain("moving", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, output.Split("Reading every identifier once").Length - 1);
        Assert.Contains("likeliest first:\n  #   ID     READ     DECODE                  GIVES     MATCHED\n  1   4105", output);

        // Each reading's speed is in the capture, so one taken on the road is visibly one.
        var capture = Directory.GetFiles(_folder, "match-free.cruise-set-speed-7E0-*.csv").Single();
        var rows = File.ReadAllLines(capture).Skip(1).Where(r => r.Contains(",4105,", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(double.Parse(r.Split(',')[^1], System.Globalization.CultureInfo.InvariantCulture) > 0, r));
    }

    [Fact]
    public async Task The_same_number_twice_is_not_a_second_reading()
    {
        var io = await RunOnTheRoad([
            .. SweepTheEngine,
            "@cruise:60", "60",
            "60",                       // not changed: refused, not counted
            "@cruise:100", "100",
            "R",
            "",
            "q",
        ]);

        Assert.Contains("That is 60 again", io.Output);
        Assert.Equal(2, io.Output.Split("Reading every identifier once").Length - 1);
    }

    [Theory]
    [InlineData("100", true, 100.0, "100")]
    [InlineData("100 km/h", true, 100.0, "100")]
    [InlineData(" 13.4", true, 13.4, "13.4")]
    [InlineData("km/h", false, 0.0, "")]
    public void A_typed_reading_keeps_the_number_and_drops_the_unit(string typed, bool ok, double shown, string number)
    {
        Assert.Equal(ok, Wizard.TryShown(typed, out var value, out var digits));
        Assert.Equal(number, digits);
        if (ok)
        {
            Assert.Equal(shown, value, 6);
        }
    }

    [Theory]
    [InlineData("Cruise set speed", "cruise-set-speed")]
    [InlineData("  Trip B / distance ", "trip-b-distance")]
    [InlineData("???", "value")]
    public void A_name_becomes_a_file_safe_id(string name, string slug) =>
        Assert.Equal(slug, Wizard.Slug(name));

    [Fact]
    public void Ranges_are_checked_before_a_sweep()
    {
        Assert.Equal(
            [(ushort)0x1000, (ushort)0x1FFF, (ushort)0x2000, (ushort)0x20FF],
            Wizard.ParseRanges("1000-1FFF, 2000-20FF")!.SelectMany(r => new[] { r.First, r.Last }));
        Assert.Null(Wizard.ParseRanges("1000-3FFF"));
        Assert.Null(Wizard.ParseRanges("2000-1000"));
        Assert.Null(Wizard.ParseRanges("nonsense"));
    }

    [Fact]
    public void The_dash_settings_say_where_the_adapter_was_and_which_port_is_the_gps()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, """
            { "adapterSerialPort": "COM4", "adapterBaudRate": 115200, "adapterIdentity": "STN2232 v5.12.4",
              "gpsEnabled": true, "gpsTransport": "Bluetooth", "gpsSerialPort": "COM7", "themeId": "builtin/modern" }
            """);

        var dash = DashSettings.Read(path);

        Assert.Equal(new DashSettings("COM4", 115200, "STN2232 v5.12.4", "COM7"), dash);
        Assert.Equal(new DashSettings(null, null, null, null), DashSettings.Read(Path.Combine(_folder, "missing.json")));

        File.WriteAllText(path, """{ "adapterSerialPort": "", "adapterBaudRate": 0, "gpsEnabled": false, "gpsSerialPort": "COM7" }""");
        Assert.Equal(new DashSettings(null, null, null, null), DashSettings.Read(path));

        File.WriteAllText(path, "not json");
        Assert.Equal(new DashSettings(null, null, null, null), DashSettings.Read(path));
    }

    [Fact]
    public async Task Pins_3_and_11_are_measured_by_listening_before_anything_is_sent_there()
    {
        var (targets, _) = HuntTargetFile.Load(TargetsFile());
        var truck = new DashDeck.Simulator.SimulatedF150(DashDeck.Simulator.Drives.Idle);
        var transport = new DashDeck.Simulator.SyntheticTransport(truck, faults: DashDeck.Simulator.SyntheticFaults.Perfect) { Pins311BitRate = 500000 };
        await using var session = await HuntSession.ForAsync(transport, truck, CancellationToken.None);

        var io = new Script("S", "", "q");
        await new Wizard(io, session, targets, new HuntOutput(_folder, DashDeck.Abstractions.SystemClock.Instance), Array.Empty<VehiclePack>())
            .RunAsync(CancellationToken.None);

        Assert.Contains("Pins 3/11 carry a 500 kbit/s bus", io.Output);
        Assert.Contains("pins 3/11 (500 kbit/s)  7A0", io.Output);
        Assert.Equal(500000, session.Adapter.Pins311BitRate);
    }

    [Fact]
    public async Task Silent_pins_3_and_11_are_never_asked()
    {
        var (targets, _) = HuntTargetFile.Load(TargetsFile());
        var truck = new DashDeck.Simulator.SimulatedF150(DashDeck.Simulator.Drives.Idle);
        var transport = new DashDeck.Simulator.SyntheticTransport(truck, faults: DashDeck.Simulator.SyntheticFaults.Perfect) { Pins311BitRate = 250000 };
        await using var session = await HuntSession.ForAsync(transport, truck, CancellationToken.None);

        var io = new Script("S", "", "q");
        await new Wizard(io, session, targets, new HuntOutput(_folder, DashDeck.Abstractions.SystemClock.Instance), Array.Empty<VehiclePack>())
            .RunAsync(CancellationToken.None);

        Assert.Contains("Nothing heard on pins 3/11", io.Output);
        Assert.Contains("only HS-CAN is asked", io.Output);
        Assert.DoesNotContain("pins 3/11 (", io.Output);
    }

    [Fact]
    public async Task A_value_the_module_keeps_to_itself_is_found_by_asking_it_through_the_steps()
    {
        var (io, findings) = await Run(
            Number("seat.driver.cool").ToString(System.Globalization.CultureInfo.InvariantCulture), "",
            "", "", "", "", "",   // the listen on pins 3/11
            "",                    // no live check
            "b",                   // not HS-CAN
            "",                    // ask a module instead
            "7B0", "4400-44FF", "", // the synthetic climate module, a range, sweep
            "",                    // ready
            "", "", "", "", "",    // the steps again
            "",                    // no live check
            "q");

        Assert.Contains("What 7B0 changed with you", io.Output);
        Assert.Contains("4401   byte 0 high nibble", io.Output);
        Assert.Contains("ask,pins 3/11 (125 kbit/s),7B0,,4401,byte 0 high nibble", findings);
        Assert.DoesNotContain(",4402,", findings);
    }

    [Fact]
    public async Task A_can_database_is_checked_against_the_truck()
    {
        Directory.CreateDirectory(_folder);
        var dbc = Path.Combine(_folder, "invented.dbc");
        File.WriteAllText(dbc, """
            BO_ 947 SynthBody: 8 BODY
             SG_ DriverDoor : 0|1@1+ (1,0) [0|1] "" Vector__XXX
            BO_ 2047 Ghost: 8 BODY
             SG_ GhostValue : 0|8@1+ (1,0) [0|255] "" Vector__XXX
            VAL_ 947 DriverDoor 1 "Ajar" 0 "Closed" ;
            """);

        var (io, findings) = await Run(
            "D", dbc, "",           // the file, check presence
            "door", "1",            // search, pick
            "@wait:1500", "", "y",  // live, stop, it matched
            "ghost", "1",           // a message the truck does not send
            "B",
            "q");

        Assert.Contains("are on this truck", io.Output);
        Assert.Contains("DriverDoor = 0 (Closed)", io.Output);
        Assert.Contains("SynthBody.DriverDoor,,dbc,pins 3/11 (125 kbit/s),,3B3,,DriverDoor,start 0 len 1 LE", findings);
        Assert.Contains(",confirmed,from invented.dbc", findings);
        Assert.Contains("Ghost.GhostValue,,dbc,,,7FF", findings);
        Assert.Contains(",absent,", findings);
    }
}
