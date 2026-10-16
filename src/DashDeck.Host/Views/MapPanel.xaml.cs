using System.ComponentModel;
using System.Windows.Controls;
using DashDeck.Host.Map;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Views;

/// <summary>
/// The MAPS destination's region. Bound to <see cref="MapViewModel"/>.
/// </summary>
/// <remarks>
/// One view for all three placements — bottom third, full screen, and on the stage — because the
/// placement changes which region it is drawn in rather than what is drawn. The shell holds the
/// region; <see cref="Stage.MapStageOccupant"/> holds the stage one.
/// <para>
/// <b>This owns the browser control</b>, for exactly as long as the panel is in the visual tree.
/// It has to be here rather than on the view-model, because a WebView2 cannot be in two places at
/// once and a placement change briefly has both panels alive. So moving the map rebuilds it,
/// which reloads the page — the camera lives on the view-model to survive that (Q43).
/// </para>
/// </remarks>
public partial class MapPanel : UserControl
{
    private MapSurface? _surface;
    private MapViewModel? _map;

    public MapPanel()
    {
        InitializeComponent();

        Loaded += (_, _) => Watch(DataContext as MapViewModel);
        Unloaded += (_, _) => Watch(null);
        DataContextChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                Watch(DataContext as MapViewModel);
            }
        };
    }

    /// <summary>Follow one view-model, and stop following whatever came before.</summary>
    private void Watch(MapViewModel? map)
    {
        if (ReferenceEquals(_map, map))
        {
            Sync();
            return;
        }

        if (_map is not null)
        {
            _map.PropertyChanged -= OnMapChanged;
            _map.ReloadRequested -= Rebuild;
        }

        _map = map;

        if (_map is not null)
        {
            _map.PropertyChanged += OnMapChanged;
            _map.ReloadRequested += Rebuild;
        }

        Sync();
    }

    private void OnMapChanged(object? sender, PropertyChangedEventArgs e)
    {
        // WantsSurface turns true the moment a first fix gives the map somewhere to open, which
        // is usually after this panel is already on screen.
        if (e.PropertyName is nameof(MapViewModel.WantsSurface) or nameof(MapViewModel.Failure))
        {
            Sync();
        }
    }

    /// <summary>Build the map when it is wanted, drop it when it is not.</summary>
    private void Sync()
    {
        var wanted = IsLoaded && _map is { WantsSurface: true };

        if (wanted && _surface is null)
        {
            var surface = new MapSurface(_map!);
            _surface = surface;
            Surface.Content = surface.View;

            // After the view exists, so the first position has something to go to.
            _map!.Attach(surface);
        }
        else if (!wanted && _surface is not null)
        {
            DropSurface();
        }
    }

    /// <summary>Throw the map away and build it again — what TRY AGAIN does.</summary>
    private void Rebuild()
    {
        DropSurface();
        Sync();
    }

    private void DropSurface()
    {
        if (_surface is null)
        {
            return;
        }

        Surface.Content = null;
        _map?.Detach(_surface);

        // The browser process outlives the control otherwise, and a map left fetching tiles on
        // the truck's connection is a bill rather than a leak.
        _surface.Dispose();
        _surface = null;
    }
}
