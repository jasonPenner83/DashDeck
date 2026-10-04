using DashDeck.Abstractions;
using DashDeck.Core;
using DashDeck.Core.Catalog;
using DashDeck.Core.Identity;
using DashDeck.Simulator;
using DashDeck.Vehicle;
using DashDeck.Vehicle.Elm;

namespace DashDeck.Tests;

/// <summary>
/// Which vehicle this is: the VIN, read from the truck or typed, decoded, and the signal pack it
/// selects (ADR-0033).
/// </summary>
public class IdentityTests
{
    // ── The VIN itself ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(" 1ftew1ep2-kf000000 ", "1FTEW1EP2KF000000")]
    [InlineData("1FTEW 1EP2K F000000", "1FTEW1EP2KF000000")]
    public void A_typed_vin_is_tidied(string typed, string expected) =>
        Assert.Equal(expected, Vin.Normalize(typed));

    [Theory]
    [InlineData("1FTEW1EP2KF000000", true)]
    [InlineData("1FTEW1EP2KF00000", false)]   // sixteen
    [InlineData("1FTEW1EP2KF00000O", false)]  // O is not a VIN letter
    [InlineData("", false)]
    public void Knows_a_well_formed_vin(string vin, bool expected) =>
        Assert.Equal(expected, Vin.IsWellFormed(vin));

    [Fact]
    public void Checks_the_north_american_check_digit()
    {
        // Made up, like every VIN in this repository — this one's remainder is 10, so its check
        // digit is the letter X.
        Assert.True(Vin.HasValidCheckDigit("ZZZZZZBZXZ0000000"));
        Assert.True(Vin.HasValidCheckDigit(SyntheticTruckData.Shipped().Vin!));
        Assert.False(Vin.HasValidCheckDigit("1FTEW1EP3KF000000"));
        Assert.Equal('2', Vin.ExpectedCheckDigit("1FTEW1EP3KF000000"));
    }

    [Fact]
    public void Takes_the_vin_from_the_end_of_a_mode_09_payload()
    {
        var payload = new byte[] { 0x01 }.Concat("1FTEW1EP2KF000000"u8.ToArray()).ToArray();

        Assert.Equal("1FTEW1EP2KF000000", Vin.FromMode09Payload(payload));
        Assert.Null(Vin.FromMode09Payload([0x01, 0x31, 0x32]));
    }

    // ── Reading it off the truck ──────────────────────────────────────────────

    [Fact]
    public async Task Reads_the_vin_from_the_synthetic_truck_through_the_real_parser()
    {
        // The multi-frame reply, frame indices glued to the data the way ATS0 prints them, all
        // the way through ElmAdapter and ElmResponseParser.
        var transport = new SyntheticTransport(new SimulatedF150(Drives.ColdStartCity), faults: SyntheticFaults.Perfect);
        await using var service = new VehicleService(new ElmAdapter(transport), TestCatalog.Load());
        await service.StartAsync(TestCancellation.Token);

        var read = await VinReader.ReadAsync(service.ProbeAsync, TestCancellation.Token);

        Assert.Null(read.Problem);
        Assert.Equal(SyntheticTruckData.Shipped().Vin!, read.Vin);
    }

    [Fact]
    public void Parses_a_multi_frame_reply_with_spaces_off()
    {
        var raw = "014\r0:490201314654\r1:45573145503250\r2:4B463030303030\r\r>";
        var response = ElmResponseParser.Parse(VinReader.Request, raw, DateTimeOffset.UnixEpoch);

        Assert.True(response.IsSuccess, response.Failure.ToString());
        Assert.Equal(18, response.Data.Length);
    }

    [Fact]
    public void Parses_a_multi_frame_reply_with_spaces_on()
    {
        var raw = "014\r0: 49 02 01 31 46 54\r1: 45 57 31 45 50 32 50\r2: 4B 46 30 30 30 30 30\r\r>";
        var response = ElmResponseParser.Parse(VinReader.Request, raw, DateTimeOffset.UnixEpoch);

        Assert.True(response.IsSuccess, response.Failure.ToString());
        Assert.Equal(18, response.Data.Length);
    }

    [Fact]
    public async Task A_truck_that_will_not_say_is_reported_not_guessed()
    {
        var calls = 0;

        Task<PidResponse> Silent(PidRequest request, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(PidResponse.Failed(request, PidFailure.NoData, DateTimeOffset.UnixEpoch));
        }

        var read = await VinReader.ReadAsync(Silent, CancellationToken.None);

        Assert.Null(read.Vin);
        Assert.Contains("mode 09", read.Problem, StringComparison.Ordinal);
        Assert.Equal(VinReader.Attempts, calls);
    }

