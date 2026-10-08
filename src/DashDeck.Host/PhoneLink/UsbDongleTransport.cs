using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using static DashDeck.Host.PhoneLink.WinUsbNative;

namespace DashDeck.Host.PhoneLink;

/// <summary>
/// The Carlinkit CPC200 over USB, through WinUSB (ADR-0019, ADR-0057).
/// </summary>
/// <remarks>
/// <b>WinUSB, called directly</b> — no USB library. The dongle is bound to Microsoft's in-box WinUSB
/// driver once with Zadig, the second driver exception ADR-0019 admits; nothing else is installed and
/// nothing is written to the registry (the device's key is read for the interface GUID WinUSB was
/// given). The endpoints are not guessed: they are read from the device's own interface descriptor —
/// the first bulk IN and bulk OUT pipes.
/// <para>
/// A dedicated thread reads exactly what the next message needs — a header, then its payload — the
/// way the community driver does, and <see cref="DongleFrameReader"/> resynchronises if the stream
/// ever stops making sense. Disposing aborts the pipes, which is what ends a blocked read.
/// </para>
/// </remarks>
public sealed class UsbDongleTransport : IDongleTransport
{
    /// <summary>The CPC200 family's vendor id.</summary>
    public const int VendorId = 0x1314;

    /// <summary>The product ids it shows up as, one per firmware generation.</summary>
    public static readonly IReadOnlyList<int> ProductIds = [0x1520, 0x1521];

    private readonly Channel<DongleMessage> _inbound = Channel.CreateUnbounded<DongleMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _writeGate = new();
    private SafeFileHandle? _file;
    private IntPtr _usb;
    private byte _in;
    private byte _out;
    private Thread? _reader;
    private volatile bool _closing;

    /// <inheritdoc />
    public string Name => "CPC200 (USB)";

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <summary>Why the last open found nothing usable, in words for the screen; null when it opened.</summary>
    public string? Problem { get; private set; }

    /// <summary>Bytes the reader had to throw away to find its place again.</summary>
    public long SkippedBytes { get; private set; }

    /// <inheritdoc />
    public Task<bool> OpenAsync(CancellationToken ct) => Task.Run(Open, ct);

    private bool Open()
    {
        if (!OperatingSystem.IsWindows())
        {
            Problem = "USB needs Windows.";
            return false;
        }

        var (instance, guids) = FindDevice();
        if (instance is null)
        {
            Problem = "No Carlinkit dongle is plugged in (USB 1314:1520 or 1521).";
            return false;
        }

        foreach (var guid in guids.Append(GenericWinUsbInterface).Append(UsbDeviceInterface).Distinct())
        {
            foreach (var path in InterfacePaths(guid))
            {
                if (!MatchesDongle(path) || !TryOpen(path))
                {
                    continue;
                }

                Problem = null;
                IsConnected = true;
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "dongle-usb-read" };
                _reader.Start();
                return true;
            }
        }

