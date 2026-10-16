using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// The MAPS destination: where the map is drawn, and — for now — a placeholder in its place.
/// </summary>
/// <remarks>
/// The map itself is <see href="../../docs/decisions/ADR-0058-offline-map-and-routing.md">ADR-0058</see>:
/// MapLibre GL JS in WebView2 over a PMTiles archive, with GraphHopper beside it for routes.
/// None of that is built. What is built is the <b>geometry</b> — three placements and the way
/// between them — because that is the part the rest of the shell has to agree with, and
/// retrofitting a second size onto a destination later means touching the band arithmetic, the
/// stage handoff and the nav all at once.
/// <para>
/// So this view-model is real and the picture inside it is honestly a placeholder. The position
/// readout is real too: the phone's GPS is proven on the truck (Q38), and the one thing worth
/// knowing before a map is drawn is whether there is a position to centre it on.
/// </para>
/// </remarks>
public sealed partial class MapViewModel : ObservableObject
{
    private readonly ILocationSource? _location;
    private readonly IClock _clock;

    /// <param name="location">
    /// The running location source, or null when GPS is off. Shared with Settings' readout rather
    /// than opened again — one serial port, one reader (ADR-0027).
    /// </param>
    /// <param name="clock">Injected, so the age of a fix is testable and replay stays honest.</param>
    public MapViewModel(ILocationSource? location = null, IClock? clock = null)
    {
        _location = location;
        _clock = clock ?? new SystemClock();
        Refresh();
    }

    /// <summary>Where the map is, and the size it returns to off the stage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanel))]
    [NotifyPropertyChangedFor(nameof(IsFull))]
    [NotifyPropertyChangedFor(nameof(IsOnStage))]
    [NotifyPropertyChangedFor(nameof(ResizeCaption))]
    [NotifyPropertyChangedFor(nameof(ResizeHint))]
    private MapView _view = MapView.Default;

    /// <summary>Raised after the placement changes, so the shell can move the map.</summary>
    /// <remarks>
    /// An event rather than the shell reaching in: the placement is this view-model's state, and
    /// what it costs — collapsing the stage, making an occupant — is the shell's business.
    /// </remarks>
    public event Action<MapView>? PlacementChanged;

    /// <summary>True while the map draws in the bottom third.</summary>
    public bool IsPanel => View.IsPanel;

    /// <summary>True while the map covers all six bands.</summary>
    public bool IsFull => View.IsFull;

    /// <summary>True while the map is a stage occupant.</summary>
    public bool IsOnStage => View.IsOnStage;

    /// <summary>The size a resize would give you, not the one you are in.</summary>
    public string ResizeCaption => View.ResizeCaption;

    /// <summary>
    /// How the size is changed, written on the panel.
    /// </summary>
    /// <remarks>
    /// The size control is a second press of the nav's MAPS cell (ADR-0062's addendum), and a
    /// gesture with nothing on screen to say so is a control nobody finds. So the panel says it,
    /// and names the size the press would give rather than the one you are in.
    /// </remarks>
    public string ResizeHint => $"TAP MAPS AGAIN FOR {ResizeCaption}";

    /// <summary>
    /// Swap the two in-nav sizes: bottom third ⟷ all six bands.
    /// </summary>
    /// <remarks>
    /// Invoked by the nav cell rather than by anything in the panel. The chip that used to do it
    /// was in the one place a control should not be — inside the thing being resized, competing
    /// with the cell you had just pressed to get here.
    /// </remarks>
    [RelayCommand]
    private void Resize() => Move(View.Resized());

    /// <summary>Hand the map to the stage, remembering the size to come back to.</summary>
    [RelayCommand]
    private void SendToStage() => Move(View.SentToTheStage());

    /// <summary>Take the map off the stage, back to the size it had before it went.</summary>
    [RelayCommand]
    private void BringBack() => Move(View.BroughtBack());

    /// <summary>
    /// Move the map, and tell the shell — but only on a real change.
    /// </summary>
    /// <remarks>
    /// The guard is load-bearing rather than tidy. Bringing the map back is reported by the
    /// occupant's own disposal as well as by the button, so the same move arrives twice; without
    /// this the second arrival would re-enter the shell's stage code while it was still unwinding
    /// the first.
    /// </remarks>
    private void Move(MapView next)
    {
        if (next == View)
        {
            return;
        }

        View = next;
        PlacementChanged?.Invoke(next);
    }

    /// <summary>Quality and position, in the same words Settings and the probe use.</summary>
    [ObservableProperty]
    private string _positionText = "NO FIX";

    /// <summary>The line under it: how old the fix is, or what to do about there not being one.</summary>
    [ObservableProperty]
    private string _positionDetail = string.Empty;

    /// <summary>
    /// Re-read the position. Called on the shell's one-second beat, and only while MAPS is the
    /// destination — a readout nobody is looking at costs a string a second for nothing.
    /// </summary>
    public void Refresh()
    {
        if (_location is null)
        {
            PositionText = "NO GPS";
            PositionDetail = "Settings ▸ Display turns on the phone's GPS.";
            return;
        }

        if (_location.Latest is not { } fix)
        {
            PositionText = "NO DATA";
            PositionDetail = $"{_location.Name} is open and nothing has arrived yet.";
            return;
        }

        PositionText = LocationStatus.Describe(fix);
        PositionDetail = $"{_location.Name} · {LocationStatus.Age(fix, _clock.UtcNow)}";
    }

    /// <summary>Where the map is and what it would centre on, for <c>--shot</c>.</summary>
    public string Describe() => $"{View.Describe()} · {PositionText}";
}
