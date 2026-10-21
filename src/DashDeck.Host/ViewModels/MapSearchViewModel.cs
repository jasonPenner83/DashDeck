using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Core.Maps;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// Searching for an address (ADR-0065): the query, the keyboard it is typed on, and what came back.
/// </summary>
/// <remarks>
/// Its own view-model rather than more of <see cref="MapViewModel"/>, because almost none of it is
/// about where the map is drawn: it is a text field, a keyboard, a list and somebody else's HTTP
/// endpoint used under a usage policy.
/// <para>
/// <b>No auto-complete.</b> Nominatim's policy forbids it outright, so nothing here searches while
/// typing — <see cref="SearchCommand"/> is the only thing that asks, and the rate limit and the
/// cache are checked even then.
/// </para>
/// <para>
/// Free of WPF on purpose, like the rest of the map's view-models, so the whole of it is tested
/// without Windows: the gate, the politeness, the keyboard and every state the panel can be in.
/// </para>
/// </remarks>
public sealed partial class MapSearchViewModel : ObservableObject
{
    private readonly Func<string, CancellationToken, Task<string>> _fetch;
    private readonly IClock _clock;
    private readonly Func<MapCamera?> _where;
    private readonly Func<double?> _speed;

    /// <summary>
    /// Answers already had, by normalised query.
    /// </summary>
    /// <remarks>
    /// The policy asks for caching, and it is the right thing anyway: searching the same address
    /// twice is what anyone does when the first result was not the one they meant, and the second
    /// look should cost nothing and work with the signal gone.
    /// </remarks>
    private readonly Dictionary<string, IReadOnlyList<GeocodeResult>> _cache = new(StringComparer.Ordinal);

    private DateTimeOffset? _lastAsked;
    private CancellationTokenSource? _running;

    /// <param name="fetch">Makes the HTTP call. Injected, so the politeness and the parsing test without a network.</param>
    /// <param name="clock">Injected, for the rate limit.</param>
    /// <param name="where">Where the truck is, to bias the search. May return null.</param>
    /// <param name="speed">How fast it is going, for the gate. May return null when unknown.</param>
    /// <param name="whileMoving">
    /// The Settings switch that lifts the gate (ADR-0072), read every time rather than latched, so
    /// turning it on unlocks the field at the next beat. Null is off.
    /// </param>
    public MapSearchViewModel(
        Func<string, CancellationToken, Task<string>> fetch,
        IClock clock,
        Func<MapCamera?> where,
        Func<double?> speed,
        Func<bool>? whileMoving = null)
    {
        _fetch = fetch;
        _clock = clock;
        _where = where;
        _speed = speed;
        _whileMoving = whileMoving ?? (() => false);
    }

    private readonly Func<bool> _whileMoving;

    /// <summary>
    /// What has been typed.
    /// </summary>
    /// <remarks>
    /// Written by the text field as well as read, so it is two-way — the field's own
    /// <c>MaxLength</c> keeps it inside <see cref="AddressQuery.MaxLength"/>, and a stale message
    /// is cleared when it changes, because a complaint about the last query hanging over the next
    /// one reads as a broken field.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery))]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    private string _query = string.Empty;

    /// <summary>True once anything has been typed, so the field stops showing its prompt.</summary>
    public bool HasQuery => Query.Length > 0;

    partial void OnQueryChanged(string value) => Message = null;

    /// <summary>What the field says when nothing has been typed.</summary>
    /// <remarks>
    /// An instance property rather than a static one, because XAML cannot reach a static through
    /// an instance path and the binding would silently show nothing.
    /// </remarks>
    public string Prompt => "Street and town";

    /// <summary>What came back, newest search only.</summary>
    public ObservableCollection<GeocodeResult> Results { get; } = [];

    /// <summary>Addresses picked before, newest first.</summary>
    /// <remarks>
    /// Offered because ADR-0058 put map tap, favourites and recents ahead of search for a reason:
    /// typing an address on a touch screen is the slowest way to say where you are going, and the
    /// place you went last week is one tap. Held for this session only — persisting them is worth
    /// doing and is not this change.
    /// </remarks>
    public ObservableCollection<GeocodeResult> Recent { get; } = [];

    /// <summary>True while a search is in flight.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    private bool _isSearching;

    /// <summary>What went wrong, or what to do, in one line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    /// <summary>True when there is something to say, so the line is not drawn empty.</summary>
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <summary>True when there is something to show in the results list.</summary>
    [ObservableProperty]
    private bool _hasResults;

