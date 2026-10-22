using System.ComponentModel;
using System.Diagnostics;
using DashDeck.Core.Emulation;

namespace DashDeck.Emulator;

/// <summary>Running the SDK's own <c>adb</c> for the few things gRPC cannot do (ADR-0075).</summary>
internal static class Adb
{
    /// <summary>
    /// Run it with these arguments, at most <paramref name="within"/>. Done or not
    /// (<see cref="AdbAnswers.Done"/>), and what it printed.
    /// </summary>
    public static async Task<(bool Done, string Output)> RunAsync(string adb, IReadOnlyList<string> arguments, TimeSpan within, CancellationToken ct)
    {
        var info = new ProcessStartInfo(adb)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("no process");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(within);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (false, ct.IsCancellationRequested ? "cancelled" : $"adb did not finish in {within.TotalSeconds:0} s");
            }

            var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return (AdbAnswers.Done(process.ExitCode, output), output.Trim());
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return (false, $"adb would not run: {e.Message}");
        }
    }
}
