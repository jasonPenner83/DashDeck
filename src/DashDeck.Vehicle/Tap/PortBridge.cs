namespace DashDeck.Vehicle.Tap;

/// <summary>
/// Passes bytes between a program on a second serial port and the adapter, recording both ways.
/// </summary>
/// <remarks>
/// <para>
/// <b>For an app that only talks to Bluetooth adapters.</b> Windows can offer an incoming Bluetooth
/// serial port: a phone pairs with the computer and opens it as it would a Bluetooth ELM327, and
/// the tap stands between that port and the real adapter. <see cref="TapBridge"/> does the same for
/// a program that connects over TCP.
/// </para>
/// <para>
/// <b>A serial port has no connections.</b> The phone coming and going is not seen; the port stays
/// open and quiet. So an answer the program side cannot take (nobody is connected and the write
/// times out) is recorded and dropped, and only losing the adapter or the program's port ends the
/// tap.
/// </para>
/// </remarks>
public sealed class PortBridge(Stream adapter, Stream program, TapRecorder recorder)
{
    /// <summary>Relay until cancelled, or until either port goes away.</summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var ct = stop.Token;

        var toAdapter = PumpAsync(program, adapter, TapDirection.ToAdapter, "program's port lost", dropFailedWrites: false, ct);
        var fromAdapter = PumpAsync(adapter, program, TapDirection.FromAdapter, "adapter lost", dropFailedWrites: true, ct);

        await Task.WhenAny(toAdapter, fromAdapter).ConfigureAwait(false);
        stop.Cancel();

        // A serial read on Windows does not always notice cancellation; closing the ports, which the
        // caller does next, is what ends it. So they are given a moment, not waited for.
        await Task.WhenAny(Task.WhenAll(toAdapter, fromAdapter), Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
        recorder.Flush();
    }

    private async Task PumpAsync(Stream from, Stream to, TapDirection direction, string lost, bool dropFailedWrites, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            int read;

            try
            {
                read = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
            {
                recorder.Flush();
                recorder.Note($"{lost}: {ex.Message}");
                return;
            }

            if (read == 0)
            {
                // A serial stream does not end; a test stream does.
                return;
            }

            recorder.Add(direction, buffer.AsSpan(0, read));

            try
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
            {
                if (!dropFailedWrites)
                {
                    // Writing to the adapter failed: it was unplugged.
                    recorder.Flush();
                    recorder.Note($"adapter lost: {ex.Message}");
                    return;
                }

                recorder.Note($"the program's side did not take {read} bytes: {ex.Message}");
            }
        }
    }
}
