using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Excise.App.Services;

/// <summary>
/// A single reversible in-session edit. The forward mutation has ALREADY been
/// applied at the call site when the entry is recorded; <see cref="UndoAsync"/>
/// reverts it and <see cref="RedoAsync"/> re-applies it.
/// </summary>
public sealed class EditHistoryEntry
{
    private readonly Func<Task> _undo;
    private readonly Func<Task> _redo;

    public EditHistoryEntry(string description, Func<Task> undo, Func<Task> redo)
    {
        Description = description ?? throw new ArgumentNullException(nameof(description));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
        _redo = redo ?? throw new ArgumentNullException(nameof(redo));
    }

    public string Description { get; }

    internal Task UndoAsync() => _undo();
    internal Task RedoAsync() => _redo();
}

/// <summary>
/// App-level, in-session undo/redo history (#782). Command pattern: each
/// reversible mutation (type-over edits, annotation authoring, page
/// reorder/rotate/delete) records an entry carrying closures that revert and
/// re-apply it. The stack is deliberately bounded to the REVERSIBLE, pre-commit
/// editing state — content already flattened/baked into the PDF content stream
/// on save is irreversible by design and is never pushed here, which is why the
/// history is cleared on every save, open, and close.
/// </summary>
public sealed class EditHistoryService
{
    private readonly Stack<EditHistoryEntry> _undo = new();
    private readonly Stack<EditHistoryEntry> _redo = new();
    private bool _isReplaying;

    /// <summary>Raised whenever the stacks change (push, undo, redo, clear).</summary>
    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.Count > 0 ? _undo.Peek().Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    /// <summary>
    /// True while an undo or redo is being replayed. Recording is suppressed
    /// during replay so the low-level mutators a closure calls cannot push new
    /// entries (belt-and-suspenders against re-entrancy — closures should call
    /// the low-level mutators directly, not the recording wrappers).
    /// </summary>
    public bool IsReplaying => _isReplaying;

    /// <summary>Record an already-applied mutation with async revert/re-apply.</summary>
    public void Push(string description, Func<Task> undo, Func<Task> redo)
    {
        if (_isReplaying)
            return;

        _undo.Push(new EditHistoryEntry(description, undo, redo));
        if (_redo.Count > 0)
            _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Record an already-applied mutation with synchronous revert/re-apply.</summary>
    public void Push(string description, Action undo, Action redo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(redo);
        Push(
            description,
            () => { undo(); return Task.CompletedTask; },
            () => { redo(); return Task.CompletedTask; });
    }

    public async Task UndoAsync()
    {
        if (_undo.Count == 0)
            return;

        var entry = _undo.Pop();
        _isReplaying = true;
        try
        {
            await entry.UndoAsync();
        }
        finally
        {
            _isReplaying = false;
        }

        _redo.Push(entry);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RedoAsync()
    {
        if (_redo.Count == 0)
            return;

        var entry = _redo.Pop();
        _isReplaying = true;
        try
        {
            await entry.RedoAsync();
        }
        finally
        {
            _isReplaying = false;
        }

        _undo.Push(entry);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drop all history. Called on document open/close and after save/flatten.</summary>
    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0)
            return;

        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
