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

    public WebStageOccupant(string name, string url, int preferredBands = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        PreferredBands = preferredBands;
        _uri = new Uri(url);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>
    /// Four bands by default. Unlike video there is no aspect ratio forcing the number —
    /// a web app reflows — so this is a judgement about how much of the dash it deserves
    /// rather than arithmetic.
    /// </summary>
    public int PreferredBands { get; }

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
