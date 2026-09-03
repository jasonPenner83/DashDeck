using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DashDeck.Host.Interop;

namespace DashDeck.Host.Stage;

/// <summary>
/// Adopts another process's top-level window into the stage.
/// </summary>
/// <remarks>
/// A container of our own is created first and the foreign window is re-parented into
/// <em>that</em>, rather than returning the app's handle from <see cref="BuildWindowCore"/>
/// directly. <see cref="HwndHost"/> destroys whatever it is given, and destroying a window
/// belonging to somebody else's process is a good way to take their process with it.
/// <para>
/// <b>Known limitation, and it is not small:</b> <see cref="HwndHost"/> does not participate
/// in WPF transforms, so a hosted window ignores the <c>Viewbox</c> the whole shell is drawn
/// inside. On the tablet that <c>Viewbox</c> is 1:1 and this is invisible; on a scaled
/// development screen the adopted window lands in the wrong place at the wrong size. Every
/// other child-window occupant — video, WebView2 — has the same property, and it is why none
/// of them appear in a <c>--shot</c> either.
/// </para>
/// </remarks>
public sealed class HostedWindow : HwndHost
{
    private readonly IntPtr _adopted;
    private IntPtr _container;
    private long _originalStyle;

    public HostedWindow(IntPtr adopted) => _adopted = adopted;

    /// <inheritdoc />
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // "static" is a class Windows has already registered. Registering one of our own for
        // something whose entire job is to be a rectangle with a handle would be ceremony.
        _container = NativeMethods.CreateWindowEx(
            0,
            "static",
            null,
            (int)(NativeMethods.WsChild | NativeMethods.WsVisible),
            0,
            0,
            (int)Math.Max(ActualWidth, 1),
            (int)Math.Max(ActualHeight, 1),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        Adopt();

        return new HandleRef(this, _container);
    }

    /// <inheritdoc />
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Release();

        if (_container != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_container);
            _container = IntPtr.Zero;
        }
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Fit();
    }

    /// <summary>
    /// Turn a top-level window into a child of the container.
    /// </summary>
    /// <remarks>
    /// The style has to change as well as the parent. A window that keeps <c>WS_POPUP</c>, a
    /// caption and a resize frame while parented into a 912-wide stage draws its own title bar
    /// and border inside the dash, which looks like a bug and behaves like one.
    /// </remarks>
    private void Adopt()
    {
        if (!NativeMethods.IsWindow(_adopted))
        {
            return;
        }

        _originalStyle = (long)NativeMethods.GetWindowLongPtr(_adopted, NativeMethods.GwlStyle);

        var style = _originalStyle;
        style &= ~(NativeMethods.WsPopup | NativeMethods.WsCaption | NativeMethods.WsThickFrame);
        style |= NativeMethods.WsChild | NativeMethods.WsVisible;

        NativeMethods.SetWindowLongPtr(_adopted, NativeMethods.GwlStyle, (IntPtr)style);
        NativeMethods.SetParent(_adopted, _container);

        Fit();
    }

    /// <summary>
    /// Hand the window back before we destroy the container it lives in.
    /// </summary>
    /// <remarks>
    /// Its original style is restored and it is re-parented to the desktop. Skipping this
    /// destroys the app's window along with the container, which from the app's point of view
    /// is being killed by something it never agreed to be embedded in.
    /// </remarks>
    private void Release()
    {
        if (!NativeMethods.IsWindow(_adopted))
        {
            return;
        }

        NativeMethods.SetParent(_adopted, IntPtr.Zero);
        NativeMethods.SetWindowLongPtr(_adopted, NativeMethods.GwlStyle, (IntPtr)_originalStyle);
    }

    /// <summary>
    /// Size the adopted window to the container.
    /// </summary>
    /// <remarks>
    /// Driven manually, because a re-parented foreign window does not resize with its host —
    /// nothing tells it to. Without this it keeps whatever size it launched at and is clipped
    /// by the stage.
    /// </remarks>
    private void Fit()
    {
        if (_container == IntPtr.Zero || !NativeMethods.IsWindow(_adopted))
        {
            return;
        }

        var width = (int)Math.Max(ActualWidth, 1);
        var height = (int)Math.Max(ActualHeight, 1);

        NativeMethods.MoveWindow(_container, 0, 0, width, height, true);
        NativeMethods.MoveWindow(_adopted, 0, 0, width, height, true);
    }
}
