using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace PhoneWave;

public class PhoneWaveTransaction : IDisposable
{
    private readonly PhoneWaveContext _context;
    private readonly object? _tag;
    private bool _disposed;

    internal PhoneWaveTransaction(PhoneWaveContext context, object? tag)
    {
        _context = context;
        _tag = tag;
    }

    Dictionary<object, TransactionChange> _changes = new();

    // Set once the abort replay starts. The aborting transaction stays the context's
    // ActiveTransaction through the replay (see ReplayGuarded), so without this guard an observer
    // write would land in _changes while the abort enumerates it — and, if it survived, become a
    // permanent unrecorded change.
    private bool _aborting;

    private void ThrowIfAborting()
    {
        if (_aborting)
            throw new InvalidOperationException("Attempted to change the state while the transaction is aborting");
    }

    internal void Set<T>(PhoneWavePropertyStorage<T> storage, T updated)
    {
        ThrowIfAborting();
        if (!_changes.TryGetValue(storage, out var change))
            _changes[storage] = new TransactionChange<T>(storage, updated);
        else
            ((TransactionChange<T>)change).SetValue(updated);
    }

    public bool TryGetChange(object key,
#if NET6_0_OR_GREATER
        [MaybeNullWhen(false)] 
#endif
    out TransactionChange change) 
        => _changes.TryGetValue(key, out change);

    public void AddChange(object key, TransactionChange firstChange)
    {
        ThrowIfAborting();
        if (TryGetChange(key, out _))
            throw new InvalidOperationException("Attempted to add with a change already present");
        _changes[key] = firstChange; 
    }

    public void Commit()
    {
        if(_disposed)
            return;
        _disposed = true;
        _context.AddRecord(new PhoneWaveRecordedTransaction(_changes.Values.ToList(), _tag));

        _context.ActiveTransaction = null;
    }

    public void Rollback()
    {
        if(_disposed)
            return;
        _disposed = true;
        ReplayGuarded();
    }

    public void Dispose() => Rollback();

    /// <summary>
    /// Replays this transaction's changes as an ABORT. The aborting transaction itself stays the
    /// context's ActiveTransaction THROUGH the replay: a notification raised by a replayed change
    /// must not begin a new transaction against an intermediate state (BeginTransaction also
    /// rejects while IsRolling). The guard clears in finally BEFORE the outermost completion
    /// notification, so completion observers transact only against the final state. Replay errors
    /// propagate — never swallowed; a completion failure is aggregated, never masking the primary.
    /// </summary>
    private void ReplayGuarded()
    {
        var context = _context;
        // From here on the transaction accepts no further changes: a notification observer that
        // writes during the abort fails loudly instead of growing _changes mid-enumeration.
        _aborting = true;
        PhoneWaveContext.AbortRollScope? roll = null;
        // Chronological failures: entry first, then one per failing change (each original error
        // retained; a failed change is never retried). No list is allocated on the clean path.
        List<Exception>? failures = null;
        // The FINAL combined failure is computed EXACTLY ONCE — in the outer finally, from the
        // single chronological list. Never folded into itself; entry/replay/completion each
        // appear once.
        ExceptionDispatchInfo? primary = null;
        try
        {
            // Acquisition can itself throw (a rising-edge IsRolling observer). The uncommitted
            // changes MUST still roll back — the transaction is already disposed and cannot be
            // retried — so on an entry failure the abort re-enters through the NON-ANNOUNCING
            // variant (the failing observer has already seen its one rising edge; the depth was
            // reset by BeginRoll's cleanup) and replays under that guarded scope.
            try
            {
                roll = context.BeginAbortRoll();
            }
            catch (Exception entryFailure)
            {
                (failures ??= []).Add(entryFailure);
                roll = context.BeginAbortRollWithoutEntryNotification();
            }

            // Per-change error capture: ONE failure aborts THAT change only — every remaining
            // independent change still attempts its rollback exactly once. A failed change is
            // never retried; later changes are never stranded. The replay walks a SNAPSHOT, so even
            // a change the guard above cannot see can never break the enumeration itself.
            ReplayFailures.ApplyEach(_changes.Values.ToArray(), static change => change.Rollback(), ref failures);
        }
        finally
        {
            // The guard clears BEFORE the completion notification (the roll's Dispose below).
            context.ActiveTransaction = null;

            if (roll is not null)
            {
                // The completion observer must see the FINAL combined failure — entry-only,
                // replay-only, or the chronological aggregate — so a throwing observer aggregates
                // with it instead of masking an earlier stage.
                primary = ReplayFailures.Combine(failures, "Abort replay failed at multiple stages.");
                roll.PrimaryReplayFailure = primary?.SourceException;
                try
                {
                    roll.Dispose();
                }
                catch (Exception endRollFailure)
                {
                    // EndRoll is the SOLE completion aggregator: its error is ALREADY the final
                    // combined failure ([...earlier stages, completion]). Capture it as the new
                    // primary — do NOT append it to the list or re-run Combine, which would
                    // duplicate the earlier stages.
                    primary = ExceptionDispatchInfo.Capture(endRollFailure);
                }
            }
        }

        // Restored and completed: surface the original failure with its original stack.
        primary?.Throw();
    }
}

/// <summary>
/// Stores a completed, now-immutable transaction
/// </summary>
public class PhoneWaveRecordedTransaction
{
    private readonly List<TransactionChange> _changes;
    public object? Tag { get; }

    internal PhoneWaveRecordedTransaction(List<TransactionChange> changes, object? tag)
    {
        Tag = tag;
        _changes = changes;
    }

    /// <summary>
    /// Replays this record BEST-EFFORT for the context's undo/redo: every change is attempted
    /// EXACTLY ONCE — a failing change is never retried, and a failure never strands the changes
    /// after it (a notification observer throwing on change N must not leave changes N+1… unapplied).
    /// All failures are retained chronologically; the caller moves the history position to match the
    /// completed replay and only then surfaces them, so a retry cannot re-toggle XOR-backed changes.
    /// </summary>
    /// <param name="forward">True to replay forward (redo), false to replay backward (undo).</param>
    /// <param name="failures">The chronological failure list; each failing change is appended.</param>
    internal void ReplayBestEffort(bool forward, ref List<Exception>? failures)
    {
        Action<TransactionChange> apply = forward
            ? static change => change.RollForward()
            : static change => change.Rollback();
        ReplayFailures.ApplyEach(_changes, apply, ref failures);
    }

    public void RollBack()
    {
        foreach (var ch in _changes)
            ch.Rollback();
    }

    public void RollForward()
    {

        foreach (var ch in _changes)
            ch.RollForward();
    }
}