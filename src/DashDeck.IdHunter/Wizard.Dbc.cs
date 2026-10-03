using System.Globalization;
using DashDeck.Abstractions;
using DashDeck.Core.Discovery.Hunt;
using DashDeck.Vehicle.Monitor;

namespace DashDeck.IdHunter;

/// <summary>
/// Check a CAN database: a file of which frame carries which value, from wherever the person found
/// one — read on the tablet, never shipped. It lists what the truck actually sends, and decodes the
/// signals picked live, for the person to compare with FORScan or the cluster (ADR-0044).
/// </summary>
internal sealed partial class Wizard
{
    private CanDatabase? _database;
    private string? _databaseName;

    /// <summary>Message identifiers heard on each bus by the last presence check, or null before one.</summary>
    private Dictionary<CanBus, HashSet<uint>>? _heard;

    /// <summary>A file named with <c>--dbc</c>, offered first.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Where the guide suggests keeping database files: <c>%LOCALAPPDATA%\DashDeck\dbc\</c>.</summary>
    public static string DatabaseFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashDeck", "dbc");

    /// <summary>What the guide searches for when the person just presses Enter: what DashDeck is hunting.</summary>
    private const string DefaultSearch = "Oil Gbox Trn_Te Fuel Tire DrStat Belt Odometer SteWhlHeat Seat Hvac Range AirAmb";

