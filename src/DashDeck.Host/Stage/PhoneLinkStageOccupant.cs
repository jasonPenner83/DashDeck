using System.Windows;
using DashDeck.Abstractions;
using DashDeck.Host.ViewModels;

namespace DashDeck.Host.Stage;

/// <summary>
/// Android Auto and CarPlay, projected through a dongle.
/// </summary>
/// <remarks>
/// The dongle performs Google's — and Apple's — projection handshake in its own firmware and
/// hands back H.264 and PCM over USB, which is why this route was chosen over building a head
/// unit (ADR-0019). The alternative was implementing a proprietary protocol and reparenting
/// somebody else's Qt window into the stage; this is a framed byte stream and a decode.
/// <para>
/// <b>The hardware is chosen and not bought</b>, so this runs against a synthetic dongle that
/// answers the handshake and sends no frames. The screen says exactly that.
/// </para>
/// </remarks>
public sealed class PhoneLinkStageOccupant(IClock clock) : IStageOccupant
{
    private readonly PhoneLinkViewModel _viewModel = new(clock);

    /// <inheritdoc />
    public string Name => "PHONE";

    /// <inheritdoc />
    public FrameworkElement CreateView() => new PhoneLinkView { DataContext = _viewModel };

    /// <inheritdoc />
    public FrameworkElement? CreateActionBar() => ActionBar.Row(
        ActionBar.Button("RECONNECT", () => _viewModel.ReconnectCommand.Execute(null), 200),
        ActionBar.Caption("Android Auto and CarPlay arrive through a Carlinkit CPC200 — not yet bought"));

    /// <inheritdoc />
    public string Describe() => _viewModel.Describe();

    /// <inheritdoc />
    public void Dispose() => _viewModel.Dispose();
}
