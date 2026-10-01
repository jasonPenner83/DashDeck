using DashDeck.Core.Identity;

namespace DashDeck.Host.Settings;

/// <summary>
/// The decoded vehicle, cached on the tablet (ADR-0033).
/// </summary>
/// <remarks>
/// <c>vehicle.json</c> in <c>%LOCALAPPDATA%\DashDeck\</c>, beside the other settings: the decoder
/// is asked once, and the truck needs no signal afterwards. It holds the VIN, which is why it
/// lives here and nowhere else — never in the repository, never in a log, never on the
/// component profile. FORGET deletes it.
/// <para>Nothing here throws, for the reason <see cref="JsonFile"/> gives.</para>
/// </remarks>
public sealed class VehicleIdentityStore
{
    public VehicleIdentityStore()
        : this(JsonFile.InLocalAppData("vehicle.json"))
    {
    }

    public VehicleIdentityStore(string path)
    {
        Path = path;
        Identity = JsonFile.Load<VehicleIdentity>(path, out var error) ?? VehicleIdentity.Unknown;
        LastError = error;
    }

    /// <summary>Where the file is. Shown in Settings.</summary>
    public string Path { get; }

    /// <summary>What is known about the vehicle; <see cref="VehicleIdentity.Unknown"/> when nothing is.</summary>
    public VehicleIdentity Identity { get; private set; }

    /// <summary>Why the last load or save failed, if it did.</summary>
    public string? LastError { get; private set; }

    public void Save(VehicleIdentity identity)
    {
        JsonFile.Save(Path, identity, out var error);
        LastError = error;

        if (error is null)
        {
            Identity = identity;
        }
    }

    /// <summary>Delete the file: the VIN and everything decoded from it.</summary>
    public void Forget()
    {
        try
        {
            System.IO.File.Delete(Path);
            Identity = VehicleIdentity.Unknown;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
