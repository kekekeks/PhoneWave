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

    /// <summary>
    /// Commits this transaction as a new undo entry with no merge key. Inside a
    /// <see cref="PhoneWaveContext.UseMergeKey"/> scope it commits as <see cref="CommitMerging"/> with the
    /// scope's key and tag function instead (so a transaction with no changes then records nothing), and
    /// a new entry takes the tag <c>mergeTags(null, this tag)</c> when the scope has a tag function.
    /// </summary>
    public void Commit()
    {
        if(_disposed)
            return;
        // The ambient scope lets code that only calls Commit() (for example an operation that a script
        // runs) join the caller's merge chain without a change.
        if (_context.AmbientMergeKey is { } ambientMergeKey)
        {
            CommitMerging(ambientMergeKey, _context.AmbientMergeTags, tagNewEntry: true);
            return;
        }
        Complete(() => _context.AddRecord(new PhoneWaveRecordedTransaction(_changes, _tag, mergeKey: null)));
    }

    /// <summary>
    /// Commits this transaction and MERGES it into the previous undo entry when that entry carries an
    /// equal <paramref name="mergeKey"/> and nothing happened to the history since it was recorded: no
    /// other commit, no undo, no redo. One undo of the merged entry then restores the state from before
    /// the FIRST merged transaction; one redo restores the state after the LAST. Otherwise the
    /// transaction is recorded as a new entry that carries <paramref name="mergeKey"/>, so a later call
    /// with the same key can merge into it.
    /// </summary>
    /// <remarks>
    /// <para>Keys are compared with <see cref="object.Equals(object, object)"/>. A null key never merges
    /// (the entry is recorded and carries no key, exactly as <see cref="Commit"/> records it).</para>
    /// <para>A transaction with no changes records NOTHING (unlike <see cref="Commit"/>, which records an
    /// empty entry) and leaves the history as it is: a later call can still merge into the previous
    /// entry. A rolled-back transaction leaves no trace either, so it does not stop a later merge.</para>
    /// <para>The merged entry keeps the previous entry's tag, or takes the tag that
    /// <paramref name="mergeTags"/> returns for (previous entry's tag, this transaction's tag). The
    /// delegate runs before anything is committed: if it throws, this transaction stays active and
    /// uncommitted.</para>
    /// <para>On a merge, <see cref="PhoneWaveContext.RecordedTransactions"/> replaces the previous entry
    /// with the merged one (a Replace notification) and the context raises
    /// <see cref="PhoneWaveContext.CurrentItem"/>; <see cref="PhoneWaveContext.CurrentIndex"/> does not
    /// change. The previous entry object is retired: its own replay methods throw from then on.</para>
    /// </remarks>
    /// <param name="mergeKey">Identifies the run of transactions that may merge (for example one script
    /// run); null to never merge.</param>
    /// <param name="mergeTags">Optional: computes the merged entry's tag from (previous tag, this tag).
    /// Null keeps the previous entry's tag.</param>
    /// <returns>What happened to the transaction.</returns>
    public PhoneWaveCommitOutcome CommitMerging(object? mergeKey, Func<object?, object?, object?>? mergeTags = null)
        => CommitMerging(mergeKey, mergeTags, tagNewEntry: false);

    /// <param name="mergeKey">See the public overload.</param>
    /// <param name="mergeTags">See the public overload.</param>
    /// <param name="tagNewEntry">True for a commit under an ambient merge scope: a new entry takes the tag
    /// <c>mergeTags(null, this tag)</c>, so an entry that such a commit starts carries the scope's tag.</param>
    private PhoneWaveCommitOutcome CommitMerging(object? mergeKey, Func<object?, object?, object?>? mergeTags, bool tagNewEntry)
    {
        if (_disposed)
            return PhoneWaveCommitOutcome.NothingRecorded;
        if (_changes.Count == 0)
        {
            Complete(static () => { });
            return PhoneWaveCommitOutcome.NothingRecorded;
        }

        if (_context.FindMergeTarget(mergeKey) is not { } previous)
        {
            // The tag delegate runs before anything is committed, as for a merge.
            var newTag = tagNewEntry && mergeTags is not null ? mergeTags(null, _tag) : _tag;
            Complete(() => _context.AddRecord(new PhoneWaveRecordedTransaction(_changes, newTag, mergeKey)));
            return PhoneWaveCommitOutcome.Recorded;
        }

        var tag = mergeTags is null ? previous.Tag : mergeTags(previous.Tag, _tag);
        List<Exception>? absorbFailures = null;
        Complete(() => _context.ReplaceCurrentRecord(previous.MergeWith(_changes, tag, ref absorbFailures)));
        // A change whose TryAbsorb threw was kept as a separate change, so the merged entry is complete
        // and replays correctly; the failure surfaces only now, after the history is consistent.
        ReplayFailures.Combine(absorbFailures, "Merging the transaction kept every change; some changes failed to merge.")?.Throw();
        return PhoneWaveCommitOutcome.Merged;
    }

    /// <summary>
    /// The ONE completion of a commit: marks the transaction done, publishes it to the history and then
    /// releases the context. The release runs even when a history observer throws, so a throwing
    /// observer cannot leave a finished transaction as the context's active one.
    /// </summary>
    private void Complete(Action publish)
    {
        _disposed = true;
        // Chronological failures: the history publication first, then the HasActiveTransaction observers.
        // A single failure keeps its original instance.
        List<Exception>? failures = null;
        try
        {
            publish();
        }
        catch (Exception publishFailure)
        {
            (failures ??= []).Add(publishFailure);
        }
        _context.EndActiveTransaction(ref failures);
        ReplayFailures.Combine(failures, "Committing the transaction failed at multiple stages.")?.Throw();
    }

    /// <summary>Marks a transaction that its caller never received as done, so it can never commit or
    /// roll back (see <see cref="PhoneWaveContext.BeginTransaction"/>). It has no changes.</summary>
    internal void Abandon() => _disposed = true;

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
            // The guard clears BEFORE the completion notification (the roll's Dispose below). The end of
            // the transaction (HasActiveTransaction) is announced only after that completion, below.
            context.ClearActiveTransaction();

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

        // Restored and completed: announce the free context last, so an observer that waits for the end
        // of the transaction sees the final state and may begin a new transaction at once. An observer
        // failure aggregates with the replay failure; it never masks it.
        List<Exception>? announceFailures = null;
        context.AnnounceActiveTransaction(ref announceFailures);
        if (announceFailures is not null)
        {
            primary = primary is null
                ? ReplayFailures.Combine(announceFailures, "HasActiveTransaction observers failed.")
                : ExceptionDispatchInfo.Capture(new AggregateException(
                    "Abort replay failed and the HasActiveTransaction observers also failed.",
                    [primary.SourceException, .. announceFailures]));
        }

        // Surface the original failure with its original stack.
        primary?.Throw();
    }
}

