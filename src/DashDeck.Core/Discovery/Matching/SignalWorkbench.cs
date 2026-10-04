using DashDeck.Core.Catalog;

namespace DashDeck.Core.Discovery.Matching;

/// <summary>What removing a signal would do, for the button that offers it.</summary>
public enum RemoveKind
{
    /// <summary>A built-in signal: it can be hidden, never removed.</summary>
    None,

    /// <summary>One of yours, with no built-in under it: removed.</summary>
    Remove,

    /// <summary>Your correction of a built-in: removing it puts the built-in back.</summary>
    Revert,
}

/// <summary>
/// Adding, editing, hiding and removing DashDeck's signals, as edits to the user's overlay
/// (ADR-0051). Pure: every operation takes the overlay and returns the new one.
/// </summary>
/// <remarks>
/// <b>The overlay is the only thing written.</b> The built-in catalog — standard plus vehicle pack —
/// is never touched, so nothing here can break what ships. Hiding a built-in writes a copy of it
/// marked hidden; unhiding drops that copy again when nothing else about it was changed. Removing
/// means removing <em>your</em> entry: a signal of yours goes, a correction of a built-in reverts
/// to the built-in, and a plain built-in cannot be removed at all — it can be hidden.
/// </remarks>
public static class SignalWorkbench
{
    /// <summary>Put a definition in the overlay, replacing any with the same id.</summary>
    public static IReadOnlyList<SignalDefinition> Upsert(IEnumerable<SignalDefinition> overlay, SignalDefinition definition) =>
        [.. overlay.Where(d => d.Id != definition.Id), definition];

    /// <summary>Hide a signal: the overlay's own entry marked hidden, or a hidden copy of the built-in.</summary>
    public static IReadOnlyList<SignalDefinition> Hide(IEnumerable<SignalDefinition> overlay, SignalDefinition current) =>
        Upsert(overlay, current with { Hidden = true });

    /// <summary>
    /// Unhide: when the overlay entry is only a hidden copy of the built-in, it goes; otherwise it
    /// stays, unhidden.
    /// </summary>
    public static IReadOnlyList<SignalDefinition> Unhide(IEnumerable<SignalDefinition> overlay, string id, SignalDefinition? builtIn)
    {
        var list = overlay.ToList();
        var mine = list.FirstOrDefault(d => d.Id == id);
        if (mine is null)
        {
            return list;
        }

        var shown = mine with { Hidden = false };
        return builtIn is not null && shown == builtIn
            ? [.. list.Where(d => d.Id != id)]
            : Upsert(list, shown);
    }

    /// <summary>What removing <paramref name="id"/> would do.</summary>
    public static RemoveKind CanRemove(IEnumerable<SignalDefinition> overlay, string id, bool isBuiltIn) =>
        !overlay.Any(d => d.Id == id) ? RemoveKind.None
        : isBuiltIn ? RemoveKind.Revert
        : RemoveKind.Remove;

    /// <summary>Remove your entry for <paramref name="id"/>: your signal goes, or a built-in comes back.</summary>
    public static IReadOnlyList<SignalDefinition> Remove(IEnumerable<SignalDefinition> overlay, string id) =>
        [.. overlay.Where(d => d.Id != id)];

    /// <summary>
    /// An edited definition, and whether it is still a measured one.
    /// </summary>
    /// <remarks>
    /// Changing where it comes from or how it decodes — bus, module, mode, PID, bytes, scale,
    /// offset — makes it a typed definition, unconfirmed until TEST. Renaming it, recategorising it,
    /// or changing its range, rate or unit label does not: those are presentation, and the truck's
    /// answer is the same. A definition that was already unconfirmed stays so.
    /// </remarks>
    public static SignalDefinition Edited(SignalDefinition before, SignalDefinition after) =>
        after with
        {
            Unconfirmed = before.Unconfirmed || !SameSource(before, after),
            Placeholder = before.Placeholder && SameSource(before, after),
            Hidden = before.Hidden,
        };

    /// <summary>True when two definitions ask the same question and decode the answer the same way.</summary>
    public static bool SameSource(SignalDefinition a, SignalDefinition b) =>
        a.Bus == b.Bus && a.Mode == b.Mode && a.Pid == b.Pid && a.ModuleAddress == b.ModuleAddress
        && a.Decode.ByteOffset == b.Decode.ByteOffset && a.Decode.ByteLength == b.Decode.ByteLength
        && a.Decode.Signed == b.Decode.Signed && a.Decode.Scale.Equals(b.Decode.Scale)
        && a.Decode.Offset.Equals(b.Decode.Offset) && a.Decode.Mask == b.Decode.Mask;
}
