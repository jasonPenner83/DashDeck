using System.Globalization;
using System.Text;
using DashDeck.Abstractions;

namespace DashDeck.SerialTap;

/// <summary>Which way bytes were travelling.</summary>
public enum TapDirection
{
    /// <summary>From the program (FORScan) to the adapter: commands.</summary>
    ToAdapter,

    /// <summary>From the adapter to the program: answers.</summary>
    FromAdapter,
}

/// <summary>
/// Turns the two byte streams through the tap into timestamped lines.
/// </summary>
/// <remarks>
/// <b>A line is what the adapter's protocol calls one.</b> An ELM327 or STN command ends in a
/// carriage return; an answer is lines ending in carriage returns, finished by the <c>&gt;</c>
/// prompt. Bytes arrive in whatever pieces the USB driver and the network hand over, so each
/// direction keeps its own partial line until its end arrives, and is stamped with the time its
/// first byte came — the moment that matters when lining a log up against FORScan's own.
/// <para>
/// Anything that is not printable ASCII is written as <c>\xNN</c>, so a binary surprise is visible
/// rather than mangling the file.
/// </para>
/// </remarks>
public sealed class TapRecorder
{
    private readonly IClock _clock;
    private readonly Action<string> _write;
    private readonly Partial _toAdapter = new();
    private readonly Partial _fromAdapter = new();
    private readonly object _gate = new();

    public TapRecorder(IClock clock, Action<string> write)
    {
        _clock = clock;
        _write = write;
    }

    /// <summary>Lines written so far, each way.</summary>
    public int CommandLines { get; private set; }

    /// <summary>Answer lines written so far.</summary>
    public int AnswerLines { get; private set; }

    /// <summary>Bytes seen each way.</summary>
    public long BytesToAdapter { get; private set; }

    /// <summary>Bytes from the adapter.</summary>
    public long BytesFromAdapter { get; private set; }

    /// <summary>Record bytes that just went through.</summary>
    public void Add(TapDirection direction, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            var partial = direction == TapDirection.ToAdapter ? _toAdapter : _fromAdapter;

            if (direction == TapDirection.ToAdapter)
            {
                BytesToAdapter += bytes.Length;
            }
            else
            {
                BytesFromAdapter += bytes.Length;
            }

            foreach (var b in bytes)
            {
                if (partial.Text.Length == 0 && partial.Started is null)
                {
                    partial.Started = _clock.UtcNow;
                }

                switch (b)
                {
                    case (byte)'\r':
                        Emit(direction, partial, prompt: false);
                        break;

                    case (byte)'\n':
                        // An ELM sends \r alone unless linefeeds are on; with them, \n just follows
                        // a line already ended.
                        if (partial.Text.Length == 0)
                        {
                            partial.Started = null;
                        }
                        else
                        {
                            Emit(direction, partial, prompt: false);
                        }

                        break;

                    case (byte)'>' when direction == TapDirection.FromAdapter:
                        Emit(direction, partial, prompt: true);
                        break;

                    case >= 0x20 and < 0x7F:
                        partial.Text.Append((char)b);
                        break;

                    default:
                        partial.Text.Append(CultureInfo.InvariantCulture, $"\\x{b:X2}");
                        break;
                }
            }
        }
    }

    /// <summary>A note in the log — a connection, a disconnection — stamped like a line.</summary>
    public void Note(string text)
    {
        lock (_gate)
        {
            _write($"{Stamp(_clock.UtcNow)}  --  {text}");
        }
    }

    /// <summary>Write out whatever partial lines are left, at the end of a session.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_toAdapter.Text.Length > 0)
            {
                Emit(TapDirection.ToAdapter, _toAdapter, prompt: false);
            }

            if (_fromAdapter.Text.Length > 0)
            {
                Emit(TapDirection.FromAdapter, _fromAdapter, prompt: false);
            }
        }
    }

    /// <summary>The time format of every line: local wall-clock time to the millisecond.</summary>
    public static string Stamp(DateTimeOffset at) =>
        at.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private void Emit(TapDirection direction, Partial partial, bool prompt)
    {
        var text = partial.Text.ToString();
        var started = partial.Started ?? _clock.UtcNow;
        partial.Text.Clear();
        partial.Started = null;

        if (text.Length == 0 && !prompt)
        {
            // Blank lines between answers carry nothing.
            return;
        }

        var arrow = direction == TapDirection.ToAdapter ? ">>" : "<<";
        var line = $"{Stamp(started)}  {arrow}  {text}{(prompt ? (text.Length > 0 ? " >" : ">") : "")}";

        if (direction == TapDirection.ToAdapter)
        {
            CommandLines++;
        }
        else
        {
            AnswerLines++;
        }

        _write(line);
    }

    private sealed class Partial
    {
        public StringBuilder Text { get; } = new();

        public DateTimeOffset? Started { get; set; }
    }
}
