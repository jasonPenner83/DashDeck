using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashDeck.Abstractions;
using DashDeck.Host.Components;
using DashDeck.Host.Dash;

namespace DashDeck.Host.ViewModels;

/// <summary>
/// The arranged dash: the cards, the pages they flow into, and the editing of both.
/// </summary>
/// <remarks>
/// Replaces the six widgets the shell used to construct in its own constructor (F3, in part).
/// The cards now come from a file and go back to it, which is most of what the component host
/// will need to do later â€” it will add <em>where a card comes from</em>, and nothing here
/// assumes the answer is always "a built-in signal card".
/// <para>
/// <b>Only the visible page holds signal declarations.</b> That is the rule that makes "a
/// bunch of widget cards" safe to offer at all: every card is demand on one serialised link
/// whose real ceiling is unmeasured (Q12, ADR-0004), so paging withdraws what you cannot see
/// and gives the budget to what you can.
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly CardValueFactory _values;
    private readonly IReadOnlyList<ValueChoice> _choices;
    private readonly ComponentHost? _components;
    private readonly IDashboardStore _store;
    private readonly List<IDashCard> _cards = [];
    private readonly AddCardSlot _addSlot = new();

    private int _widgetBands = 2;
    private bool _loaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultiplePages))]
    private int _pageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditCaption))]
    private bool _isEditing;

    /// <summary>The card whose editor is open, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardEditorOpen))]
    private CardEditorViewModel? _editor;

    /// <summary>A component's full-screen detail, when one is open, or null.</summary>
    /// <remarks>
    /// The first use of the full-screen surface a component can offer (ADR-0023). Tapping a
    /// component card outside edit mode opens its detail here; it covers the widget region like
    /// Settings and the card editor do (Q17), the stage keeps running, and a back bar closes it.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComponentDetailOpen))]
    private System.Windows.FrameworkElement? _componentDetail;

    /// <summary>True while a component detail covers the dash.</summary>
    public bool IsComponentDetailOpen => ComponentDetail is not null;

    /// <summary>The name on the detail's back bar.</summary>
    public string ComponentDetailTitle { get; private set; } = string.Empty;

    /// <summary>The card whose detail is open, kept active while it is.</summary>
    private ComponentCardViewModel? _detailCard;

    public DashboardViewModel(
        CardValueFactory values,
        IReadOnlyList<ValueChoice> choices,
        ComponentHost? components = null,
        IDashboardStore? store = null)
    {
        _values = values;
        _choices = choices;
        _components = components;
        _store = store ?? new FileDashboardStore();

        // Null means never configured and gets the shipped six; an empty list means the user
        // removed every card, which is a choice and must survive a restart.
        var layout = _store.Load() ?? DashboardLayout.Default();

        foreach (var spec in layout.Cards)
        {
            _cards.Add(BuildCard(spec));
        }

        _loaded = true;
        Repack();
    }

    /// <summary>
    /// Turn a stored spec into the right kind of card.
    /// </summary>
    /// <remarks>
    /// The one place the source is dispatched on: a <c>Component</c> spec becomes a
    /// <see cref="ComponentCardViewModel"/> resolved against the host, everything else a
    /// <see cref="WidgetCardViewModel"/>. A component named by a card that is not installed
    /// resolves to null and the card renders itself as unavailable rather than vanishing.
    /// </remarks>
    private IDashCard BuildCard(CardSpec spec) => spec.ParsedSource == CardSource.Component
        ? new ComponentCardViewModel(spec, _components?.ById(spec.SignalId))
        : new WidgetCardViewModel(_values, spec, Find(spec));

    /// <summary>Every signal the loaded catalog defines, for the editor.</summary>
    public IReadOnlyList<ValueChoice> Choices => _choices;

    /// <summary>The pages, as rendered. Rebuilt whenever anything about the layout changes.</summary>
    public ObservableCollection<CardPage> Pages { get; } = [];

    /// <summary>True when there is more than one page, which is when the dots are worth drawing.</summary>
    public bool HasMultiplePages => Pages.Count > 1;

    /// <summary>True when the card editor is covering the screen.</summary>
    public bool IsCardEditorOpen => Editor is not null;

    /// <summary>What the edit control says.</summary>
    public string EditCaption => IsEditing ? "DONE" : "EDIT";

    /// <summary>True when there is nothing at all on the dash.</summary>
    public bool IsEmpty => _cards.Count == 0;

    /// <summary>Where the arrangement is stored. Shown in the editor.</summary>
    public static string StorePath => DashboardStore.Path;

    /// <summary>
    /// How many bands the widget region has right now, which the stage decides.
    /// </summary>
    /// <remarks>
    /// Setting it re-packs: a three-band stage leaves three widget rows rather than two, so
    /// the same cards page differently. This is also what finally answers F9 â€” the sixth band
    /// stopped being spare because rows are no longer fixed at two.
    /// </remarks>
    public int WidgetBands
    {
        get => _widgetBands;
        set
        {
            if (_widgetBands == value)
            {
                return;
            }

            _widgetBands = value;
            Repack();
        }
    }

    /// <summary>Enter or leave edit mode.</summary>
    [RelayCommand]
    private void ToggleEdit()
    {
        IsEditing = !IsEditing;

        // The add tile joins and leaves the flow, so the pages genuinely change shape.
        Repack();
    }

    /// <summary>Add a card and open its editor straight away.</summary>
    /// <remarks>
    /// A card added blank would be a card bound to nothing, so it starts on the first signal
    /// not already on the dash â€” which is usually the one being reached for, and is never
    /// wrong enough to matter since the editor opens on top of it.
    /// </remarks>
    [RelayCommand]
    private void AddCard()
    {
        var taken = _cards.Select(c => c.Spec.SignalId).ToHashSet(StringComparer.Ordinal);
        var choice = _choices.FirstOrDefault(c => !taken.Contains(c.Id)) ?? _choices.FirstOrDefault();

        if (choice is null)
        {
            return;
        }

        var card = new WidgetCardViewModel(
            _values,
            new CardSpec
            {
                SignalId = choice.Id,
                Source = choice.Source.ToString(),
                RateHz = choice.DefaultRateHz,
                Format = SuggestFormat(choice),
            },
            choice);

        _cards.Add(card);
        Save();
        Repack();

        // Land on the page the new card went to, so it is not added out of sight.
        PageIndex = IndexOfPageContaining(card);
        OpenEditor(card);
    }

    /// <summary>Remove a card.</summary>
    [RelayCommand]
    private void RemoveCard(WidgetCardViewModel? card)
    {
        if (card is null || !_cards.Remove(card))
        {
            return;
        }

        card.Dispose();
        CloseEditor();
        Save();
        Repack();
    }

    /// <summary>Move a card one place earlier in the flow.</summary>
    [RelayCommand]
    private void MoveEarlier(WidgetCardViewModel? card) => Move(card, -1);

    /// <summary>Move a card one place later in the flow.</summary>
    [RelayCommand]
    private void MoveLater(WidgetCardViewModel? card) => Move(card, +1);

    /// <summary>Put the shipped six back.</summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        foreach (var card in _cards)
        {
            card.Dispose();
        }

        _cards.Clear();

        foreach (var spec in DashboardLayout.Default().Cards)
        {
            _cards.Add(BuildCard(spec));
        }

        CloseEditor();
        PageIndex = 0;
        Save();
        Repack();
    }

    /// <summary>Open a card for editing. Only reachable from edit mode.</summary>
    [RelayCommand]
    private void OpenEditor(WidgetCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        // Kept live while its editor is open, so the preview shows real readings rather than
        // a placeholder for however long you are choosing a format.
        card.Activate();
        Editor = new CardEditorViewModel(this, card, _choices);
    }

    /// <summary>Close the editor and go back to the dash.</summary>
    [RelayCommand]
    private void CloseEditor()
    {
        Editor = null;

        // Whatever was activated for the preview goes back to being governed by the page.
        ApplyActivation();
    }

    /// <summary>Open a component's full-screen detail. Bound to a tap on a component card.</summary>
    [RelayCommand]
    private void OpenComponentDetail(ComponentCardViewModel? card)
    {
        if (card is null || !card.HasFullScreen || card.CreateDetail() is not { } detail)
        {
            return;
        }

        // The detail is fed by the component behind the card, so that card must stay active
        // while the detail is up — even though opening it hides the dash and would otherwise
        // deactivate it. Same exemption the edited card gets, one surface along.
        _detailCard = card;
        card.Activate();

        ComponentDetailTitle = card.Title;
        OnPropertyChanged(nameof(ComponentDetailTitle));
        ComponentDetail = detail;
    }

    /// <summary>Close the component detail and go back to the dash.</summary>
    [RelayCommand]
    private void CloseComponentDetail()
    {
        _detailCard = null;
        ComponentDetail = null;

        // Back to being governed by the page — the card deactivates if it is off the visible one.
        ApplyActivation();
    }

    /// <summary>Show the next page, if there is one.</summary>
    [RelayCommand]
    private void NextPage() => PageIndex = Math.Min(PageIndex + 1, Pages.Count - 1);

    /// <summary>Show the previous page, if there is one.</summary>
    [RelayCommand]
    private void PreviousPage() => PageIndex = Math.Max(PageIndex - 1, 0);

    /// <summary>Show a page by number. Bound to the dots.</summary>
    [RelayCommand]
    private void GoToPage(object? index)
    {
        if (index is int i)
        {
            PageIndex = Math.Clamp(i, 0, Pages.Count - 1);
        }
        else if (index is string s && int.TryParse(s, out var parsed))
        {
            PageIndex = Math.Clamp(parsed, 0, Pages.Count - 1);
        }
    }

    /// <summary>
    /// Open the editor on a card by position. For the <c>--edit-card</c> development flag.
    /// </summary>
    public void OpenCardAt(int index)
    {
        if (index >= 0 && index < _cards.Count)
        {
            OpenEditor(_cards[index] as WidgetCardViewModel);
        }
    }

    /// <summary>
    /// Open the component detail for the card at a position. For the <c>--detail</c> flag, since
    /// the detail is otherwise a tap and a screenshot cannot tap.
    /// </summary>
    public void OpenComponentDetailAt(int index)
    {
        if (index >= 0 && index < _cards.Count && _cards[index] is ComponentCardViewModel card)
        {
            OpenComponentDetail(card);
        }
    }

    /// <summary>
    /// Adopt an edited card. The editor's only way back in.
    /// </summary>
    /// <remarks>
    /// One entry point rather than the editor mutating a card directly, so that saving,
    /// re-packing and re-activating cannot be forgotten for one kind of edit and remembered
    /// for another.
    /// </remarks>
    public void CardEdited(WidgetCardViewModel card, CardSpec spec)
    {
        card.Apply(spec, Find(spec));
        Save();

        // A width change re-flows everything after it, so this is not merely cosmetic.
        Repack();
        PageIndex = IndexOfPageContaining(card);
    }

    partial void OnPageIndexChanged(int value) => ApplyActivation();

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var card in _cards)
        {
            card.Dispose();
        }

        _cards.Clear();
    }

    /// <summary>
    /// Find what a card is bound to, matching the source as well as the id.
    /// </summary>
    /// <remarks>
    /// Both, not just the id. The sensor catalog already names <c>vehicle.heading</c> as the
    /// signal it would prefer, so the day that PID is found the same id means two different
    /// things in two catalogs — matching on the id alone would silently hand a card the wrong
    /// one, and it would look like the value had simply started reading oddly.
    /// </remarks>
    private ValueChoice? Find(CardSpec spec) => _choices.FirstOrDefault(c =>
        c.Source == spec.ParsedSource && string.Equals(c.Id, spec.SignalId, StringComparison.Ordinal));

    /// <summary>A starting format that suits the signal, so a new card is not born ugly.</summary>
    private static string SuggestFormat(ValueChoice choice) =>
        choice.Unit is "L/h" or "g/s" ? "0.0" : "0";

    private void Move(WidgetCardViewModel? card, int delta)
    {
        if (card is null)
        {
            return;
        }

        var from = _cards.IndexOf(card);
        var to = from + delta;

        if (from < 0 || to < 0 || to >= _cards.Count)
        {
            return;
        }

        _cards.RemoveAt(from);
        _cards.Insert(to, card);

        Save();
        Repack();
        PageIndex = IndexOfPageContaining(card);
    }

    /// <summary>
    /// Rebuild the pages from the ordered cards.
    /// </summary>
    /// <remarks>
    /// Everything that can change the shape of the dash funnels through here â€” adding,
    /// removing, reordering, resizing a card, entering edit mode, and the stage claiming a
    /// different number of bands. One path means the activation rule below is applied exactly
    /// once per change and cannot be forgotten at a call site.
    /// </remarks>
    private void Repack()
    {
        if (!_loaded)
        {
            return;
        }

        var slots = new List<IDashSlot>(_cards);

        if (IsEditing)
        {
            slots.Add(_addSlot);
        }

        var pages = CardPacker.Pack(slots, BandGrid.RowsIn(_widgetBands));

        Pages.Clear();

        foreach (var page in pages)
        {
            Pages.Add(page);
        }

        // Removing the last card on the last page must not leave the view on a page that no
        // longer exists.
        var clamped = Math.Clamp(PageIndex, 0, Pages.Count - 1);

        if (clamped != PageIndex)
        {
            PageIndex = clamped;
        }
        else
        {
            ApplyActivation();
        }

        OnPropertyChanged(nameof(HasMultiplePages));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Declare the signals on the visible page and withdraw the rest.
    /// </summary>
    /// <remarks>
    /// The card being edited is exempt: its editor shows a live preview, and a preview of a
    /// suspended card would show the placeholder for as long as you looked at it.
    /// </remarks>
    private void ApplyActivation()
    {
        var visible = PageIndex >= 0 && PageIndex < Pages.Count
            ? Pages[PageIndex].Slots.OfType<IDashCard>().ToHashSet()
            : [];

        foreach (var card in _cards)
        {
            // Exempt from deactivation: the card being edited (its editor previews it live) and
            // the card whose full-screen detail is open (the detail is fed by it).
            if (visible.Contains(card) || ReferenceEquals(card, Editor?.Card) || ReferenceEquals(card, _detailCard))
            {
                card.Activate();
            }
            else
            {
                card.Deactivate();
            }
        }
    }

    private int IndexOfPageContaining(IDashCard card)
    {
        for (var i = 0; i < Pages.Count; i++)
        {
            if (Pages[i].Slots.Contains(card))
            {
                return i;
            }
        }

        return PageIndex;
    }

    private void Save() =>
        _store.Save(new DashboardLayout { Cards = [.. _cards.Select(c => c.Spec)] });
}