/// <summary>
/// Stores a completed, now-immutable transaction
/// </summary>
public class PhoneWaveRecordedTransaction
{
    // One segment per committed transaction in this record, in commit order: a record that was never
    // merged has exactly one. A merge appends the later transaction's changes that no earlier change
    // absorbed (TransactionChange.TryAbsorb) as a new segment.
    private readonly List<List<TransactionChange>> _segments;

    // Where the newest change of each change key lives, so a merge offers a later change to it. Kept
    // only by a record that carries a merge key: no other record can ever be merged into.
    private readonly Dictionary<object, (int Segment, int Index)>? _latest;

    // Set when this record was merged into a newer record or retagged: that record took over (and may
    // have changed) these change objects, so this one must never replay again.
    private bool _retired;

    public object? Tag { get; }

    /// <summary>
    /// The key given to <see cref="PhoneWaveTransaction.CommitMerging"/> (or the ambient key of a
    /// <see cref="PhoneWaveContext.UseMergeKey"/> scope) when this entry was recorded, or null (never merges).
    /// </summary>
    public object? MergeKey { get; }

    /// <summary>
    /// How many committed transactions this entry holds: 1 when it was never merged, plus one per merge.
    /// (A merged transaction whose changes were all absorbed still counts.)
    /// </summary>
    public int CommitCount { get; }

    /// <summary>
    /// When the newest transaction of this entry was committed (UTC): the time it was recorded, or of its latest
    /// merge. A retag keeps it.
    /// </summary>
    public DateTime RecordedAt { get; }

