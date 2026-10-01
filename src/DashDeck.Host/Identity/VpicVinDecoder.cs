using System.Net.Http;
using DashDeck.Core.Identity;

namespace DashDeck.Host.Identity;

/// <summary>Turns a VIN into what the vehicle is. A seam, so the settings screen is testable offline.</summary>
public interface IVinDecoder
{
    /// <summary>Decode one VIN. Never throws for a network or decoder problem — that is a <see cref="VinDecodeResult.Problem"/>.</summary>
    Task<VinDecodeResult> DecodeAsync(string vin, DateTimeOffset nowUtc, CancellationToken ct);
}

/// <summary>
/// NHTSA's public vPIC decoder (ADR-0033).
/// </summary>
/// <remarks>
/// Called only when a person presses LOOK UP, and the answer is cached — the same posture as
/// the weather fetch: the dash must come up and run with no signal at all. Reading the answer
/// is <see cref="VpicDecoding"/>, in the engine, where it is tested.
/// </remarks>
public sealed class VpicVinDecoder : IVinDecoder
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<VinDecodeResult> DecodeAsync(string vin, DateTimeOffset nowUtc, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync(VpicDecoding.RequestUri(vin), ct).ConfigureAwait(false);
            return VpicDecoding.Parse(json, vin, nowUtc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new VinDecodeResult(null, "couldn't reach NHTSA's decoder — check the connection and try again");
        }
    }
}
