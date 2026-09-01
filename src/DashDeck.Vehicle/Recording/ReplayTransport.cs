using System.Text.Json;

namespace DashDeck.Vehicle.Recording;

/// <summary>
/// Replays a recorded session. An ordinary transport, so everything above it is unaware.
/// </summary>
/// <remarks>
/// Replay answers by command rather than strictly in recorded order, because the arbiter
/// will not necessarily ask in the same sequence twice. Each command cycles through the
/// responses recorded for it, so a signal that changed during the drive still changes
/// during replay.
/// </remarks>
public sealed class ReplayTransport : IVehicleTransport
{
    private readonly Dictionary<string, List<string>> _byCommand = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _cursor = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    public ReplayTransport(string path)
    {
        _path = path;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<RecordedExchange>(line);
            if (entry is null)
            {
                continue;
            }

            if (!_byCommand.TryGetValue(entry.Command, out var list))
            {
                list = [];
                _byCommand[entry.Command] = list;
            }

            list.Add(entry.Response);
        }
    }

    public TransportState State { get; private set; } = TransportState.Disconnected;

    public string Description => $"Replay of {Path.GetFileName(_path)}";

    public event Action<TransportState>? StateChanged;

    /// <summary>Distinct commands present in the recording. Useful for asserting coverage.</summary>
    public IReadOnlyCollection<string> RecordedCommands => _byCommand.Keys;

    public Task ConnectAsync(CancellationToken ct)
    {
        State = TransportState.Connected;
        StateChanged?.Invoke(State);
        return Task.CompletedTask;
    }

    public Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
        if (!_byCommand.TryGetValue(command, out var responses) || responses.Count == 0)
        {
            // A command the recording never saw. NO DATA is the honest answer — the same
            // one a truck gives for a PID it does not support.
            return Task.FromResult("NO DATA\r\r>");
        }

        var index = _cursor.GetValueOrDefault(command);
        _cursor[command] = (index + 1) % responses.Count;
        return Task.FromResult(responses[index]);
    }

    public ValueTask DisposeAsync()
    {
        State = TransportState.Disconnected;
        return ValueTask.CompletedTask;
    }
}
