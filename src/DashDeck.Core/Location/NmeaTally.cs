using System.Globalization;

namespace DashDeck.Core.Location;

/// <summary>
/// What a stretch of a phone's NMEA feed added up to — and, when nothing useful arrived, which
/// of the several possible reasons it was.
/// </summary>
/// <remarks>
/// <see cref="NmeaParser"/> answers one question about one sentence: does it decode. That is the
/// right shape for the read loop and the wrong shape for a person holding a phone and a tablet
/// and wondering which COM port the pairing landed on. This counts what came past — how many
/// lines, how many decoded, how many were sentence types the dash does not read, whether a fix
/// was ever claimed — and turns it into one line to put on a screen or print to a console.
/// <para>
/// It deliberately separates <em>nothing arrived</em> from <em>sentences arrived and none carried
/// a fix</em>, because they need opposite fixes: the first is the wrong port or an app that is
/// not sharing, the second is a receiver that has not seen the sky yet. Reporting either as "no
/// GPS" would send someone to the wrong place.
/// </para>
/// </remarks>
public sealed class NmeaTally
{
    private readonly List<string> _unreadTypes = [];

    /// <summary>Every line the transport handed over, including junk and blank lines.</summary>
    public int Lines { get; private set; }

    /// <summary>Lines that decoded into a fix, with or without a position.</summary>
    public int Parsed { get; private set; }

    /// <summary>RMC sentences — position, speed and course.</summary>
    public int Rmc { get; private set; }

    /// <summary>GGA sentences — position and fix quality.</summary>
    public int Gga { get; private set; }

    /// <summary>
    /// Sentences the dash does not read (GSV, GSA, VTG…). Not a fault: a feed carrying these and
    /// no RMC or GGA is still a working link, and the app usually has a switch for which to send.
    /// </summary>
    public int OtherSentences { get; private set; }

    /// <summary>
    /// Lines that looked like an RMC or GGA and would not decode — a bad checksum or a truncated
    /// line. A few mean a noisy link; a stream of them means the baud or the port is wrong.
    /// </summary>
    public int Rejected { get; private set; }

    /// <summary>Lines that were not NMEA at all.</summary>
    public int Junk { get; private set; }

    /// <summary>The newest fix that decoded, fix or not.</summary>
    public LocationFix? Latest { get; private set; }

    /// <summary>The newest fix that actually carried a position.</summary>
    public LocationFix? LatestWithPosition { get; private set; }

    /// <summary>The sentence types seen that the dash does not read, for the console to name.</summary>
    public IReadOnlyList<string> UnreadTypes => _unreadTypes;

    /// <summary>Feed one line, exactly as the transport delivered it.</summary>
    public void Add(string line, DateTimeOffset arrivedUtc)
    {
        Lines++;

        if (NmeaParser.TryParse(line, arrivedUtc, out var fix))
        {
            Parsed++;

            if (SentenceType(line) is "GGA")
            {
                Gga++;
            }
            else
            {
                Rmc++;
            }

            Latest = fix;

            if (fix.HasFix)
            {
                LatestWithPosition = fix;
            }

            return;
        }

        var type = SentenceType(line);

        if (type is null)
        {
            Junk++;
            return;
        }

        if (type is "RMC" or "GGA")
        {
            Rejected++;
            return;
        }

        OtherSentences++;

        if (!_unreadTypes.Contains(type, StringComparer.Ordinal))
        {
            _unreadTypes.Add(type);
        }
    }

    /// <summary>What this adds up to, which decides what the person should try next.</summary>
    public NmeaVerdict Verdict => this switch
    {
        { Lines: 0 } => NmeaVerdict.Silent,
        { Parsed: 0, OtherSentences: > 0 } => NmeaVerdict.WrongSentences,
        { Parsed: 0 } => NmeaVerdict.NotNmea,
        { LatestWithPosition: null } => NmeaVerdict.NoFixYet,
        _ => NmeaVerdict.Fix,
    };

    /// <summary>
    /// One line for a screen: what is arriving and, when there is a position, where and how fast.
    /// </summary>
    public string Describe() => Verdict switch
    {
        NmeaVerdict.Silent => "NO DATA — nothing has arrived",
        NmeaVerdict.WrongSentences => string.Create(
            CultureInfo.InvariantCulture,
            $"CONNECTED — {Lines} lines, no RMC or GGA ({string.Join(", ", _unreadTypes)})"),
        NmeaVerdict.NotNmea when Rejected > 0 => string.Create(
            CultureInfo.InvariantCulture,
            $"CONNECTED — {Lines} lines, {Rejected} damaged RMC/GGA and nothing that decoded"),
        NmeaVerdict.NotNmea => string.Create(
            CultureInfo.InvariantCulture,
            $"CONNECTED — {Lines} lines, none of it NMEA"),
        NmeaVerdict.NoFixYet => string.Create(
            CultureInfo.InvariantCulture,
            $"NO FIX — {Parsed} sentences, the receiver has no position yet"),
        _ => LocationStatus.Describe(LatestWithPosition!.Value),
    };

    /// <summary>
    /// The three-letter sentence type, or null when the line is not an NMEA sentence at all.
    /// Read without validating the checksum, because a rejected sentence still needs naming.
    /// </summary>
    private static string? SentenceType(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var s = line.Trim();

        if (s.Length < 6 || s[0] != '$')
        {
            return null;
        }

        var comma = s.IndexOf(',');
        var head = comma > 1 ? s[1..comma] : s[1..];

        return head.Length >= 3 ? head[^3..].ToUpperInvariant() : null;
    }
}

/// <summary>What a tally adds up to — each one sends the person somewhere different.</summary>
public enum NmeaVerdict
{
    /// <summary>Not one line. The wrong port, or the phone is not sharing.</summary>
    Silent,

    /// <summary>Sentences arrive, but none the dash reads. The app's sentence selection.</summary>
    WrongSentences,

    /// <summary>Bytes arrive and are not NMEA. The wrong port or the wrong app.</summary>
    NotNmea,

    /// <summary>The link works and the receiver has no position yet. Go outside and wait.</summary>
    NoFixYet,

    /// <summary>A position arrived. This is the port to put in Settings.</summary>
    Fix,
}
