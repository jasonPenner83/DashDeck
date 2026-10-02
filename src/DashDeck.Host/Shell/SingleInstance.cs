using System.Diagnostics;
using System.Threading;
using DashDeck.Host.Interop;

namespace DashDeck.Host.Shell;

/// <summary>
/// The Windows side of <see cref="InstanceGate"/>: a named mutex, and finding the other dash's window.
/// </summary>
/// <remarks>
/// A <c>Local\</c> mutex — per signed-in session, nothing written anywhere, gone with the process.
/// No registry, no service: constraint C1 holds. If a dash is ended without releasing it (a crash,
/// the exit backstop), Windows marks the mutex abandoned and the next one takes it as its own.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\DashDeck.Host.SingleInstance";
    private const int SwRestore = 9;

    private readonly Mutex _mutex;
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>
    /// Take the dash, or bring the other one forward. Null means: do not start — another DashDeck
    /// is (or stayed) running. Called on the UI thread, which must also <see cref="Release"/> it.
    /// </summary>
    public static SingleInstance? Claim()
    {
        var mutex = new Mutex(false, MutexName);
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var acquired = TryTake(mutex);
            var other = acquired ? IntPtr.Zero : OtherWindow();

            switch (InstanceGate.Next(acquired, other != IntPtr.Zero, watch.Elapsed))
            {
                case InstanceStep.Run:
                    return new SingleInstance(mutex, owned: true);

                case InstanceStep.ActivateOtherAndExit:
                    NativeMethods.ShowWindow(other, SwRestore);
                    NativeMethods.SetForegroundWindow(other);
                    mutex.Dispose();
                    return null;

                case InstanceStep.GiveUp:
                    mutex.Dispose();
                    return null;

                default:
                    Thread.Sleep(InstanceGate.Interval);
                    break;
            }
        }
    }

    /// <summary>
    /// Let go — once the adapter's port is closed, so a successor can open it. Safe to call twice.
    /// </summary>
    public void Release()
    {
        if (!_owned)
        {
            return;
        }

        _owned = false;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread after all; the handle closing below releases it anyway.
        }
    }

    public void Dispose()
    {
        Release();
        _mutex.Dispose();
    }

    private static bool TryTake(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The last holder ended without letting go. It is ours now.
            return true;
        }
    }

    /// <summary>Another DashDeck's visible main window, or zero.</summary>
    private static IntPtr OtherWindow()
    {
        using var self = Process.GetCurrentProcess();

        foreach (var process in Process.GetProcessesByName(self.ProcessName))
        {
            using (process)
            {
                if (process.Id == self.Id)
                {
                    continue;
                }

                var window = process.MainWindowHandle;
                if (window != IntPtr.Zero && NativeMethods.IsWindowVisible(window))
                {
                    return window;
                }
            }
        }

        return IntPtr.Zero;
    }
}
