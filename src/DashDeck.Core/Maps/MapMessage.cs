namespace DashDeck.Core.Maps;

/// <summary>What the map page is telling the dash.</summary>
public enum MapSignal
{
    /// <summary>Nothing recognisable. Treated as no message at all.</summary>
    Unknown,

    /// <summary>The style loaded and the first frame drew.</summary>
    Ready,

    /// <summary>It could not load, and why — almost always no signal.</summary>
    Failed,

    /// <summary>Where the map is now looking.</summary>
    Camera,

    /// <summary>The map was panned by hand, so it is no longer following the vehicle.</summary>
    Freed,

    /// <summary>Directions were asked for, from the popup on a searched place.</summary>
    Directions,
}

/// <summary>
/// One message from the map page, parsed.
/// </summary>
/// <remarks>
/// The page talks to the dash over <c>postMessage</c> strings, the same channel the web occupant
/// uses for its DRM probe. Parsing is here, in Core, because a string protocol between two
/// languages is exactly the thing that breaks silently — the page keeps working, the dash just
/// stops hearing it, and nothing anywhere says so. Tested instead.
/// </remarks>
/// <param name="Signal">What happened.</param>
/// <param name="Camera">Where the map is looking, for <see cref="MapSignal.Camera"/>.</param>
/// <param name="Detail">The reason, for <see cref="MapSignal.Failed"/>.</param>
public readonly record struct MapMessage(MapSignal Signal, MapCamera? Camera = null, string? Detail = null)
{
    /// <summary>Every message the page sends carries this, so nothing else on the channel is read as one.</summary>
    public const string Prefix = "map:";

    /// <summary>Read one message. False for anything that is not one of ours.</summary>
    public static bool TryParse(string? text, out MapMessage message)
    {
        message = default;

        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = text[Prefix.Length..];

        if (body.Equals("ready", StringComparison.Ordinal))
        {
            message = new MapMessage(MapSignal.Ready);
            return true;
        }

        if (body.Equals("free", StringComparison.Ordinal))
        {
            message = new MapMessage(MapSignal.Freed);
            return true;
        }

        if (body.Equals("directions", StringComparison.Ordinal))
        {
            message = new MapMessage(MapSignal.Directions);
            return true;
        }

        if (body.StartsWith("camera:", StringComparison.Ordinal))
        {
            // A camera that will not parse is dropped rather than reported as a zeroed one,
            // which would silently move the map to the Gulf of Guinea.
            if (!MapCamera.TryParse(body["camera:".Length..], out var camera))
            {
                return false;
            }

            message = new MapMessage(MapSignal.Camera, camera);
            return true;
        }

        if (body.StartsWith("error:", StringComparison.Ordinal))
        {
            var detail = body["error:".Length..].Trim();

            message = new MapMessage(
                MapSignal.Failed,
                Detail: detail.Length == 0 ? "the map did not load" : detail);

            return true;
        }

        return false;
    }
}
