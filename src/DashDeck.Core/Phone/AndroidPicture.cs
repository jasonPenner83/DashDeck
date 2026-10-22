namespace DashDeck.Core.Phone;

/// <summary>
/// How big a picture the phone sends, against its full size (ADR-0076). The phone draws it either way;
/// what changes is how much video the tablet decodes and copies. The picture is scaled to fill, so a
/// smaller one is softer. Android's units stay the dash's (<see cref="AndroidFit"/>).
/// </summary>
public static class AndroidPicture
{
    /// <summary>The longest side of a SHARP picture; BALANCED and FAST scale it.</summary>
    public const int SharpLongSide = 1920;

    /// <summary>The sizes, in order, with the hold menu's names for them.</summary>
    public static readonly IReadOnlyList<(string Name, double Scale)> Sizes = [("SHARP", 1.0), ("BALANCED", 0.75), ("FAST", 0.5)];

    /// <summary>A size from the settings file; BALANCED when it is not one.</summary>
    public static (string Name, double Scale) Parse(string? saved) =>
        Sizes.FirstOrDefault(s => string.Equals(s.Name, saved?.Trim(), StringComparison.OrdinalIgnoreCase)) is { Name: not null } size
            ? size
            : Sizes[1];

    /// <summary>The size after this one, round again after FAST.</summary>
    public static (string Name, double Scale) Next((string Name, double Scale) size)
    {
        var at = Sizes.ToList().FindIndex(s => s.Name == size.Name);
        return Sizes[(at + 1) % Sizes.Count];
    }

    /// <summary>The longest side for a size.</summary>
    public static int LongSide((string Name, double Scale) size) => (int)Math.Round(SharpLongSide * size.Scale);
}

/// <summary>
/// How many things happened a second, lately — video packets from the phone — over a window that
/// slides with the clock it is given. Safe to add to from one thread and read from another.
/// </summary>
public sealed class RollingRate(TimeSpan window)
{
    private readonly Queue<TimeSpan> _times = new();

    /// <summary>Something happened at this moment (a monotonic clock's elapsed time).</summary>
    public void Add(TimeSpan at)
    {
        lock (_times)
        {
            _times.Enqueue(at);
            Trim(at);
        }
    }

    /// <summary>How many a second over the window ending now.</summary>
    public double PerSecond(TimeSpan now)
    {
        lock (_times)
        {
            Trim(now);
            return _times.Count / window.TotalSeconds;
        }
    }

    /// <summary>
    /// The most that happened in any one second of the window ending now — the frame rate while
    /// something moved. The phone sends a frame only when its picture changes, and the menu that
    /// shows this is opened with the picture standing still.
    /// </summary>
    public int BusiestSecond(TimeSpan now)
    {
        lock (_times)
        {
            Trim(now);

            var times = _times.ToArray();
            var busiest = 0;
            for (int first = 0, last = 0; last < times.Length; last++)
            {
                while (times[last] - times[first] >= TimeSpan.FromSeconds(1))
                {
                    first++;
                }

                busiest = Math.Max(busiest, last - first + 1);
            }

            return busiest;
        }
    }

    private void Trim(TimeSpan now)
    {
        while (_times.Count > 0 && now - _times.Peek() > window)
        {
            _times.Dequeue();
        }
    }
}
