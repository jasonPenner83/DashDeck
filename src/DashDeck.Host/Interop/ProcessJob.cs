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