    // ── Decoding it ───────────────────────────────────────────────────────────

    private const string CleanDecode = """
        {"Count":1,"Message":"Results returned successfully","SearchCriteria":"VIN:1FTEW1EP2KF000000",
         "Results":[{"Make":"FORD","Model":"F-150","ModelYear":"2019","Trim":"XLT","DisplacementL":"2.694",
                     "EngineCylinders":"6","Turbo":"Yes","FuelTypePrimary":"Gasoline","DriveType":"4WD/4-Wheel Drive/4x4",
                     "ErrorCode":"0","ErrorText":"0 - VIN decoded clean. Check Digit (9th position) is correct"}]}
        """;

    [Fact]
    public void Reads_a_clean_vpic_decode()
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var result = VpicDecoding.Parse(CleanDecode, "1FTEW1EP2KF000000", at);

        Assert.Null(result.Problem);
        var vehicle = result.Identity!;
        Assert.Equal(2019, vehicle.ModelYear);
        Assert.Equal("FORD", vehicle.Make);
        Assert.Equal("F-150", vehicle.Model);
        Assert.Equal(2.7, vehicle.DisplacementLitres);
        Assert.Equal(6, vehicle.Cylinders);
        Assert.True(vehicle.Turbocharged);
        Assert.Equal("NHTSA vPIC", vehicle.Source);
        Assert.Equal(at, vehicle.DecodedUtc);
        Assert.Equal("2019 FORD F-150 XLT  ·  2.7 L 6 cyl turbo  ·  Gasoline", vehicle.Describe());
    }

    [Fact]
    public void Keeps_a_partial_decode_and_says_what_the_decoder_said()
    {
        var json = """
            {"Results":[{"Make":"FORD","Model":"","ModelYear":"2019","DisplacementL":"","EngineCylinders":"",
                         "ErrorCode":"1","ErrorText":"1 - Check Digit (9th position) does not calculate properly"}]}
            """;

        var result = VpicDecoding.Parse(json, "1FTEW1EP3KF000000", DateTimeOffset.UnixEpoch);

        Assert.Equal("FORD", result.Identity!.Make);
        Assert.Null(result.Identity.Model);
        Assert.Null(result.Identity.DisplacementLitres);
        Assert.Contains("Check Digit", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vin_the_decoder_does_not_know_is_a_failure()
    {
        var json = """{"Results":[{"Make":"","Model":"","ModelYear":"","ErrorCode":"7","ErrorText":"7 - Manufacturer not registered"}]}""";

        var result = VpicDecoding.Parse(json, "ZZZZZZZZZZZZZZZZZ", DateTimeOffset.UnixEpoch);

        Assert.Null(result.Identity);
        Assert.Contains("not registered", result.Problem, StringComparison.Ordinal);
        Assert.NotNull(VpicDecoding.Parse("not json", "x", DateTimeOffset.UnixEpoch).Problem);
    }

    [Fact]
    public void The_profile_gets_the_facts_but_never_the_vin()
    {
        var vehicle = VpicDecoding.Parse(CleanDecode, "1FTEW1EP2KF000000", DateTimeOffset.UnixEpoch).Identity!;
        var profile = vehicle.ToProfile(fuelTankLitres: 136);

        Assert.Equal(136, profile.FuelTankLitres);
        Assert.Equal(2019, profile.ModelYear);
        Assert.Equal(2.7, profile.EngineDisplacementLitres);
        Assert.DoesNotContain(typeof(VehicleProfile).GetProperties(), p => p.Name.Contains("Vin", StringComparison.OrdinalIgnoreCase));
    }

    // ── Choosing a signal pack ────────────────────────────────────────────────

    private static VehicleIdentity F150(int year = 2019, double litres = 2.7, string model = "F-150") =>
        new() { Make = "FORD", Model = model, ModelYear = year, DisplacementLitres = litres };

    private static VehiclePack Pack() => VehiclePacks.Parse("""
        {
          "name": "Test pack",
          "match": { "make": "Ford", "model": "F-150", "yearMin": 2018, "yearMax": 2020, "displacementLitres": 2.7 },
          "signals": [
            { "id": "trans.fluidTemp", "name": "Transmission Temperature", "mode": 34, "pid": 4352,
              "decode": { "byteOffset": 0, "byteLength": 1, "signed": false, "scale": 1, "offset": -40, "unit": "°C" } }
          ]
        }
        """, "test.json");

    [Theory]
    [InlineData(2019, 2.7, "F-150", true)]
    [InlineData(2019, 2.7, "F150", true)]      // decoders disagree about the dash
    [InlineData(2019, 3.5, "F-150", false)]    // a different engine
    [InlineData(2021, 2.7, "F-150", false)]    // a different generation
    [InlineData(2019, 2.7, "Ranger", false)]
    public void A_pack_matches_the_vehicle_it_was_written_for(int year, double litres, string model, bool expected) =>
        Assert.Equal(expected, Pack().Match.Matches(F150(year, litres, model)));

    [Fact]
    public void An_unknown_vehicle_gets_no_pack() =>
        Assert.Empty(VehiclePacks.Select([Pack()], VehicleIdentity.Unknown));

    [Fact]
    public void A_matching_pack_is_laid_over_the_standard_catalog()
    {
        var catalog = VehiclePacks.Apply(TestCatalog.Load(), VehiclePacks.Select([Pack()], F150()));

        Assert.Equal(TestCatalog.Load().Count + 1, catalog.Count);
        Assert.Equal(0x22, catalog["trans.fluidTemp"].Mode);
    }

    [Fact]
    public void A_pack_that_would_match_everything_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            VehiclePacks.Parse("""{ "name": "Everything", "match": {}, "signals": [] }""", "all.json"));

        Assert.Contains("match.make", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pack_can_name_the_rate_on_pins_3_and_11_and_a_bad_one_is_refused()
    {
        var pack = VehiclePacks.Parse("""{ "name": "P", "match": { "make": "Ford" }, "pins311BitRate": 500000 }""", "p.json");
        Assert.Equal(500000, VehiclePacks.Pins311BitRate([pack]));
        Assert.Null(VehiclePacks.Pins311BitRate([Pack()]));

        var ex = Assert.Throws<InvalidDataException>(() =>
            VehiclePacks.Parse("""{ "name": "P", "match": { "make": "Ford" }, "pins311BitRate": 123 }""", "p.json"));
        Assert.Contains("pins311BitRate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repository_ships_no_vehicle_files()
    {
        // ADR-0052: what is specific to a vehicle is the user's, in %LOCALAPPDATA%\DashDeck\vehicles.
        var folder = Path.Combine(Path.GetDirectoryName(TestCatalog.Path())!, "vehicles");

        Assert.Empty(Directory.EnumerateFiles(folder, "*.json"));
    }

    [Fact]
    public void A_users_vehicle_file_replaces_a_shipped_one_of_the_same_name_and_adds_the_rest()
    {
        var root = Path.Combine(Path.GetTempPath(), "dashdeck-packs-" + Guid.NewGuid().ToString("N"));
        var shipped = Directory.CreateDirectory(Path.Combine(root, "shipped")).FullName;
        var yours = Directory.CreateDirectory(Path.Combine(root, "yours")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(shipped, "a.json"), """{ "name": "Shipped A", "match": { "make": "Ford" } }""");
            File.WriteAllText(Path.Combine(yours, "a.json"), """{ "name": "Your A", "match": { "make": "Ford" }, "pins311BitRate": 500000 }""");
            File.WriteAllText(Path.Combine(yours, "b.json"), """{ "name": "Your B", "match": { "make": "Ford" } }""");

            var (packs, problems) = VehiclePacks.LoadFolders(shipped, yours, Path.Combine(root, "missing"));

            Assert.Empty(problems);
            Assert.Equal(["Your A", "Your B"], packs.Select(p => p.Name));
            Assert.Equal(500000, VehiclePacks.Pins311BitRate(VehiclePacks.Select(packs, F150())));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_pack_can_name_its_identity_question_and_ranges_and_bad_ones_are_refused()
    {
        var pack = VehiclePacks.Parse("""
            { "name": "P", "match": { "make": "Ford" }, "identityDid": "F113",
              "identifierRanges": [ { "name": "Body", "from": "DD00", "to": "DDFF" } ] }
            """, "p.json");
        Assert.Equal("F113", pack.IdentityDid);
        Assert.Single(pack.IdentifierRanges);

        var ex = Assert.Throws<InvalidDataException>(() => VehiclePacks.Parse("""
            { "name": "P", "match": { "make": "Ford" }, "identityDid": "XYZ",
              "identifierRanges": [ { "from": "DDFF", "to": "DD00" } ] }
            """, "p.json"));
        Assert.Contains("identityDid", ex.Message, StringComparison.Ordinal);
        Assert.Contains("identifierRanges", ex.Message, StringComparison.Ordinal);
    }
}
