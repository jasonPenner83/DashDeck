using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace DashDeck.Host.Stage;

/// <summary>
/// A web app on the stage, in a WebView2.
/// </summary>
/// <remarks>
/// The generic answer to "can we not reinvent this". Anything that already exists as a web
/// app — Nuvio, a map, a dashboard — becomes a stage occupant without reparenting somebody
/// else's window or reimplementing their product. The Evergreen runtime ships with Windows
/// 11, so this costs nothing against constraint C1: still nothing to install.
/// <para>
/// It is also the licence-safe way to host GPL software. Nuvio is GPLv3, and displaying a
/// page it serves does not make DashDeck a derivative work; linking its code in would. The
/// arm's length is the point, not an accident of convenience.
/// </para>
/// </remarks>
public sealed class WebStageOccupant : IStageOccupant
{
    private readonly Uri _uri;
    private WebView2? _view;
    private bool _disposed;

    public WebStageOccupant(string name, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        _uri = new Uri(url);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public FrameworkElement CreateView()
    {
        _view = new WebView2
        {
            // WebView2 wants a writable profile directory and defaults to one beside the
            // executable, which fails the moment the app lives somewhere read-only.
            // %LOCALAPPDATA% is where everything else of ours lives too (constraint C1).
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = UserDataFolder(),
            },
            Source = _uri,
        };

        return _view;
    }

    /// <summary>
    /// Back, reload and home — the three controls a hosted page cannot give you itself.
    /// </summary>
    /// <remarks>
    /// Every call is guarded, because <c>WebView2</c> throws rather than no-ops if its core
    /// has not finished initialising, and the bar is on screen and tappable from the moment
    /// the occupant loads.
    /// </remarks>
    public FrameworkElement? CreateActionBar() => ActionBar.Row(
        ActionBar.Button("‹ BACK", () => Guarded(v =>
        {
            if (v.CanGoBack)
            {
                v.GoBack();
            }
        })),
        ActionBar.Button("RELOAD", () => Guarded(v => v.Reload())),
        ActionBar.Button("HOME", () => Guarded(v => v.Source = _uri)),
        ActionBar.Caption(_uri.Host));

    /// <inheritdoc />
    public string Describe()
    {
        var core = _view?.CoreWebView2;

        return core is null
            ? $"web=initialising url={_uri}"
            : $"web=ready title=\"{core.DocumentTitle}\" url={core.Source}";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Without this the browser process outlives the occupant and keeps playing whatever
        // was on screen — audible, invisible, and impossible to stop from the dash.
        _view?.Dispose();
    }

    /// <summary>Run something against the view only when it is in a state to accept it.</summary>
    private void Guarded(Action<WebView2> action)
    {
        if (_disposed || _view?.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            action(_view);
        }
        catch (Exception)
        {
            // A browser control that objects to being driven must not take the dash down.
        }
    }

    private static string UserDataFolder()
    {
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DashDeck",
            "WebView2");

        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