    /// <summary>
    /// Whether a destination may be typed at all — the speed gate.
    /// </summary>
    /// <remarks>
    /// Read on the beat rather than latched, so stopping unlocks it without anything else
    /// happening, and pulling away locks it again.
    /// </remarks>
    public bool MayType => SearchGate.MayType(_speed(), _whileMoving());

    /// <summary>Why not, when the truck is moving.</summary>
    public string? Barred => SearchGate.WhyNot(_speed(), _whileMoving());

    /// <summary>True when the search button would do something.</summary>
    public bool CanSearch => !IsSearching && MayType && AddressQuery.IsSearchable(Query);

    /// <summary>Re-read the things that change underneath this — the speed, mostly.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(MayType));
        OnPropertyChanged(nameof(Barred));
        OnPropertyChanged(nameof(CanSearch));
    }

    /// <summary>Ask the geocoder.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!MayType)
        {
            Message = Barred;
            return;
        }

        if (AddressQuery.WhyNot(Query) is { } why)
        {
            Message = why;
            return;
        }

        var key = AddressQuery.CacheKey(Query);

        if (_cache.TryGetValue(key, out var held))
        {
            Show(held, fromCache: true);
            return;
        }

        var now = _clock.UtcNow;

        if (!AddressQuery.MayAsk(now, _lastAsked))
        {
            // One a second, as the policy asks. A double-tap on a rough road is one event.
            Message = "Just a moment — one search a second.";
            return;
        }

        _running?.Cancel();
        _running = new CancellationTokenSource();
        var token = _running.Token;

        _lastAsked = now;
        IsSearching = true;
        Message = null;

        try
        {
            var json = await _fetch(Geocoder.Url(Query, _where()), token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            var found = Geocoder.Parse(json);
            _cache[key] = found;
            Show(found, fromCache: false);
        }
        catch (OperationCanceledException)
        {
            // Another search replaced this one. Not a failure, and nothing to say about it.
        }
        catch (Exception)
        {
            // The tiles come over the same connection, so if this failed the map is probably
            // already saying so. Naming the likely cause beats naming the exception.
            Message = "No answer — the search needs a connection.";
            Results.Clear();
            HasResults = false;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearching = false;
            }
        }
    }

    private void Show(IReadOnlyList<GeocodeResult> found, bool fromCache)
    {
        Results.Clear();

        foreach (var result in found)
        {
            Results.Add(result);
        }

        HasResults = Results.Count > 0;

        Message = Results.Count > 0
            ? null
            : fromCache
                ? "Nothing found for that — try fewer words."
                : "Nothing found for that. A street and a town finds more than a street alone.";
    }

    /// <summary>
    /// Take a result: remember it, and hand it back to whoever is listening.
    /// </summary>
    /// <remarks>
    /// The map is not touched from here. This view-model knows nothing about placements or
    /// cameras — it raises <see cref="Picked"/> and the map does the moving, which is what keeps
    /// it testable without a browser.
    /// </remarks>
    [RelayCommand]
    private void Choose(GeocodeResult? result)
    {
        if (result is not { } found)
        {
            return;
        }

        Remember(found);
        Picked?.Invoke(found);
    }

    /// <summary>Raised when an address has been chosen.</summary>
    public event Action<GeocodeResult>? Picked;

    /// <summary>Empty the field and the results, ready for another search.</summary>
    [RelayCommand]
    private void Reset()
    {
        Query = string.Empty;
        Results.Clear();
        HasResults = false;
        Message = null;
    }

    private void Remember(GeocodeResult found)
    {
        for (var i = Recent.Count - 1; i >= 0; i--)
        {
            if (Recent[i] == found)
            {
                Recent.RemoveAt(i);
            }
        }

        Recent.Insert(0, found);

        while (Recent.Count > RecentKept)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }
    }

    /// <summary>How many recent addresses are kept. Enough to be useful, few enough to tap.</summary>
    private const int RecentKept = 6;

    /// <summary>What the search is doing, for <c>--shot</c>.</summary>
    public string Describe() =>
        $"query=\"{Query}\" results={Results.Count} recent={Recent.Count}"
        + (IsSearching ? " searching" : string.Empty)
        + (MayType ? string.Empty : " barred")
        + (Message is { } note ? $" note=\"{note}\"" : string.Empty);
}
