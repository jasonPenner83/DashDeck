namespace DashDeck.IdHunter;

/// <summary>What the guide says and hears — the terminal, or a script in the tests.</summary>
internal interface IHuntConsole
{
    void Write(string text);

    void WriteLine(string text = "");

    /// <summary>Wait for a line.</summary>
    string? ReadLine();

    /// <summary>Wait for a line while something else runs; cancelling leaves the line for the next read.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);
}

internal sealed class SystemConsole : IHuntConsole
{
    private Task<string?>? _pending;

    public void Write(string text) => Console.Write(text);

    public void WriteLine(string text = "") => Console.WriteLine(text);

    public string? ReadLine()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            return pending.GetAwaiter().GetResult();
        }

        return Console.ReadLine();
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        _pending ??= Task.Run(Console.ReadLine, CancellationToken.None);
        var line = await _pending.WaitAsync(ct).ConfigureAwait(false);
        _pending = null;
        return line;
    }
}
