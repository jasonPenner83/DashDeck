using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DashDeck.Host.Interop;

/// <summary>
/// A job object that kills whatever is in it when DashDeck goes.
/// </summary>
/// <remarks>
/// <b>Not optional.</b> The web occupant already carries this warning in its <c>Dispose</c>:
/// without it the hosted thing "outlives the occupant and keeps playing whatever was on
/// screen — audible, invisible, and impossible to stop from the dash". A separate process is
/// that failure with no upper bound, because killing the parent does not touch it and it may
/// not even have a visible window any more once its host has gone.
/// <para>
/// <c>KILL_ON_JOB_CLOSE</c> means the operating system enforces it, which covers the case a
/// <c>finally</c> block cannot: DashDeck itself being killed.
/// </para>
/// </remarks>
public sealed class ProcessJob : IDisposable
{
    private IntPtr _handle;

    public ProcessJob()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);

        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var limits = new NativeMethods.JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf(limits);
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, false);

            NativeMethods.SetInformationJobObject(
                _handle,
                NativeMethods.JobObjectExtendedLimitInformation,
                buffer,
                (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>True when the job exists and can actually enforce anything.</summary>
    public bool IsUsable => _handle != IntPtr.Zero;

    /// <summary>Put a process under the job's protection. Returns false if it could not be.</summary>
    /// <remarks>
    /// A false here is worth surfacing rather than swallowing: it means the launched app will
    /// survive DashDeck, which the user should be told about rather than discovering when
    /// something is still making noise.
    /// </remarks>
    public bool Adopt(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        return IsUsable && NativeMethods.AssignProcessToJobObject(_handle, process.Handle);
    }

    /// <summary>
    /// Every process currently in the job, the launched one and its children.
    /// </summary>
    /// <remarks>
    /// <b>This is how a window is found.</b> <c>Process.MainWindowHandle</c> looks only at the
    /// process it was given, and a launcher that starts the real program in a child never gets
    /// one — jpackage applications do exactly that, and NuvioDesktop is one. Watching the
    /// launcher's handle stay zero for twenty-five seconds while a perfectly good window sat
    /// on its child is what sent the occupant into its "running outside" fallback.
    /// <para>
    /// The job already had to exist for cleanup. Asking it which processes it holds costs
    /// nothing and is exact, where walking a parent-process tree is neither.
    /// </para>
    /// </remarks>
    public IReadOnlyList<uint> ProcessIds()
    {
        if (_handle == IntPtr.Zero)
        {
            return [];
        }

        // Two counts and then the ids. Sized for far more processes than an application should
        // ever have; a truncated answer is still a usable one, because any of the ids will do.
        const int Capacity = 256;
        var size = (sizeof(uint) * 2) + (IntPtr.Size * Capacity);
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            if (!NativeMethods.QueryInformationJobObject(
                    _handle,
                    NativeMethods.JobObjectBasicProcessIdList,
                    buffer,
                    (uint)size,
                    IntPtr.Zero))
            {
                return [];
            }

            var returned = (int)(uint)Marshal.ReadInt32(buffer, sizeof(uint));
            var ids = new List<uint>(returned);

            for (var i = 0; i < Math.Min(returned, Capacity); i++)
            {
                ids.Add((uint)Marshal.ReadIntPtr(buffer, (sizeof(uint) * 2) + (i * IntPtr.Size)));
            }

            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // Closing the last handle is what kills the members. That is the whole mechanism.
        NativeMethods.CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}
