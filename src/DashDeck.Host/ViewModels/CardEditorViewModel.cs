using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Host.Dash;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// One choice in the card editor.
/// </summary>
/// <remarks>
/// Chips rather than combo boxes, sliders or spinners throughout, and that is a touch
/// decision rather than an aesthetic one: every option here has a small, known set of sane
/// values, and a row of 56-tall targets can be hit with a thumb in a truck. A slider for a
/// polling rate would invite 3.7 Hz, which is a worse answer than any of the six offered.
/// </remarks>
public sealed partial class OptionChip : ObservableObject
{
    public OptionChip(string group, string caption, object value, string? detail = null, bool enabled = true)
    {
        Group = group;
        Caption = caption;
        Value = value;
        Detail = detail;
        IsEnabled = enabled;
    }

    /// <summary>Which setting this chip belongs to. Lets one command serve every row.</summary>
    public string Group { get; }

    /// <summary>What the chip says.</summary>
    public string Caption { get; }

    /// <summary>A second, quieter line. Used by the signal picker for the id and unit.</summary>
    public string? Detail { get; }

    /// <summary>True when this line is worth drawing.</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>The value this chip selects.</summary>
    public object Value { get; }

    /// <summary>False for an option this signal cannot offer — a bar with no range to fill.</summary>
    public bool IsEnabled { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Editing one card: what it shows, how it shows it, and how often it asks.
/// </summary>
/// <remarks>
/// <b>There is no Cancel, and every change applies at once.</b> The rest of the app already
/// works this way — a theme or an accent is written the moment it changes, because a dash is
/// closed by having its power pulled and a setting that survives only a graceful shutdown is
/// not a setting (ADR-0014). A card editor that hoarded changes behind an OK button would be
/// the one place that lost work to an ignition cut.
/// <para>
/// It also makes the preview honest: the card above the controls is the real card, live on
/// the real signal, not a mock-up of one.
/// </para>
/// </remarks>
public sealed partial class CardEditorViewModel : ObservableObject
{
    private readonly DashboardViewModel _dashboard;
    private readonly IReadOnlyList<SignalChoice> _choices;
    private bool _suspendApply;

    public CardEditorViewModel(
        DashboardViewModel dashboard,
        WidgetCardViewModel card,
        IReadOnlyList<SignalChoice> choices)
    {
        _dashboard = dashboard;
        _choices = choices;
        Card = card;

        _label = card.Spec.Label;
        Rebuild();
    }

    /// <summary>The card being edited. Bound directly as the preview, because it is the card.</summary>
    public WidgetCardViewModel Card { get; }

    /// <summary>Where the arrangement is stored, shown so it is never a mystery.</summary>
    public static string StorePath => DashboardStore.Path;

    /// <summary>Every signal in the catalog, as chips.</summary>
    public ObservableCollection<OptionChip> Signals { get; } = [];

    /// <summary>One column or two.</summary>
    public ObservableCollection<OptionChip> Widths { get; } = [];

    /// <summary>Number or bar.</summary>
    public ObservableCollection<OptionChip> Styles { get; } = [];

    /// <summary>Display units this signal can offer.</summary>
    public ObservableCollection<OptionChip> Units { get; } = [];

    /// <summary>Requested rate, including the catalog default.</summary>
    public ObservableCollection<OptionChip> Rates { get; } = [];

    /// <summary>Decimal places.</summary>
    public ObservableCollection<OptionChip> Formats { get; } = [];

    /// <summary>What survives when the budget is oversubscribed.</summary>
    public ObservableCollection<OptionChip> Priorities { get; } = [];

    /// <summary>True when this signal has more than one unit to offer.</summary>
    public bool HasUnitChoice => Units.Count > 1;

    /// <summary>
    /// The caption. Empty means the catalog name, which the placeholder says.
    /// </summary>
    [ObservableProperty]
    private string _label;

    /// <summary>What the label box shows when nothing has been typed.</summary>
    public string LabelPlaceholder => Card.Choice?.Caption ?? Card.Spec.SignalId.ToUpperInvariant();

    /// <summary>
    /// A plain-language note about what this card costs the link.
    /// </summary>
    /// <remarks>
    /// The one number in this editor with a consequence beyond the card itself. Everything on
    /// the dash shares one serialised link, so a rate is a claim on everyone else — showing
    /// what was asked for beside what the arbiter actually allocated is the same honesty the
    /// debug console prints its plan for.
    /// </remarks>
    public string CostNote
    {
        get
        {
            var requested = Card.Spec.RateHz > 0
                ? Card.Spec.RateHz
                : Card.Choice?.DefaultRateHz ?? 0;

            return string.Create(
                CultureInfo.CurrentCulture,
                $"Asking for {requested:0.##} Hz on a link every card shares. {Card.RateText}.");
        }
    }

    /// <summary>Select an option in any of the rows.</summary>
    [RelayCommand]
    private void Choose(OptionChip? chip)
    {
        if (chip is null || !chip.IsEnabled)
        {
            return;
        }

        var spec = Card.Spec;

        spec = chip.Group switch
        {
            "signal" => RespecForSignal(spec, (string)chip.Value),
            "width" => spec with { Width = (int)chip.Value },
            "style" => spec with { Style = ((CardStyle)chip.Value).ToString() },
            "unit" => spec with { Unit = ((DisplayUnit)chip.Value).ToString() },
            "rate" => spec with { RateHz = (double)chip.Value },
            "format" => spec with { Format = (string)chip.Value },
            "priority" => spec with { Priority = ((SignalPriority)chip.Value).ToString() },
            _ => spec,
        };

        Apply(spec);
    }

    /// <summary>Delete this card and close the editor.</summary>
    [RelayCommand]
    private void Remove() => _dashboard.RemoveCardCommand.Execute(Card);

    /// <summary>Close the editor.</summary>
    [RelayCommand]
    private void Done() => _dashboard.CloseEditorCommand.Execute(null);

    partial void OnLabelChanged(string value)
    {
        if (_suspendApply)
        {
            return;
        }

        Apply(Card.Spec with { Label = value.Trim() });
    }

    /// <summary>
    /// Move a card to a different signal.
    /// </summary>
    /// <remarks>
    /// The rate, the unit and possibly the style all belonged to the old signal, so they are
    /// re-derived rather than carried over. Keeping a 4 Hz request when the card is switched
    /// from speed to ambient air temperature would silently spend the budget on a value that
    /// changes once a minute; keeping °F on a value now measured in km/h would just be wrong.
    /// The label is kept only if it was typed by hand.
    /// </remarks>
    private CardSpec RespecForSignal(CardSpec spec, string signalId)
    {
        var choice = _choices.FirstOrDefault(c => string.Equals(c.Id, signalId, StringComparison.Ordinal));

        if (choice is null)
        {
            return spec with { SignalId = signalId };
        }

        var keepStyle = spec.ParsedStyle is CardStyle.Bar && choice.HasRange;

        return spec with
        {
            SignalId = signalId,
            RateHz = choice.DefaultRateHz,
            Unit = DisplayUnit.Auto.ToString(),
            Style = (keepStyle ? CardStyle.Bar : CardStyle.Number).ToString(),
            Format = choice.Unit is "L/h" or "g/s" ? "0.0" : spec.Format,
        };
    }

    private void Apply(CardSpec spec)
    {
        _dashboard.CardEdited(Card, spec);
        Rebuild();
    }

    /// <summary>Rebuild every row and re-mark what is selected.</summary>
    private void Rebuild()
    {
        var spec = Card.Spec;
        var choice = Card.Choice;
        var hasRange = choice?.HasRange is true;

        Fill(Signals, [.. _choices.Select(c =>
            new OptionChip("signal", c.Caption, c.Id, c.Detail))], spec.SignalId);

        Fill(Widths,
            [
                new OptionChip("width", "1 COLUMN", 1),
                new OptionChip("width", "2 COLUMNS", 2),
            ],
            spec.Columns);

        Fill(Styles,
            [
                new OptionChip("style", "NUMBER", CardStyle.Number),
                new OptionChip("style", "BAR", CardStyle.Bar, hasRange ? null : "no range in the catalog", hasRange),
            ],
            spec.ParsedStyle);

        Fill(Units,
            [.. (choice?.UnitChoices ?? [DisplayUnit.Auto])
                .Select(u => new OptionChip("unit", DisplayUnits.Caption(u), u))],
            spec.ParsedUnit);

        Fill(Rates,
            [
                new OptionChip("rate", "CATALOG", 0.0),
                new OptionChip("rate", "0.2 Hz", 0.2),
                new OptionChip("rate", "0.5 Hz", 0.5),
                new OptionChip("rate", "1 Hz", 1.0),
                new OptionChip("rate", "2 Hz", 2.0),
                new OptionChip("rate", "4 Hz", 4.0),
            ],
            spec.RateHz);

        Fill(Formats,
            [
                new OptionChip("format", "0", "0"),
                new OptionChip("format", "0.0", "0.0"),
                new OptionChip("format", "0.00", "0.00"),
            ],
            spec.Format);

        Fill(Priorities,
            [
                new OptionChip("priority", "HIGH", SignalPriority.High),
                new OptionChip("priority", "NORMAL", SignalPriority.Normal),
                new OptionChip("priority", "LOW", SignalPriority.Low),
            ],
            spec.ParsedPriority);

        // The label follows the spec when the spec was changed for it — switching signals
        // must not leave the box showing a name that is no longer on the card.
        _suspendApply = true;
        Label = spec.Label;
        _suspendApply = false;

        OnPropertyChanged(nameof(HasUnitChoice));
        OnPropertyChanged(nameof(LabelPlaceholder));
        OnPropertyChanged(nameof(CostNote));
    }

    private static void Fill(ObservableCollection<OptionChip> target, IReadOnlyList<OptionChip> chips, object selected)
    {
        target.Clear();

        foreach (var chip in chips)
        {
            chip.IsSelected = Equals(chip.Value, selected);
            target.Add(chip);
        }
    }
}
