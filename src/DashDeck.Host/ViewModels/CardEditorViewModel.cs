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
    public OptionChip(string group, string caption, object value, string? detail = null, bool enabled = true, string category = "")
    {
        Group = group;
        Caption = caption;
        Value = value;
        Detail = detail;
        IsEnabled = enabled;
        Category = category;
    }

    /// <summary>Which setting this chip belongs to. Lets one command serve every row.</summary>
    public string Group { get; }

    /// <summary>Function group, for the signal picker to sort chips under. Empty for others.</summary>
    public string Category { get; }

    /// <summary>What the chip says.</summary>
    public string Caption { get; }

    /// <summary>A second, quieter line. Used by the signal picker for the id and unit.</summary>
    public string? Detail { get; }

    /// <summary>True when this line is worth drawing.</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>The value this chip selects.</summary>
    public object Value { get; }

    /// <summary>False for an option this signal cannot offer â€” a bar with no range to fill.</summary>
    public bool IsEnabled { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One category of signal chips in the picker: a header and the chips under it.</summary>
public sealed record SignalGroup(string Name, IReadOnlyList<OptionChip> Chips);

/// <summary>
/// Editing one card: what it shows, how it shows it, and how often it asks.
/// </summary>
/// <remarks>
/// <b>There is no Cancel, and every change applies at once.</b> The rest of the app already
/// works this way â€” a theme or an accent is written the moment it changes, because a dash is
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
    private readonly IReadOnlyList<ValueChoice> _choices;
    private bool _suspendApply;

    public CardEditorViewModel(
        DashboardViewModel dashboard,
        WidgetCardViewModel card,
        IReadOnlyList<ValueChoice> choices)
    {
        _dashboard = dashboard;
        _choices = choices;
        Card = card;

        _label = card.Spec.Label;
        Rebuild();
    }

    /// <summary>
    /// The signal chips, grouped by category and narrowed by the search box.
    /// </summary>
    /// <remarks>
    /// A dash that can be pointed at three dozen signals is a wall of chips without this: the
    /// groups make it browsable by function, the search box findable by name. Grouped data
    /// rather than a <c>GroupStyle</c> because the layout is a header over a wrap of chips, which
    /// nested <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>s express
    /// directly and a grouped <c>ItemsControl</c> fights.
    /// </remarks>
    public ObservableCollection<SignalGroup> SignalGroups { get; } = [];

    /// <summary>The picker's search text. Narrows the signals to those whose name or id matches.</summary>
    [ObservableProperty]
    private string _signalSearch = string.Empty;

    partial void OnSignalSearchChanged(string value) => RebuildSignalGroups();

    private void RebuildSignalGroups()
    {
        var needle = SignalSearch?.Trim() ?? string.Empty;

        var groups = Signals
            .Where(c => Matches(c, needle))
            .GroupBy(c => c.Category)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SignalGroup(
                g.Key,
                [.. g.OrderBy(c => c.Caption, StringComparer.OrdinalIgnoreCase)]));

        SignalGroups.Clear();

        foreach (var group in groups)
        {
            SignalGroups.Add(group);
        }
    }

    private static bool Matches(OptionChip chip, string needle) =>
        // The name, the category and the id (carried in the detail line), so "temp", "fuel" and
        // "vehicle.speed" all find what a person would expect.
        needle.Length == 0
        || chip.Caption.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || chip.Category.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || (chip.Detail?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);

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
    /// the dash shares one serialised link, so a rate is a claim on everyone else â€” showing
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
                $"Asking for {requested:0.##} Hz on a link every card shares. {Card.FooterText}.");
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
            "signal" => RespecForValue(spec, (ValueChoice)chip.Value),
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
    /// changes once a minute; keeping Â°F on a value now measured in km/h would just be wrong.
    /// <para>
    /// <b>The label goes too</b>, and that is worth the annoyance of retyping a good one. It
    /// was kept at first, on the reasoning that a hand-typed caption is worth preserving â€”
    /// which produced a card reading <c>RPM</c> above a coolant temperature within minutes of
    /// the feature being used. A label that names the wrong quantity is the exact failure this
    /// project refuses everywhere else, and the card is never left nameless: an empty label
    /// falls back to the new signal's own name.
    /// </para>
    /// </remarks>
    private CardSpec RespecForValue(CardSpec spec, ValueChoice choice)
    {
        var keepStyle = spec.ParsedStyle is CardStyle.Bar && choice.HasRange;

        return spec with
        {
            SignalId = choice.Id,
            Source = choice.Source.ToString(),
            Label = string.Empty,
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

        // The chip carries the choice, not its id. Ids are no longer unique across the two
        // catalogs -- the sensor catalog names vehicle.heading as the signal it prefers, so
        // one day both will define it -- and a picker keyed on the id alone would offer two
        // indistinguishable chips and select the wrong one.
        // Hidden and unconfirmed signals are not offered (ADR-0051) — except the one this card already
        // shows, so editing a card never silently changes what it is pointed at.
        Fill(Signals, [.. _choices.Where(c => c.Offered || ReferenceEquals(c, choice)).Select(c =>
            new OptionChip("signal", c.Caption, c, c.Detail, category: c.Category))], Card.Choice);

        // Re-group from the freshly built chips, keeping whatever the search box is filtering to.
        RebuildSignalGroups();

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

        // The label follows the spec when the spec was changed for it â€” switching signals
        // must not leave the box showing a name that is no longer on the card.
        _suspendApply = true;
        Label = spec.Label;
        _suspendApply = false;

        OnPropertyChanged(nameof(HasUnitChoice));
        OnPropertyChanged(nameof(LabelPlaceholder));
        OnPropertyChanged(nameof(CostNote));
    }

    private static void Fill(ObservableCollection<OptionChip> target, IReadOnlyList<OptionChip> chips, object? selected)
    {
        target.Clear();

        foreach (var chip in chips)
        {
            chip.IsSelected = Equals(chip.Value, selected);
            target.Add(chip);
        }
    }
}



