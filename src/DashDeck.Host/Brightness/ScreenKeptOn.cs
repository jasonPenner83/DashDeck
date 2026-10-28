using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Core.Screen;

namespace DashDeck.Host.Brightness;

/// <summary>
/// Keeps the screen on while DashDeck runs and the tablet is plugged in (ADR-0080).
/// </summary>
/// <remarks>
/// <para>
/// <b>A request, not a setting.</b> <c>SetThreadExecutionState</c> asks Windows to keep the display
/// and the system awake for as long as the request stands. No power plan or sleep timeout is
/// changed (C1), and Windows drops the request by itself when DashDeck exits, however it exits.
/// </para>
/// <para>
/// Driven by the shell's one-second beat, on the dispatcher. The request belongs to the thread
/// that makes it, so it is always made on that one. Windows is asked only when the answer changes.
/// </para>
/// </remarks>
public sealed partial class ScreenKeptOn : ObservableObject, IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private readonly Func<PowerLine> _power;
    private readonly Func<bool, bool> _request;
    private bool? _keepingOn;

    /// <summary>What the screen is doing, one line, for Settings ▸ DISPLAY.</summary>
    [ObservableProperty]
    private string _status = "Checking the power…";

    /// <param name="power">Where the power is coming from. Null reads Windows.</param>
    /// <param name="request">Keeps the screen on, or lets it go; false when Windows refused. Null asks Windows.</param>
    public ScreenKeptOn(Func<PowerLine>? power = null, Func<bool, bool>? request = null)
    {
        _power = power ?? ReadPowerLine;
        _request = request ?? Request;
    }

    /// <summary>Look at the power again, and ask Windows if the answer changed.</summary>
    public void Apply()
    {
        PowerLine line;

        try
        {
            line = _power();
        }
        catch (Exception)
        {
            line = PowerLine.Unknown;
        }

        var decision = ScreenAwake.Decide(line);

        if (_keepingOn == decision.KeepOn)
        {
            return;
        }

        var granted = Ask(decision.KeepOn);
        _keepingOn = decision.KeepOn;
        Status = granted || !decision.KeepOn ? decision.Status : "Windows would not keep the screen on: it sleeps as Windows is set";
    }

    /// <summary>Let the screen go. Windows would on exit anyway; this says so sooner.</summary>
    public void Dispose()
    {
        if (_keepingOn is true)
        {
            Ask(false);
        }

        _keepingOn = false;
    }

    private bool Ask(bool keepOn)
    {
        try
        {
            return _request(keepOn);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Request(bool keepOn) =>
        SetThreadExecutionState(keepOn ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous) != 0;

    private static PowerLine ReadPowerLine()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return PowerLine.Unknown;
        }

        return status.ACLineStatus switch
        {
            0 => PowerLine.Battery,
            1 => PowerLine.Mains,
            _ => PowerLine.Unknown,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);
}