        Problem = guids.Count == 0
            ? "The dongle is plugged in but not bound to WinUSB. Bind it once with Zadig (see docs/phone-projection.md)."
            : "The dongle is plugged in and could not be opened. Close anything else using it, or re-plug it.";
        return false;
    }

    private bool TryOpen(string path)
    {
        var file = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (file.IsInvalid)
        {
            file.Dispose();
            return false;
        }

        if (!WinUsb_Initialize(file, out var usb))
        {
            file.Dispose();
            return false;
        }

        if (!WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor))
        {
            WinUsb_Free(usb);
            file.Dispose();
            return false;
        }

        byte pipeIn = 0, pipeOut = 0;
        for (byte i = 0; i < descriptor.BNumEndpoints; i++)
        {
            if (WinUsb_QueryPipe(usb, 0, i, out var pipe) && pipe.PipeType == PipeTypeBulk)
            {
                if ((pipe.PipeId & 0x80) != 0 && pipeIn == 0)
                {
                    pipeIn = pipe.PipeId;
                }
                else if ((pipe.PipeId & 0x80) == 0 && pipeOut == 0)
                {
                    pipeOut = pipe.PipeId;
                }
            }
        }

        if (pipeIn == 0 || pipeOut == 0)
        {
            WinUsb_Free(usb);
            file.Dispose();
            return false;
        }

        // A write that fills its last packet exactly is ended with a zero-length packet, so the dongle
        // never waits for more of a message that is complete.
        byte on = 1;
        WinUsb_SetPipePolicy(usb, pipeOut, ShortPacketTerminate, 1, ref on);

        _file = file;
        _usb = usb;
        _in = pipeIn;
        _out = pipeOut;
        return true;
    }

    private void ReadLoop()
    {
        var frames = new DongleFrameReader();
        var buffer = new byte[64 * 1024];

        while (!_closing)
        {
            var want = Math.Min(frames.Needed, 1024 * 1024);
            if (buffer.Length < want)
            {
                buffer = new byte[want];
            }

            if (!WinUsb_ReadPipe(_usb, _in, buffer, want, out var read, IntPtr.Zero))
            {
                break;
            }

            frames.Append(buffer.AsSpan(0, read));
            while (frames.Next() is { } message)
            {
                _inbound.Writer.TryWrite(message);
            }

            SkippedBytes = frames.SkippedBytes;
        }

        IsConnected = false;
        _inbound.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async Task<DongleMessage?> ReadAsync(CancellationToken ct)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task SendAsync(DongleMessage message, CancellationToken ct)
    {
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }

        var bytes = message.ToBytes();
        return Task.Run(
            () =>
            {
                lock (_writeGate)
                {
                    if (!_closing && !WinUsb_WritePipe(_usb, _out, bytes, bytes.Length, out _, IntPtr.Zero))
                    {
                        IsConnected = false;
                    }
                }
            },
            ct);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_closing)
        {
            return ValueTask.CompletedTask;
        }

        _closing = true;
        IsConnected = false;

        if (_usb != IntPtr.Zero)
        {
            // Ends a read blocked in the driver; the thread then sees _closing and leaves.
            WinUsb_AbortPipe(_usb, _in);
            WinUsb_AbortPipe(_usb, _out);
            _reader?.Join(TimeSpan.FromSeconds(2));

            lock (_writeGate)
            {
                WinUsb_Free(_usb);
                _usb = IntPtr.Zero;
            }
        }

        _file?.Dispose();
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // ── Finding it ────────────────────────────────────────────────────────────

    /// <summary>True when a device path or instance id names the dongle: <c>vid_1314&amp;pid_1520</c> or <c>…1521</c>.</summary>
    public static bool MatchesDongle(string pathOrInstance)
    {
        var text = pathOrInstance.ToUpperInvariant();
        return text.Contains(string.Create(CultureInfo.InvariantCulture, $"VID_{VendorId:X4}"), StringComparison.Ordinal)
            && ProductIds.Any(pid => text.Contains(string.Create(CultureInfo.InvariantCulture, $"PID_{pid:X4}"), StringComparison.Ordinal));
    }

    /// <summary>The GUIDs in a <c>DeviceInterfaceGUIDs</c> (multi-string) or <c>DeviceInterfaceGUID</c> value.</summary>
    public static IReadOnlyList<Guid> ParseGuids(byte[] value)
    {
        var text = Encoding.Unicode.GetString(value);
        return [.. text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Guid.TryParse(t.Trim(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)];
    }

    /// <summary>The dongle's instance id and the interface GUIDs WinUSB was given for it, if it is plugged in.</summary>
    private static (string? Instance, List<Guid> Guids) FindDevice()
    {
        var set = SetupDiGetClassDevsAll(IntPtr.Zero, "USB", IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == new IntPtr(-1))
        {
            return (null, []);
        }

        try
        {
            var info = new SpDevinfoData { CbSize = Marshal.SizeOf<SpDevinfoData>() };
            for (var i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                var id = new char[512];
                if (!SetupDiGetDeviceInstanceId(set, ref info, id, id.Length, out var length))
                {
                    continue;
                }

                var instance = new string(id, 0, Math.Max(0, length - 1));
                if (!MatchesDongle(instance))
                {
                    continue;
                }

                return (instance, RegistryGuids(set, ref info));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return (null, []);
    }

    private static List<Guid> RegistryGuids(IntPtr set, ref SpDevinfoData info)
    {
        var key = SetupDiOpenDevRegKey(set, ref info, DicsFlagGlobal, 0, DiregDev, KeyRead);
        if (key == new IntPtr(-1) || key == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var guids = new List<Guid>();
            foreach (var name in new[] { "DeviceInterfaceGUIDs", "DeviceInterfaceGUID" })
            {
                var size = 0;
                if (RegQueryValueEx(key, name, IntPtr.Zero, out _, null, ref size) != 0 || size <= 0)
                {
                    continue;
                }

                var data = new byte[size];
                if (RegQueryValueEx(key, name, IntPtr.Zero, out _, data, ref size) == 0)
                {
                    guids.AddRange(ParseGuids(data));
                }
            }

            return guids;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static IEnumerable<string> InterfacePaths(Guid guid)
    {
        var paths = new List<string>();
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1))
        {
            return paths;
        }

        try
        {
            var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (var i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required <= 0)
                {
                    continue;
                }

                var detail = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize is the fixed part's size: 8 on 64-bit, 6 on 32-bit — not the buffer's.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero)
                        && Marshal.PtrToStringUni(detail + 4) is { } path)
                    {
                        paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }
}
