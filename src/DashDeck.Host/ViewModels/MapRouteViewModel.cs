using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Maps;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// Working out the way to a chosen place (ADR-0065's addendum).
/// </summary>
/// <remarks>
/// <b>This is not the navigation ADR-0058 describes.</b> That is a routing engine bundled in the
/// deploy so guidance survives a dead zone, and it is still ahead. This answers how far, how long
/// and which way, once, while stopped — through a public OSRM, on the same terms as everything
/// else the map leans on.
/// <para>
/// Its own view-model for the same reason search has one: almost none of it is about where the map
/// is drawn, and keeping it free of WPF means the politeness, the parsing and every state it can
/// be in are tested without Windows.
/// </para>
/// </remarks>
public sealed partial class MapRouteViewModel : ObservableObject
{
    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly IClock _clock;
    private readonly Func<MapCamera?> _from;

    private DateTimeOffset? _lastAsked;
    private CancellationTokenSource? _running;

    /// <param name="fetch">Makes the HTTP call. Injected, so this tests without a network.</param>
    /// <param name="clock">Injected, for the rate limit.</param>
    /// <param name="from">Where the truck is. A route from nowhere is not a route.</param>
    public MapRouteViewModel(
        Func<string, CancellationToken, Task<string>> fetch,
        IClock clock,
        Func<MapCamera?> from)
    {
        _fetch = fetch;
        _clock = clock;
        _from = from;
    }

    /// <summary>The way there, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoute))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private Route? _route;

    /// <summary>True when there is a route to show.</summary>
    public bool HasRoute => Route is not null;

    /// <summary>"12.4 km · 18 min", or empty.</summary>
    public string Summary => Route?.Summary ?? string.Empty;

    /// <summary>Where it goes, for the strip above the steps.</summary>
    [ObservableProperty]
    private string _destinationName = string.Empty;

    /// <summary>The instructions, in order.</summary>
    public ObservableCollection<RouteStep> Steps { get; } = [];

    /// <summary>True while a route is being worked out.</summary>
    [ObservableProperty]
    private bool _isRouting;

    /// <summary>What went wrong, in one line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string? _note;

    /// <summary>True when there is something to say about the attempt.</summary>
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    /// <summary>Who to credit, which the router's terms require on screen.</summary>
    public string Attribution => Router.Attribution;

    /// <summary>Raised when a route has been found, so the map can draw it.</summary>
    public event Action<Route>? Found;

    /// <summary>Raised when a route has been given up, so the map can rub it out.</summary>
    public event Action? Cleared;

    /// <summary>
    /// Find the way from where the truck is to <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// One request a second, as the router's terms ask — and not cached, deliberately: a route
    /// depends on where the truck is now, so an answer from five minutes and two streets ago is
    /// not the same answer. Searching is cached; routing is not.
    /// </remarks>
    public async Task FindAsync(MapCamera to, string name)
    {
        if (_from() is not { } from || !from.IsUsable)
        {
            Note = "No position — the route has to start somewhere.";
            return;
        }

        var now = _clock.UtcNow;

        if (!AddressQuery.MayAsk(now, _lastAsked))
        {
            Note = "Just a moment — one route a second.";
            return;
        }

        _running?.Cancel();
        _running = new CancellationTokenSource();
        var token = _running.Token;

        _lastAsked = now;
        IsRouting = true;
        Note = null;
        DestinationName = name;

        try
        {
            var json = await _fetch(Router.Url(from, to), token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (Router.Parse(json) is not { } found)
            {
                Clear();
                Note = "No way there that the router could find.";
                return;
            }

            Route = found;
            Steps.Clear();

            foreach (var step in found.Steps)
            {
                Steps.Add(step);
            }

            Found?.Invoke(found);
        }
        catch (OperationCanceledException)
        {
            // Replaced by another request. Not a failure.
        }
        catch (Exception)
        {
            Clear();
            Note = "No answer — directions need a connection.";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsRouting = false;
            }
        }
    }

    /// <summary>Throw the route away.</summary>
    [RelayCommand]
    private void Clear()
    {
        Route = null;
        Steps.Clear();
        DestinationName = string.Empty;
        Note = null;
        Cleared?.Invoke();
    }

    /// <summary>What the routing is doing, for <c>--shot</c>.</summary>
    public string Describe() =>
        HasRoute
            ? $"route to \"{DestinationName}\" {Summary} steps={Steps.Count}"
            : IsRouting ? "routing" : Note is { } note ? $"route: {note}" : "no route";
}
