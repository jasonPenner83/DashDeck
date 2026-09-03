using System.Windows;
using DashDeck.Host.Settings;
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
    private readonly DisplaySettings? _display;
    private WebView2? _view;
    private bool _disposed;

    public WebStageOccupant(string name, string url, DisplaySettings? display = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        _uri = new Uri(url);
        _display = display;

        if (_display is not null)
        {
            _display.PropertyChanged += OnDisplayChanged;
        }
    }

    private void OnDisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DisplaySettings.WebScale))
        {
            ApplyScale();
        }
    }

    /// <summary>
    /// Zoom the page.
    /// </summary>
    /// <remarks>
    /// The browser's own zoom rather than a WPF <c>ScaleTransform</c>, and the difference is
    /// the whole point: zooming makes the page <em>reflow</em> into the space and show more,
    /// where a transform would draw the same phone-shaped layout smaller and gain nothing.
    /// </remarks>
    private void ApplyScale()
    {
        if (_disposed || _view?.CoreWebView2 is null || _display is null)
        {
            return;
        }

        try
        {
            _view.ZoomFactor = DisplaySettings.Clamp(_display.WebScale);
        }
        catch (Exception)
        {
            // WebView2 throws on a factor it dislikes rather than clamping. Not worth a dash.
        }
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>
    /// Whether this page can play protected content, once it has been asked.
    /// </summary>
    /// <remarks>
    /// <b>WebView2 does not ship Widevine</b>, and Spotify and Apple Music both gate playback
    /// behind it. The failure is silent and cruel: the player loads, the artwork appears, the
    /// controls respond, and pressing play does nothing. Asking the page directly turns that
    /// into something the dash can say out loud, which is the same rule every vehicle signal
    /// follows — a confidently broken screen is worse than one that admits it.
    /// </remarks>
    public string DrmStatus { get; private set; } = "unchecked";

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

        _view.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (e.IsSuccess && _view?.CoreWebView2 is { } core)
            {
                ApplyScale();

                core.WebMessageReceived += (_, message) =>
                {
                    if (message.TryGetWebMessageAsString() is { } text
                        && text.StartsWith("drm:", StringComparison.Ordinal))
                    {
                        DrmStatus = text["drm:".Length..];
                    }
                };
            }
        };

        _view.NavigationCompleted += async (_, _) => await ProbeDrmAsync().ConfigureAwait(true);

        return _view;
    }

    /// <summary>
    /// Ask the page whether it can decrypt Widevine.
    /// </summary>
    /// <remarks>
    /// <c>requestMediaKeySystemAccess</c> is the same call a streaming site makes before it
    /// decides whether to offer playback, so this is the site's own question asked early. It
    /// rejects rather than throwing when the CDM is absent, which is why the script resolves
    /// to a word instead of relying on an exception crossing the bridge.
    /// </remarks>
    private async Task ProbeDrmAsync()
    {
        if (_disposed || _view?.CoreWebView2 is null)
        {
            return;
        }

        // The answer is posted back rather than returned. ExecuteScriptAsync does not await
        // promises — it serialises whatever the expression evaluates to, so an async function
        // comes back as the JSON of a pending Promise, which is "{}". That looked like a
        // failed probe and was actually a working probe reported wrongly.
        const string Script = """
            (async () => {
              const say = s => window.chrome.webview.postMessage('drm:' + s);
              try {
                await navigator.requestMediaKeySystemAccess('com.widevine.alpha', [{
                  initDataTypes: ['cenc'],
                  audioCapabilities: [{ contentType: 'audio/mp4; codecs="mp4a.40.2"' }]
                }]);
                say('widevine');
              } catch (e) {
                say('none');
              }
            })();
            """;

        try
        {
            await _view.ExecuteScriptAsync(Script).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A probe that fails tells us nothing and must not take the occupant with it.
            DrmStatus = "unknown";
        }
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
            : $"web=ready title=\"{core.DocumentTitle}\" url={core.Source} " +
              $"drm={DrmStatus} zoom={_view?.ZoomFactor:0.00}";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_display is not null)
        {
            _display.PropertyChanged -= OnDisplayChanged;
        }

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
