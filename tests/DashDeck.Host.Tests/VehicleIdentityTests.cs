using System.IO;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Core.Identity;
using DashDeck.Host.Identity;
using DashDeck.Host.Settings;
using DashDeck.Host.ViewModels;
using DashDeck.Vehicle;

namespace DashDeck.Host.Tests;

/// <summary>
/// Settings ▸ Vehicle: the VIN, read or typed, decoded and cached, correctable by hand, and the
/// signal pack it picks (ADR-0033).
/// </summary>
public sealed class VehicleIdentityTests : IDisposable
{
    private const string SyntheticVin = "1FTEW1EP2KF000000";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dashdeck-vehicle-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "vehicle.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly VehicleIdentity Decoded = new()
    {
        Vin = SyntheticVin,
        ModelYear = 2019,
        Make = "FORD",
        Model = "F-150",
        DisplacementLitres = 2.7,
        Cylinders = 6,
        Turbocharged = true,
        FuelType = "Gasoline",
        Source = "NHTSA vPIC",
        DecodedUtc = DateTimeOffset.UnixEpoch,
    };

    private sealed class FakeDecoder(VinDecodeResult result) : IVinDecoder
    {
        public List<string> Asked { get; } = [];

        public Task<VinDecodeResult> DecodeAsync(string vin, DateTimeOffset nowUtc, CancellationToken ct)
        {
            Asked.Add(vin);
            return Task.FromResult(result);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private static Task<PidResponse> Answers(PidRequest request, CancellationToken ct)
    {
        var payload = new byte[] { 0x01 }.Concat(System.Text.Encoding.ASCII.GetBytes(SyntheticVin)).ToArray();
        return Task.FromResult(PidResponse.Ok(request, payload, DateTimeOffset.UnixEpoch));
    }

    private static Task<PidResponse> Silent(PidRequest request, CancellationToken ct) =>
        Task.FromResult(PidResponse.Failed(request, PidFailure.NoData, DateTimeOffset.UnixEpoch));

    private static readonly VehiclePack F150Pack = new()
    {
        Name = "Ford F-150 2.7 EcoBoost (2018–2020)",
        Match = new VehiclePackMatch { Make = "Ford", Model = "F-150", YearMin = 2018, YearMax = 2020, DisplacementLitres = 2.7 },
    };

    private VehicleIdentityViewModel Make(
        FakeDecoder? decoder = null,
        Func<PidRequest, CancellationToken, Task<PidResponse>>? probe = null,
        bool simulated = false) =>
        new(
            new VehicleIdentityStore(FilePath),
            decoder ?? new FakeDecoder(new VinDecodeResult(Decoded, null)),
            probe ?? Answers,
            new FixedClock(),
            simulated,
            [F150Pack],
            activePacks: [],
            packProblem: null);

    [Fact]
    public async Task Reads_the_vin_from_the_truck()
    {
        var vehicle = Make();

        await vehicle.ReadFromTruckCommand.ExecuteAsync(null);

        Assert.Equal(SyntheticVin, vehicle.VinText);
        Assert.False(vehicle.StatusIsProblem);
        Assert.Equal("Looks right.", vehicle.VinMessage);
    }

    [Fact]
    public async Task Says_when_the_vin_came_from_the_simulator()
    {
        var vehicle = Make(simulated: true);

        await vehicle.ReadFromTruckCommand.ExecuteAsync(null);

        Assert.Contains("synthetic truck", vehicle.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truck_that_will_not_say_offers_the_box_instead()
    {
        var vehicle = Make(probe: Silent);

        await vehicle.ReadFromTruckCommand.ExecuteAsync(null);

        Assert.Equal("", vehicle.VinText);
        Assert.True(vehicle.StatusIsProblem);
        Assert.Contains("type it instead", vehicle.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_typed_vin_is_checked_as_it_is_typed()
    {
        var vehicle = Make();

        vehicle.VinText = "1FTEW1EP2";
        Assert.False(vehicle.LookUpCommand.CanExecute(null));
        Assert.Contains("9 so far", vehicle.VinMessage, StringComparison.Ordinal);

        vehicle.VinText = "1ftew1ep3kf000000";   // lower case, wrong check digit
        Assert.True(vehicle.LookUpCommand.CanExecute(null));
        Assert.True(vehicle.VinIsSuspect);
        Assert.Contains("should be 2", vehicle.VinMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Looking_up_fills_the_fields_caches_the_answer_and_picks_the_pack()
    {
        var decoder = new FakeDecoder(new VinDecodeResult(Decoded, null));
        var vehicle = Make(decoder);
        vehicle.VinText = SyntheticVin;

        await vehicle.LookUpCommand.ExecuteAsync(null);

        Assert.Equal([SyntheticVin], decoder.Asked);
        Assert.Equal("2019", vehicle.YearText);
        Assert.Equal("2.7", vehicle.DisplacementText);
        Assert.Equal("TURBO — YES", vehicle.TurboLabel);
        Assert.Contains("Ford F-150 2.7 EcoBoost", vehicle.PackText, StringComparison.Ordinal);
        Assert.Contains("next launch", vehicle.PackText, StringComparison.Ordinal);
        Assert.True(vehicle.RestartNeeded);

        // Cached: a fresh start reads it back without asking anyone.
        Assert.Equal(Decoded, new VehicleIdentityStore(FilePath).Identity);
    }

    [Fact]
    public async Task A_failed_lookup_changes_nothing()
    {
        var vehicle = Make(new FakeDecoder(new VinDecodeResult(null, "couldn't reach NHTSA's decoder")));
        vehicle.VinText = SyntheticVin;

        await vehicle.LookUpCommand.ExecuteAsync(null);

        Assert.True(vehicle.StatusIsProblem);
        Assert.Contains("Nothing was changed", vehicle.Status, StringComparison.Ordinal);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task A_correction_by_hand_is_kept_and_says_so()
    {
        var vehicle = Make();
        vehicle.VinText = SyntheticVin;
        await vehicle.LookUpCommand.ExecuteAsync(null);

        vehicle.Trim = "XLT";

        var stored = new VehicleIdentityStore(FilePath).Identity;
        Assert.Equal("XLT", stored.Trim);
        Assert.Equal("NHTSA vPIC + your corrections", stored.Source);
        Assert.Equal(SyntheticVin, stored.Vin);
    }

    [Fact]
    public void A_vehicle_can_be_entered_entirely_by_hand()
    {
        var vehicle = Make();

        vehicle.Make = "Ford";
        vehicle.Model = "F150";
        vehicle.YearText = "2019";
        vehicle.DisplacementText = "2.7";

        var stored = new VehicleIdentityStore(FilePath).Identity;
        Assert.Equal("Entered by hand", stored.Source);
        Assert.Null(stored.Vin);
        Assert.Contains("Ford F-150 2.7 EcoBoost", vehicle.PackText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_that_does_not_parse_is_refused_not_saved()
    {
        var vehicle = Make();
        vehicle.Make = "Ford";

        vehicle.DisplacementText = "two point seven";

        Assert.Contains("litres", vehicle.FieldProblem, StringComparison.Ordinal);
        Assert.Null(new VehicleIdentityStore(FilePath).Identity.DisplacementLitres);
    }

    [Fact]
    public async Task Forget_deletes_the_vin_from_the_tablet()
    {
        var vehicle = Make();
        vehicle.VinText = SyntheticVin;
        await vehicle.LookUpCommand.ExecuteAsync(null);

        vehicle.ForgetCommand.Execute(null);

        Assert.False(File.Exists(FilePath));
        Assert.Equal("", vehicle.VinText);
        Assert.Equal("", vehicle.Make);
        Assert.False(vehicle.RestartNeeded);
    }

    [Fact]
    public void An_unknown_vehicle_runs_the_standard_set_and_says_nothing_about_packs()
    {
        var vehicle = Make();

        Assert.Equal("", vehicle.PackText);
        Assert.StartsWith("No vehicle set", vehicle.Summary, StringComparison.Ordinal);
    }
}
