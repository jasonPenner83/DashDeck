using CommunityToolkit.Mvvm.ComponentModel;
using DashDeck.Host.Stage;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// One entry in the stage launcher — the option plus whether it is the one currently on.
/// </summary>
/// <remarks>
/// The wrapper exists only so "which button is lit" can change without rebuilding the list.
/// <see cref="StageOption"/> is a record describing what is available; that is static, and
/// deliberately knows nothing about what is running.
/// </remarks>
public sealed partial class StageOptionViewModel(StageOption option) : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>What this button would put on the stage.</summary>
    public StageOption Option { get; } = option;

    /// <summary>Short uppercase name, as shown on the button.</summary>
    public string Name => Option.Name;

    /// <summary>One line under it in the full grid — what it is, or why it cannot be chosen.</summary>
    public string Detail => Option.Detail;

    /// <summary>False for placeholders: listed, but not choosable.</summary>
    public bool IsAvailable => Option.IsAvailable;
}
