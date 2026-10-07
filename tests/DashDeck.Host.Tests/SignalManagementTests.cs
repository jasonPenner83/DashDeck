using DashDeck.Abstractions;
using DashDeck.Core.Catalog;
using DashDeck.Host.Dash;
using DashDeck.Host.ViewModels;
using DashDeck.Vehicle;

namespace DashDeck.Host.Tests;

/// <summary>
/// Hidden and unconfirmed signals on the dash (ADR-0051): left out of the card picker, and a typed
/// one confirmed only by a TEST that answered what is being saved.
/// </summary>
public sealed class SignalManagementTests
{
    private static SignalDefinition Signal(string id, bool hidden = false, bool unconfirmed = false) => new()
    {
        Id = id,
        Name = id,
        Pid = 0x0C,
        Decode = new DecodeSpec(0, 2, false, 0.25, 0, "rpm"),
        Hidden = hidden,
        Unconfirmed = unconfirmed,
    };

    [Fact]
    public void Hidden_and_unconfirmed_signals_are_not_offered_in_the_picker()
    {
        var catalog = SignalCatalog.FromDefinitions([
            Signal("a.shown"),
            Signal("b.hidden", hidden: true),
            Signal("c.typed", unconfirmed: true),
        ]);

        var choices = ValueChoice.From(catalog).ToDictionary(c => c.Id);

        Assert.True(choices["a.shown"].Offered);
        Assert.False(choices["b.hidden"].Offered);
        Assert.False(choices["c.typed"].Offered);
    }

    private static SignalEditorViewModel Editor(SignalDefinition start, Func<PidRequest, PidResponse> answer, List<SignalDefinition> saved) =>
        new(start, isNew: false, SignalOrigin.Yours, note: null,
            (request, _) => Task.FromResult(answer(request)),
            _ => false, _ => null, saved.Add, () => { }, null);

    [Fact]
    public async Task A_typed_signal_saved_after_test_answered_it_is_confirmed()
    {
        var saved = new List<SignalDefinition>();
        var editor = Editor(Signal("c.typed", unconfirmed: true), r => PidResponse.Ok(r, [0x1A, 0xF8], DateTimeOffset.UnixEpoch), saved);

        await editor.TestCommand.ExecuteAsync(null);
        editor.SaveCommand.Execute(null);

        Assert.False(Assert.Single(saved).Unconfirmed);
    }

    [Fact]
    public void A_typed_signal_saved_without_test_stays_unconfirmed()
    {
        var saved = new List<SignalDefinition>();
        var editor = Editor(Signal("c.typed", unconfirmed: true), r => PidResponse.Ok(r, [0x1A, 0xF8], DateTimeOffset.UnixEpoch), saved);

        editor.SaveCommand.Execute(null);

        Assert.True(Assert.Single(saved).Unconfirmed);
        Assert.Contains("TEST", editor.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_that_got_no_answer_does_not_confirm()
    {
        var saved = new List<SignalDefinition>();
        var editor = Editor(Signal("c.typed", unconfirmed: true), r => PidResponse.Failed(r, PidFailure.NoData, DateTimeOffset.UnixEpoch), saved);

        await editor.TestCommand.ExecuteAsync(null);
        editor.SaveCommand.Execute(null);

        Assert.True(Assert.Single(saved).Unconfirmed);
    }

    [Fact]
    public async Task Changing_the_pid_after_test_needs_another_test()
    {
        var saved = new List<SignalDefinition>();
        var editor = Editor(Signal("c.typed", unconfirmed: true), r => PidResponse.Ok(r, [0x1A, 0xF8], DateTimeOffset.UnixEpoch), saved);

        await editor.TestCommand.ExecuteAsync(null);
        editor.PidText = "0D";
        editor.SaveCommand.Execute(null);

        Assert.True(Assert.Single(saved).Unconfirmed);
    }

    [Fact]
    public void Saving_a_hidden_signal_from_the_editor_keeps_it_hidden()
    {
        var saved = new List<SignalDefinition>();
        var editor = Editor(Signal("b.hidden", hidden: true), r => PidResponse.Ok(r, [1, 2], DateTimeOffset.UnixEpoch), saved);

        editor.Name = "Renamed";
        editor.SaveCommand.Execute(null);

        Assert.True(Assert.Single(saved).Hidden);
    }
}