    private async Task CheckDatabaseAsync(CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine("CHECK A CAN DATABASE — a .dbc (or .dbcx) file saying which frame carries which value.");
        _io.WriteLine("What it says is a lead: the guide shows whether your truck sends each frame, and decodes the");
        _io.WriteLine("signals you pick live, for you to compare with FORScan or the cluster. The file stays on this");
        _io.WriteLine("tablet; only what you confirm goes in findings.csv.");

        if (!LoadDatabase())
        {
            return;
        }

        var database = _database!;

        if (_heard is null && Proceed("Check which of its messages your truck sends? Enter for yes (about 10 s), B to skip"))
        {
            await CheckPresenceAsync(database, ct).ConfigureAwait(false);
        }

        while (!ct.IsCancellationRequested)
        {
            _io.WriteLine();
            var words = Ask("Search its signals (words like: oil tire door fuel), Enter for DashDeck's list, B to go back");
            if (words.Equals("B", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var found = database.Search(words.Length == 0 ? DefaultSearch : words).Take(60).ToList();
            if (found.Count == 0)
            {
                _io.WriteLine("Nothing by that name.");
                continue;
            }

            _io.WriteLine("  #   ID        HEARD ON         MESSAGE / SIGNAL");
            for (var i = 0; i < found.Count; i++)
            {
                var (message, signal) = found[i];
                _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {i + 1,-3} {message.IdText,-8}  {Heard(message),-15}  {message.Name}.{signal.Name}  {signal.Unit}"));
            }

            var picks = Ask("Numbers to check live (e.g. 3 or 3,5,9), Enter to search again");
            var chosen = picks.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .Where(n => n >= 1 && n <= found.Count)
                .Select(n => found[n - 1])
                .ToList();

            foreach (var group in chosen.GroupBy(c => c.Message))
            {
                await CheckMessageAsync(group.Key, [.. group.Select(g => g.Signal)], ct).ConfigureAwait(false);
            }
        }
    }

    private bool LoadDatabase()
    {
        var suggestion = DatabasePath;
        if (suggestion is null && Directory.Exists(DatabaseFolder))
        {
            suggestion = Directory.EnumerateFiles(DatabaseFolder, "*.dbc*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        if (_database is not null && suggestion == DatabasePath && _databaseName is not null &&
            Proceed($"Use {_databaseName} again? Enter for yes, B for another file"))
        {
            return true;
        }

        while (true)
        {
            var typed = Ask(suggestion is null
                ? $"Path to the file (or put it in {DatabaseFolder}), B to go back"
                : $"Path to the file [{suggestion}], B to go back").Trim().Trim('"');

            if (typed.Equals("B", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = typed.Length == 0 ? suggestion : typed;
            if (path is null || !File.Exists(path))
            {
                _io.WriteLine("No file there. Drag the file into this window to type its path.");
                continue;
            }

            try
            {
                _database = CanDatabase.Load(path);
            }
            catch (IOException ex)
            {
                _io.WriteLine($"Could not read it: {ex.Message}");
                continue;
            }

            DatabasePath = path;
            _databaseName = Path.GetFileName(path);
            _heard = null;

            var fd = _database.Messages.Count(m => m.CanFdOnly);
            var summary = string.Create(CultureInfo.InvariantCulture, $"{_databaseName}: {_database.Messages.Count} messages, {_database.SignalCount} signals");
            if (fd > 0)
            {
                summary += string.Create(CultureInfo.InvariantCulture, $", {fd} of them CAN FD only (this adapter cannot hear those)");
            }

            if (_database.Skipped > 0)
            {
                summary += string.Create(CultureInfo.InvariantCulture, $"; {_database.Skipped} signal lines it could not read");
            }

            _io.WriteLine(summary + ".");

            if (_database.Messages.Count == 0)
            {
                _io.WriteLine("That does not look like a CAN database — no BO_ lines.");
                continue;
            }

            return true;
        }
    }

    /// <summary>Listen a few seconds to each bus and note which of the file's messages are there.</summary>
    private async Task CheckPresenceAsync(CanDatabase database, CancellationToken ct)
    {
        if (!await ParkedAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        _heard = [];
        var known = database.Messages.Select(m => m.Id).ToHashSet();

        foreach (var bus in _session.RequestBuses)
        {
            _io.Write($"  Listening to {Bus(bus)}… ");
            var ids = new HashSet<uint>();
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await foreach (var frame in _session.Monitor.ListenAsync(bus, null, window.Token, Rate(bus)).ConfigureAwait(false))
                {
                    ids.Add(frame.Id);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            await _session.RestoreAsync(ct).ConfigureAwait(false);
            _heard[bus] = ids;
            _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{ids.Count} identifiers, {ids.Count(known.Contains)} of them in the file."));
        }

        var anywhere = _heard.Values.SelectMany(h => h).ToHashSet();
        var listed = database.Messages.Count(m => anywhere.Contains(m.Id));
        _io.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{listed} of the file's {database.Messages.Count} messages are on this truck."));
        _io.WriteLine("The rest were not heard: sent rarely, on a bus the OBD port does not reach, or not this truck's.");

        if (_session.Pins311BitRate is null)
        {
            _io.WriteLine("Pins 3/11 were silent at start-up, so only HS-CAN was heard.");
        }
    }

    private string Heard(CanMessage message)
    {
        if (message.CanFdOnly)
        {
            return "CAN FD only";
        }

        if (_heard is null)
        {
            return "not checked";
        }

        var buses = _heard.Where(h => h.Value.Contains(message.Id)).Select(h => h.Key == CanBus.Hs ? "HS" : "pins 3/11").ToList();
        return buses.Count == 0 ? "not heard" : string.Join(" + ", buses);
    }

    /// <summary>Find the message on a bus, then decode the picked signals live until Enter.</summary>
    private async Task CheckMessageAsync(CanMessage message, IReadOnlyList<CanSignal> signals, CancellationToken ct)
    {
        _io.WriteLine();
        _io.WriteLine($"{message.IdText} {message.Name}: {string.Join(", ", signals.Select(s => s.Name))}");

        if (message.CanFdOnly)
        {
            _io.WriteLine("CAN FD only — this adapter cannot hear it.");
            return;
        }

        if (!await ParkedAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var bus = await FindMessageBusAsync(message.Id, ct).ConfigureAwait(false);
        if (bus is null)
        {
            _io.WriteLine("Not heard on either bus in 3 seconds each.");
            foreach (var signal in signals)
            {
                Record(message, signal, null, "not heard", "", "absent");
            }

            return;
        }

        _io.WriteLine($"Heard on {Bus(bus.Value)}. Make it change — warm up, rev, open a door, read the cluster — and");
        _io.WriteLine("compare. A line is printed whenever a value changes. Press Enter to stop.");

        var lows = signals.ToDictionary(s => s.Name, _ => double.MaxValue);
        var highs = signals.ToDictionary(s => s.Name, _ => double.MinValue);
        var frames = 0;

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var listening = Task.Run(async () =>
            {
                string? last = null;
                try
                {
                    await foreach (var frame in _session.Monitor.ListenAsync(bus.Value, message.Id, stop.Token, Rate(bus.Value)).ConfigureAwait(false))
                    {
                        frames++;
                        foreach (var s in signals)
                        {
                            if (s.Decode(frame.Data) is { } v)
                            {
                                lows[s.Name] = Math.Min(lows[s.Name], v);
                                highs[s.Name] = Math.Max(highs[s.Name], v);
                            }
                        }

                        var line = string.Join("   ", signals.Select(s => $"{s.Name} = {s.Describe(frame.Data)}"));
                        if (line != last)
                        {
                            last = line;
                            _io.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {frame.At.TotalSeconds,6:0.0}s  {line}"));
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
            }, CancellationToken.None);

            await _io.ReadLineAsync(ct).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await listening.ConfigureAwait(false);
        }

        await _session.RestoreAsync(ct).ConfigureAwait(false);
        _io.WriteLine($"{frames} frames.");

        foreach (var signal in signals)
        {
            _io.WriteLine($"{signal.Name}:");
            var verdict = Verdict();
            var range = lows[signal.Name] <= highs[signal.Name]
                ? string.Create(CultureInfo.InvariantCulture, $"read {lows[signal.Name]:0.###} to {highs[signal.Name]:0.###} {signal.Unit}; {frames} frames")
                : $"never decoded; {frames} frames";
            Record(message, signal, bus, range, Bus(bus.Value), verdict);
        }
    }

    /// <summary>Which bus carries an identifier: what the presence check heard, or a short listen on each.</summary>
    private async Task<CanBus?> FindMessageBusAsync(uint id, CancellationToken ct)
    {
        if (_heard is not null)
        {
            foreach (var (bus, ids) in _heard)
            {
                if (ids.Contains(id))
                {
                    return bus;
                }
            }
        }

        foreach (var bus in _session.RequestBuses)
        {
            var heard = false;
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(3));

            try
            {
                await foreach (var _ in _session.Monitor.ListenAsync(bus, id, window.Token, Rate(bus)).ConfigureAwait(false))
                {
                    heard = true;
                    break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            await _session.RestoreAsync(ct).ConfigureAwait(false);
            if (heard)
            {
                return bus;
            }
        }

        return null;
    }

    private void Record(CanMessage message, CanSignal signal, CanBus? bus, string evidence, string busName, string verdict) =>
        _output.Finding(_output.Now(), $"{message.Name}.{signal.Name}", "", "dbc", busName, "", message.IdText, "",
            signal.Name, signal.Layout, signal.Scaling, evidence, "", verdict, $"from {_databaseName}");
}
