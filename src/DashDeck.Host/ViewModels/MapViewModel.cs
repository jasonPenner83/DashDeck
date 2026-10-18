using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Location;
using DashDeck.Core.Maps;
using DashDeck.Host.Maps;

namespace DashDeck.Host.ViewModels;

/// <summary>What the MAPS region is showing right now.</summary>
public enum MapPicture
{
    /// <summary>GPS is off, so there is no position to centre on and no map is built.</summary>
    NoGps,

    /// <summary>A source is open and no fix has arrived yet.</summary>
    WaitingForFix,

    /// <summary>There is a position and the page is still fetching its style and tiles.</summary>
    Loading,

    /// <summary>A map, on screen.</summary>
    Drawn,

    /// <summary>It did not load. Almost always no signal.</summary>
    Failed,
}

/// <summary>
/// The MAPS destination: where the map is drawn, where it is looking, and whether there is one.
/// </summary>
/// <remarks>
/// Three placements (<see cref="MapView"/>, ADR-0062) and the map itself — MapLibre on tiles
/// fetched as you drive (ADR-0063). This holds everything that has to outlive the browser
/// control, because moving the map between placements rebuilds it (Q43): the camera, whether it
/// is following the vehicle, and what to say when there is no map to show.
/// <para>
/// It deliberately knows nothing about WPF or WebView2 — the live surface arrives as an
/// <see cref="IMapSurface"/> — so the one part of this feature that can be tested without
/// Windows, a browser and a signal stays testable.
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
    /// <param name="styleUrl">
    /// A <c>--map-style</c> override. Validated rather than trusted (<see cref="MapSource.StyleOr"/>):
    /// it is a URL off the command line going into a browser control, and an unusable one falls
    /// back to the dark default rather than leaving the dash with no map.
    /// </param>
    /// <param name="search">
    /// Address search (ADR-0065), or null where there is none — which is how every test that does
    /// not care about searching constructs this.
    /// </param>
    /// <param name="directions">Routing (ADR-0065's addendum), or null where there is none.</param>
    public MapViewModel(
        ILocationSource? location = null,
        IClock? clock = null,
        string? styleUrl = null,
        MapSearchViewModel? search = null,
        MapRouteViewModel? directions = null)
    {
        _location = location;
        _clock = clock ?? new SystemClock();
        StyleOverride = MapSource.IsAcceptableStyle(styleUrl) ? styleUrl!.Trim() : null;
        Search = search;
        Directions = directions;

        if (Search is not null)
        {
            Search.Picked += GoTo;
        }

        if (Directions is not null)
        {
            Directions.Found += found => Surface?.Route(found);
            Directions.Cleared += () => Surface?.ClearRoute();
        }

        Refresh();
    }

    /// <summary>Address search, or null when the dash was built without it.</summary>
    public MapSearchViewModel? Search { get; }

    /// <summary>
    /// Directions to a chosen place, or null when the dash was built without them.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Search"/> because they fail separately: a place can be found with
    /// no way there, and a route can be asked for from a recent address with no search at all.
    /// </remarks>
    public MapRouteViewModel? Directions { get; }

    /// <summary>The place the pin is on, or null.</summary>
    /// <remarks>Kept so DIRECTIONS TO, which arrives from inside the page, knows where to.</remarks>
    public GeocodeResult? Destination { get; private set; }

    /// <summary>True when there is a search to offer.</summary>
    public bool IsSearchOffered => Search is not null;

    /// <summary>
    /// True while the search is covering the map.
    /// </summary>
    /// <remarks>
    /// It covers rather than overlays, and has to: a WebView2 is a child window, so WPF cannot
    /// draw a keyboard on top of it (ADR-0021). The map is still there, still following, still
    /// fetching nothing it did not already want — it is simply behind the search until it closes.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMapShowing))]
    private bool _isSearchOpen;

    /// <summary>
    /// Open the search.
    /// </summary>
    /// <remarks>
    /// It takes the map full screen on the way in. Five rows of keys, a field and a result list do
    /// not fit the bottom third, and the placement already exists (ADR-0062) — so searching is the
    /// first thing that uses a placement change for a reason other than taste.
    /// </remarks>
    [RelayCommand]
    private void OpenSearch()
    {
        if (Search is null)
        {
            return;
        }

        // Off the stage first, then full screen. From the stage the nav's region shows only the
        // "map is on the stage" notice, so a search opened there would have nowhere to draw.
        if (View.IsOnStage)
        {
            Move(View.BroughtBack());
        }

        if (View.IsPanel)
        {
            Move(View.Resized());
        }

        Search.Refresh();
        IsStepsOpen = false;
        IsSearchOpen = true;
    }

    /// <summary>Close the search and leave the map where it is.</summary>
    [RelayCommand]
    private void CloseSearch() => IsSearchOpen = false;

    /// <summary>
    /// True while the turn list is covering the map.
    /// </summary>
    /// <remarks>
    /// Covering, like the search, and for the same reason — a list drawn over a child window
    /// would be behind it (ADR-0021). The route stays on the map underneath either way.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMapShowing))]
    private bool _isStepsOpen;

    /// <summary>Show or hide the turn list.</summary>
    [RelayCommand]
    private void ToggleSteps()
    {
        if (Directions?.HasRoute != true)
        {
            return;
        }

        IsStepsOpen = !IsStepsOpen;
    }

    /// <summary>
    /// Go to a chosen address: move there, drop a pin, stop following and close the search.
    /// </summary>
    /// <remarks>
    /// Following is switched off deliberately. You have just said where you want to look, and a
    /// map that slid straight back to the truck would undo it — RECENTRE is how you return.
    /// </remarks>
    private void GoTo(GeocodeResult result)
    {
        Camera = result.Camera;
        Destination = result;
        IsFollowing = false;
        IsSearchOpen = false;

        // A new place means the old route is about somewhere else. Cleared before the pin moves,
        // so there is never a line on screen going to a pin that is no longer there.
        Directions?.ClearCommand.Execute(null);

        Surface?.Destination(result.Camera, result);
        OnPropertyChanged(nameof(Picture));
        OnPropertyChanged(nameof(WantsSurface));
    }

    /// <summary>
    /// Directions were asked for, from the button inside the popup.
    /// </summary>
    /// <remarks>
    /// The request comes from the page rather than from a WPF control, because the popup is in the
    /// page — a WebView2 is a child window and nothing WPF drew over the map would be visible
    /// (ADR-0021). So the bridge carries it, and this is where it lands.
    /// </remarks>
    private void AskForDirections()
    {
        if (Directions is null || Destination is not { } place)
        {
            return;
        }

        // Fire and forget: the beat and the page both carry on, and the view-model reports its own
        // progress. Awaiting here would mean a message handler that blocks the bridge.
        _ = Directions.FindAsync(place.Camera, place.Label);
    }

    /// <summary>
    /// A <c>--map-style</c> URL that wins over the theme's own, or null.
    /// </summary>
    /// <remarks>
    /// The escape hatch, and the way to see OpenFreeMap's five hosted looks without a rebuild —
    /// which is also the known-good comparison for a generated style that looks wrong.
    /// </remarks>
    public string? StyleOverride { get; }

    /// <summary>
    /// The style built from the theme's colours (ADR-0064), or null while the override is in force.
    /// </summary>
    /// <remarks>
    /// Rebuilt whenever a palette is applied — a theme worn, an accent picked, dusk falling — and
    /// handed to the live map, so the map turns with the rest of the dash instead of staying dark
    /// grey under an LCARS console.
    /// </remarks>
    public string? StyleJson { get; private set; }

    /// <summary>
    /// How fast the truck is going, or null when nothing trustworthy says.
    /// </summary>
    /// <remarks>
    /// For the search's speed gate (ADR-0058). From the phone's GPS rather than the adapter,
    /// because the map already has it and a destination typed with no GPS has nothing to centre
    /// on anyway. <c>NaN</c> is unknown, not nought — a stale fix carries no speed.
    /// </remarks>
    /// <summary>
    /// Where the truck is, as a camera — which is where a route starts.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Camera"/>: that is where the map is <em>looking</em>, which after a search is
    /// the place you searched for. A route from there would be a route from nowhere you have been.
    /// </remarks>
    public MapCamera? Here =>
        _location?.Latest is { } fix && fix.HasFix ? MapCamera.Over(fix) : null;

    public double? SpeedKmh =>
        _location?.Latest is { } fix && double.IsFinite(fix.GroundSpeedKmh) ? fix.GroundSpeedKmh : null;

    /// <summary>The colour the vehicle marker is drawn in, from the theme.</summary>
    public string MarkerColour { get; private set; } = MapPalette.Default.Marker;

    private MapPalette _palette = MapPalette.Default;

    /// <summary>What went wrong with the theme's style, if the hosted one had to be used instead.</summary>
    /// <remarks>
    /// Carried into <see cref="Describe"/> rather than onto the screen. The generated style is
    /// tested, so this is a bug rather than a state — but a map that silently stopped following
    /// the theme would be a puzzle, and this is the sentence that solves it.
    /// </remarks>
    public string? StyleNote { get; private set; }

    /// <summary>
    /// Take a palette: build the style, keep the marker colour, and restyle a map already drawn.
    /// </summary>
    /// <remarks>
    /// The marker is taken even under a <c>--map-style</c> override, because the vehicle is ours
    /// to draw whoever supplies the cartography underneath it.
    /// </remarks>
    internal void Wear(MapPalette palette)
    {
        _palette = palette;
        MarkerColour = palette.Marker;

        if (StyleOverride is null)
        {
            var built = MapStyle.Build(palette);

            if (MapStyle.IsWellFormed(built))
            {
                StyleJson = built;
                StyleNote = null;
            }
            else
            {
                // Never hand a malformed style to the renderer: it draws a dark rectangle and
                // reports nothing, which is indistinguishable from no signal.
                StyleJson = null;
                StyleNote = "the theme's map style would not build — using the hosted dark style";
            }
        }

        Surface?.Restyle(StyleJson, palette);
    }

    // ---- Where the map is drawn (ADR-0062) ----

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

    /// <summary>The size a resize would give, not the one you are in.</summary>
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

    // ---- The map itself (ADR-0063) ----

    /// <summary>
    /// The live map, while there is one. Set by the surface as it is built and cleared as it goes.
    /// </summary>
    /// <remarks>
    /// Internal because it is the view half of the arrangement: the panel owns a surface for as
    /// long as it is on screen and registers it here, so the one-second beat has something to
    /// push a position into.
    /// </remarks>
    internal IMapSurface? Surface { get; private set; }

    /// <summary>Take a freshly built surface, and give it back the camera and follow state.</summary>
    internal void Attach(IMapSurface surface)
    {
        Surface = surface;
        surface.Follow(IsFollowing);
        surface.Restyle(StyleJson, _palette);

        // A rebuilt map starts empty, so anything that was on the old one goes back: the pin, its
        // popup, and the route. Without this, moving the map between placements would quietly
        // lose the destination you had just looked up.
        if (Destination is { } place)
        {
            surface.Destination(place.Camera, place);
        }

        if (Directions?.Route is { } route)
        {
            surface.Route(route);
        }

        Push();
    }

    /// <summary>Let go of a surface that is going away — unless a newer one already replaced it.</summary>
    internal void Detach(IMapSurface surface)
    {
        if (ReferenceEquals(Surface, surface))
        {
            Surface = null;
            IsReady = false;
        }
    }

    /// <summary>
    /// Where the map is looking, kept outside the page so it survives being rebuilt.
    /// </summary>
    /// <remarks>
    /// Null until there is something to centre on. <b>Nothing invents a centre</b> — no home
    /// coordinates, no city hard-coded in the source (ADR-0052's posture, and a repository rule):
    /// a map with no position says so instead.
    /// </remarks>
    public MapCamera? Camera { get; private set; }

    /// <summary>True once the page has reported a drawn map.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Picture))]
    [NotifyPropertyChangedFor(nameof(IsMapDrawn))]
    [NotifyPropertyChangedFor(nameof(PlaceholderTitle))]
    [NotifyPropertyChangedFor(nameof(PlaceholderDetail))]
    private bool _isReady;

    /// <summary>Why there is no map, when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Picture))]
    [NotifyPropertyChangedFor(nameof(IsMapDrawn))]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    [NotifyPropertyChangedFor(nameof(WantsSurface))]
    [NotifyPropertyChangedFor(nameof(PlaceholderTitle))]
    [NotifyPropertyChangedFor(nameof(PlaceholderDetail))]
    private string? _failure;

    /// <summary>
    /// Whether the map is following the vehicle.
    /// </summary>
    /// <remarks>
    /// On by default and switched off by panning, which is the only arrangement that works in a
    /// moving vehicle: a map that fought your pan would be useless, and one that never came back
    /// would lose you for good. RECENTRE is how it comes back.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecentreOffered))]
    private bool _isFollowing = true;

    /// <summary>True when the map has been panned away and can be put back on the vehicle.</summary>
    public bool IsRecentreOffered => !IsFollowing;

    /// <summary>Put the map back on the vehicle and keep it there.</summary>
    [RelayCommand]
    private void Recentre()
    {
        IsFollowing = true;
        Surface?.Follow(true);
    }

    /// <summary>Raised when the surface should be thrown away and built again.</summary>
    public event Action? ReloadRequested;

    /// <summary>
    /// Try again after a failure — almost always after the signal came back.
    /// </summary>
    /// <remarks>
    /// A rebuild rather than a reload of the page, because the failure may have been the browser
    /// control itself rather than the style: retrying only the navigation would leave the one
    /// case that cannot recover looking like the one that can.
    /// </remarks>
    [RelayCommand]
    private void Retry()
    {
        Failure = null;
        IsReady = false;
        ReloadRequested?.Invoke();
    }

    /// <summary>True when the map failed to load and a retry is worth offering.</summary>
    public bool HasFailed => Failure is not null;

    /// <summary>
    /// What the page has told us.
    /// </summary>
    /// <remarks>
    /// The page's own errors come through here rather than being swallowed, because every failure
    /// in a browser control looks identical on screen: a dark rectangle. On this dash that has to
    /// carry a reason.
    /// </remarks>
    internal void OnMessage(MapMessage message)
    {
        switch (message.Signal)
        {
            case MapSignal.Ready:
                Failure = null;
                IsReady = true;

                // The page opened on whatever camera it was given; anything learned since — a
                // first fix that arrived while it was loading — goes in now.
                Push();
                break;

            case MapSignal.Failed:
                IsReady = false;
                Failure = message.Detail ?? "the map did not load";
                break;

            case MapSignal.Camera when message.Camera is { } camera:
                // The page is the authority on where it is looking: it reports after every pan,
                // zoom and recentre, and this is what survives the rebuild.
                Camera = camera;
                break;

            case MapSignal.Freed:
                IsFollowing = false;
                break;

            case MapSignal.Directions:
                AskForDirections();
                break;
        }
    }

    /// <summary>Which of the five things the region is showing.</summary>
    public MapPicture Picture =>
        Failure is not null ? MapPicture.Failed
        : _location is null ? MapPicture.NoGps
        : IsReady ? MapPicture.Drawn
        : Camera is null ? MapPicture.WaitingForFix
        : MapPicture.Loading;

    /// <summary>True when a map is on screen, so the placeholder gets out of the way.</summary>
    public bool IsMapDrawn => Picture is MapPicture.Drawn;

    /// <summary>
    /// Whether the map's own region is the thing being shown, rather than something over it.
    /// </summary>
    public bool IsMapShowing => !IsSearchOpen && !IsStepsOpen;

    /// <summary>
    /// Whether a surface is worth building.
    /// </summary>
    /// <remarks>
    /// Not until there is somewhere to point it. A map opened with no camera would have to invent
    /// a centre, and the two honest options — the middle of the ocean, or somebody's house in the
    /// source code — are both worse than saying there is no position yet.
    /// </remarks>
    public bool WantsSurface => Camera is not null && Failure is null;

    /// <summary>The heading on the placeholder, when there is no map.</summary>
    public string PlaceholderTitle => Picture switch
    {
        MapPicture.NoGps => "No GPS",
        MapPicture.WaitingForFix => "Waiting for a position",
        MapPicture.Loading => "Loading the map",
        MapPicture.Failed => "No map",
        _ => string.Empty,
    };

    /// <summary>The line under it: what to do, or what went wrong.</summary>
    public string PlaceholderDetail => Picture switch
    {
        MapPicture.NoGps => "Settings ▸ Display turns on the phone's GPS. A map needs a position to centre on.",
        MapPicture.WaitingForFix => $"{_location?.Name} is open and nothing has arrived yet.",
        MapPicture.Loading => $"Fetching tiles · {MapSource.Attribution}",
        MapPicture.Failed => $"{Failure} — the tiles come over the truck's connection, so this is usually signal.",
        _ => string.Empty,
    };

    // ---- The position, and the readout under it ----

    /// <summary>Quality and position, in the same words Settings and the probe use.</summary>
    [ObservableProperty]
    private string _positionText = "NO FIX";

    /// <summary>The line under it: how old the fix is, or what to do about there not being one.</summary>
    [ObservableProperty]
    private string _positionDetail = string.Empty;

    /// <summary>
    /// Re-read the position, and give it to the map. Called on the shell's one-second beat, and
    /// only while the map is on screen — a readout nobody is looking at costs a string a second
    /// for nothing, and a map nobody is looking at should not be fetching tiles.
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

        // The speed gate is read rather than latched, so stopping unlocks the keyboard and
        // pulling away locks it again without anything else having to happen.
        Search?.Refresh();

        // The first usable fix is what decides the map can be built at all, and where it opens.
        if (Camera is null && fix.HasFix)
        {
            Camera = MapCamera.Over(fix);
            OnPropertyChanged(nameof(Picture));
            OnPropertyChanged(nameof(PlaceholderTitle));
            OnPropertyChanged(nameof(PlaceholderDetail));
            OnPropertyChanged(nameof(WantsSurface));
        }

        Push();
    }

    /// <summary>Hand the current fix to the map, if there is a map and anything to draw.</summary>
    /// <remarks>
    /// Deliberately a wider rule than <see cref="LocationFix.HasFix"/>, which is the dash's
    /// "worth drawing a number from" test and excludes Stale. A map is different: when the link
    /// drops, the last known position is still the most useful thing on screen — what must not
    /// happen is it sitting there in full colour looking current. So a stale fix is pushed with
    /// <c>live: false</c> and the marker drains of colour where it stands.
    /// <para>
    /// Only the opening camera insists on <see cref="LocationFix.HasFix"/>: where the map first
    /// centres is a decision made once, and it should be made on a position worth trusting.
    /// </para>
    /// </remarks>
    private void Push()
    {
        if (Surface is { } surface && _location?.Latest is { } fix && Drawable(fix))
        {
            surface.Position(fix, fix.HasFix);
        }
    }

    /// <summary>
    /// Whether a fix has a position the map can draw at all.
    /// </summary>
    /// <remarks>
    /// Unavailable means the hold has ended and the coordinates are no longer claimed to mean
    /// anything (ADR-0027's addendum), so nothing is sent — the marker stays where it last was,
    /// already grey from the Stale readings that preceded it.
    /// </remarks>
    private static bool Drawable(LocationFix fix) =>
        fix.Quality is not SignalQuality.Unavailable
        && double.IsFinite(fix.Latitude) && double.IsFinite(fix.Longitude);

    /// <summary>Where the map is, what it is showing and what it would centre on, for <c>--shot</c>.</summary>
    /// <remarks>
    /// Carries the style note when there is one, because that is the only place it surfaces: a map
    /// that quietly stopped following the theme is otherwise just a map in the wrong colours.
    /// </remarks>
    public string Describe() =>
        $"{View.Describe()} · {Picture.ToString().ToLowerInvariant()}"
        + (IsSearchOpen && Search is { } open ? $" · search: {open.Describe()}" : string.Empty)
        + (Directions is { } going && (going.HasRoute || going.HasNote) ? $" · {going.Describe()}" : string.Empty)
        + (Camera is { } c ? $" · {c.Describe()}" : string.Empty)
        + (StyleOverride is { } url ? $" · style: {url}" : string.Empty)
        + (StyleNote is { } note ? $" · style: {note}" : string.Empty)
        + $" · {PositionText}";
}