    internal PhoneWaveRecordedTransaction(Dictionary<object, TransactionChange> changes, object? tag, object? mergeKey)
    {
        RecordedAt = DateTime.UtcNow;
        var segment = new List<TransactionChange>(changes.Count);
        Dictionary<object, (int Segment, int Index)>? latest = mergeKey is null ? null : new(changes.Count);
        foreach (var pair in changes)
        {
            if (latest is not null)
                latest[pair.Key] = (0, segment.Count);
            segment.Add(pair.Value);
        }
        _segments = [segment];
        _latest = latest;
        Tag = tag;
        MergeKey = mergeKey;
        CommitCount = 1;
    }

    private PhoneWaveRecordedTransaction(List<List<TransactionChange>> segments,
        Dictionary<object, (int Segment, int Index)> latest, object? tag, object? mergeKey, int commitCount, DateTime recordedAt)
    {
        RecordedAt = recordedAt;
        _segments = segments;
        _latest = latest;
        Tag = tag;
        MergeKey = mergeKey;
        CommitCount = commitCount;
    }

    /// <summary>
    /// Returns a new record with the same changes, chain state and commit count, and
    /// <paramref name="tag"/>; this record is retired, as on a merge (the new record takes over its changes).
    /// </summary>
    /// <param name="tag">The new record's tag.</param>
    internal PhoneWaveRecordedTransaction Retag(object? tag)
    {
        // Only the open chain's record is retagged, and only a record with a merge key opens a chain.
        var latest = _latest ?? throw new InvalidOperationException("Attempted to retag a record without a merge key");
        _retired = true;
        return new PhoneWaveRecordedTransaction(_segments, latest, tag, MergeKey, CommitCount, RecordedAt);
    }

    /// <summary>
    /// Merges the changes of a just-committed transaction into this record and returns the merged record,
    /// which takes over this record's changes; this record is retired. Each later change is offered to the
    /// newest earlier change under the same key; the ones not absorbed form a new segment. A change whose
    /// TryAbsorb throws is kept as a separate change (always correct) and its failure is appended to
    /// <paramref name="failures"/>.
    /// </summary>
    /// <param name="later">The later transaction's changes, in recording order.</param>
    /// <param name="tag">The merged record's tag.</param>
    /// <param name="failures">The failure list; created on the first failing TryAbsorb.</param>
    internal PhoneWaveRecordedTransaction MergeWith(Dictionary<object, TransactionChange> later, object? tag,
        ref List<Exception>? failures)
    {
        // Only a record with a merge key is a merge target, and only that record keeps the index.
        var latest = _latest ?? throw new InvalidOperationException("Attempted to merge into a record without a merge key");
        var segment = new List<TransactionChange>();
        foreach (var pair in later)
        {
            if (latest.TryGetValue(pair.Key, out var at) && TryAbsorb(_segments[at.Segment][at.Index], pair.Value, ref failures))
                continue;
            latest[pair.Key] = (_segments.Count, segment.Count);
            segment.Add(pair.Value);
        }
        if (segment.Count > 0)
            _segments.Add(segment);
        _retired = true;
        return new PhoneWaveRecordedTransaction(_segments, latest, tag, MergeKey, CommitCount + 1, DateTime.UtcNow);
    }

    private static bool TryAbsorb(TransactionChange earlier, TransactionChange later, ref List<Exception>? failures)
    {
        try
        {
            return earlier.TryAbsorb(later);
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
            return false;
        }
    }

    /// <summary>
    /// The changes in replay order. Redo walks the segments oldest first; undo walks them newest first.
    /// Inside a segment the changes keep their recorded order in both directions, exactly as the undo of
    /// one transaction always has (one change per key per transaction, so they are independent).
    /// </summary>
    private IEnumerable<TransactionChange> InReplayOrder(bool forward)
    {
        if (_retired)
            throw new InvalidOperationException("Attempted to replay a record that was merged into a newer record");
        for (var n = 0; n < _segments.Count; n++)
        {
            foreach (var change in _segments[forward ? n : _segments.Count - 1 - n])
                yield return change;
        }
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
        ReplayFailures.ApplyEach(InReplayOrder(forward), apply, ref failures);
    }

    public void RollBack()
    {
        foreach (var ch in InReplayOrder(forward: false))
            ch.Rollback();
    }

    public void RollForward()
    {

        foreach (var ch in InReplayOrder(forward: true))
            ch.RollForward();
    }
}