using System.Globalization;
using System.Windows;
using DashDeck.Abstractions;
using DashDeck.Core.Catalog;

namespace DashDeck.IdMatcher;

/// <summary>
/// Add or edit one DashDeck signal (ADR-0051). Hex for module, mode and PID, as FORScan and the
/// traffic list show them.
/// </summary>
public partial class SignalEditorWindow : Window
{
    private readonly Func<string, bool> _idTaken;
    private readonly bool _isNew;

    public SignalEditorWindow(SignalDefinition start, bool isNew, string intro, Func<string, bool> idTaken)
    {
        InitializeComponent();
        _isNew = isNew;
        _idTaken = idTaken;
        Intro.Text = intro;
        Title = isNew ? "New DashDeck signal" : $"Edit {start.Id}";

        var inv = CultureInfo.InvariantCulture;
        IdBox.Text = start.Id;
        IdBox.IsReadOnly = !isNew;
        NameBox.Text = start.Name;
        CategoryBox.Text = start.Category;
        HsRadio.IsChecked = start.Bus == CanBus.Hs;
        P311Radio.IsChecked = start.Bus == CanBus.Ms;
        ModuleBox.Text = start.Module ?? "";
        ModeBox.Text = start.Mode.ToString("X2", inv);
        PidBox.Text = start.Pid.ToString(start.Pid <= 0xFF && start.Mode == 0x01 ? "X2" : "X4", inv);
        ByteOffsetBox.Text = start.Decode.ByteOffset.ToString(inv);
        LengthBox.SelectedIndex = start.Decode.ByteLength switch { 2 => 1, 4 => 2, _ => 0 };
        SignedBox.IsChecked = start.Decode.Signed;
        ScaleBox.Text = start.Decode.Scale.ToString("0.##########", inv);
        OffsetBox.Text = start.Decode.Offset.ToString("0.##########", inv);
        UnitBox.Text = start.Decode.Unit;
        MinBox.Text = start.Min?.ToString("0.###", inv) ?? "";
        MaxBox.Text = start.Max?.ToString("0.###", inv) ?? "";
        RateBox.Text = start.DefaultRateHz.ToString("0.###", inv);
        _start = start;

        Loaded += (_, _) => (isNew ? IdBox : NameBox).Focus();
    }

    /// <summary>What it started from: everything the form has no field for is carried over from it.</summary>
    private readonly SignalDefinition _start;

    /// <summary>The definition saved, or null when cancelled.</summary>
    public SignalDefinition? Result { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var problems = new List<string>();
        var inv = CultureInfo.InvariantCulture;

        bool Hex(string text, int max, out int value) =>
            int.TryParse(text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.AllowHexSpecifier, inv, out value)
            && value >= 0 && value <= max;

        double Number(string text, string what)
        {
            if (double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, inv, out var v))
            {
                return v;
            }

            problems.Add($"{what} must be a number");
            return 0;
        }

        double? Optional(string text, string what) => text.Trim().Length == 0 ? null : Number(text, what);

        var id = IdBox.Text.Trim();
        if (id.Length == 0)
        {
            problems.Add("give it an id, like ford.transmissionTemp");
        }
        else if (_isNew && _idTaken(id))
        {
            problems.Add($"'{id}' already exists — edit that one instead");
        }

        if (!Hex(ModeBox.Text, 0xFF, out var mode) || mode == 0)
        {
            problems.Add("mode must be hex, 01–FF");
        }

        if (!Hex(PidBox.Text, 0xFFFF, out var pid))
        {
            problems.Add("PID must be hex, up to four digits");
        }

        var module = ModuleBox.Text.Trim().ToUpperInvariant();
        if (module.Length > 0 && SignalDefinition.ParseModule(module) is null)
        {
            problems.Add("module must be a module address in hex (700–7F7), or empty for the broadcast");
        }

        if (!int.TryParse(ByteOffsetBox.Text.Trim(), NumberStyles.Integer, inv, out var byteOffset) || byteOffset < 0)
        {
            problems.Add("the first byte must be 0 or more");
        }

        var length = LengthBox.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 };
        var scale = Number(ScaleBox.Text, "the scale");
        var offset = Number(OffsetBox.Text, "the offset");
        var rate = Number(RateBox.Text, "readings a second");
        var min = Optional(MinBox.Text, "the bottom of the range");
        var max = Optional(MaxBox.Text, "the top of the range");

        if (problems.Count == 0)
        {
            var definition = _start with
            {
                Id = id,
                Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : id,
                Category = CategoryBox.Text.Trim().Length > 0 ? CategoryBox.Text.Trim() : "Other",
                Bus = P311Radio.IsChecked == true ? CanBus.Ms : CanBus.Hs,
                Mode = (byte)mode,
                Pid = (ushort)pid,
                Module = module.Length > 0 ? module : null,
                Decode = new DecodeSpec(byteOffset, length, SignedBox.IsChecked == true, scale, offset, UnitBox.Text.Trim(), _start.Decode.Mask),
                DefaultRateHz = rate,
                Min = min,
                Max = max,
            };

            problems.AddRange(SignalCatalog.Check(definition));

            if (problems.Count == 0)
            {
                Result = definition;
                DialogResult = true;
                return;
            }
        }

        Problem.Text = string.Join("; ", problems.Select(p => char.ToUpperInvariant(p[0]) + p[1..])) + ".";
    }
}
