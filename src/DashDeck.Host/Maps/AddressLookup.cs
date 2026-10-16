using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using DashDeck.Core.Maps;

namespace DashDeck.Host.Maps;

/// <summary>
/// The one HTTP call address search makes (ADR-0065).
/// </summary>
/// <remarks>
/// Separate from the view-model so the politeness, the URL and the parsing stay testable without
/// a network — the view-model takes a function, the way <c>UpdateService</c> does.
/// <para>
/// <b>The User-Agent is not optional.</b> Nominatim's usage policy requires an identifiable
/// application with a way to make contact, and a generic agent is grounds for being blocked. It
/// names DashDeck, its version and the public repository, which is where an issue reaches the
/// person responsible.
/// </para>
/// </remarks>
internal static class AddressLookup
{
    private static HttpClient? _client;

    /// <summary>
    /// How long a search waits before giving up.
    /// </summary>
    /// <remarks>
    /// Short on purpose. This is a person stopped in a truck waiting for a list, and a search that
    /// has not answered in ten seconds is a search to try again rather than one to keep waiting
    /// for — the connection is cellular and either there or not.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Ask the geocoder, and hand back whatever it said.</summary>
    public static Task<string> FetchAsync(string url, CancellationToken ct) =>
        Client().GetStringAsync(url, ct);

    private static HttpClient Client()
    {
        if (_client is not null)
        {
            return _client;
        }

        var client = new HttpClient { Timeout = Patience };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", Geocoder.UserAgent(RunningVersion()));

        return _client = client;
    }

    /// <summary>
    /// The version this build calls itself, for the agent.
    /// </summary>
    /// <remarks>
    /// The informational version, which carries the product version rather than the pinned
    /// assembly identity — <c>AssemblyVersion</c> is held at 1.0.0.0 so already-built components
    /// keep binding, so it would say nothing useful here.
    /// </remarks>
    private static string RunningVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var version = informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0";

        // Strips the "+<commit>" a build may append, which has no business in a User-Agent.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
