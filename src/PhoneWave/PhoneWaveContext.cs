using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace PhoneWave;

public class PhoneWaveContext : INotifyPropertyChanged
{
    public static PhoneWaveContext DummyFrozenContext { get; } = new PhoneWaveContext() { IsSuspended = true };

    private ObservableCollection<PhoneWaveRecordedTransaction> _recorded = new();
    private int _currentIndex = -1;
    public bool IsSuspended { get; private set; } = true;
     
    public void Suspend()
    {
        IsSuspended = true;
        CurrentIndex = -1;
        _mergeCandidate = null;
        _recorded.Clear();
        List<Exception>? failures = null;
        EndActiveTransaction(ref failures);
        ReplayFailures.Combine(failures, "HasActiveTransaction observers failed.")?.Throw();
    }
    public void Resume()
    {
        IsSuspended = false;
    }

    public bool TryGetChanges<T>(object key,
#if NET6_0_OR_GREATER
        [MaybeNullWhen(false)]
#endif
        out T changes) where T : TransactionChange
    { 
        if (IsSuspended)
        {
            changes = null;
            return false;
        }
        if (ActiveTransaction == null) 
            throw new InvalidOperationException("Attempted TryGetChanges with no active transaction");
        if(ActiveTransaction.TryGetChange(key, out var c))
        {
            changes = (T)c;
            return true;
        }
        changes = null;
        return false;
    }
    public void AddChange(object key, TransactionChange change)
    {
        if (IsSuspended) return;
        if (ActiveTransaction == null) 
            throw new InvalidOperationException("Attempted AddChange with no active transaction");
        ActiveTransaction.AddChange(key, change);
    }

    /// <summary>
    /// The ONE gate of every tracked container change (<see cref="PhoneWaveCollection{T}"/>,
    /// <see cref="PhoneWaveDictionary{TKey,TValue}"/>). The container calls it BEFORE it changes anything:
    /// while suspended it returns <see langword="false"/> (the change applies and nothing is recorded);
    /// during a replay (undo/redo/abort) or with no active transaction it throws, so a refused change
    /// never lands half-applied; otherwise it returns <see langword="true"/> and the container records the
    /// change before it applies it. <paramref name="kind"/> names the container in the message
    /// ("collection", "dictionary").
    /// </summary>
    internal bool EnsureCanModify(string kind)
    {
        if (IsSuspended)
            return false;
        // A replay (undo/redo/abort) applies recorded changes through the containers' NoTransaction
        // paths only, so an edit that reaches here while one runs comes from a notification observer.
        // It used to be applied silently and never recorded — a permanent change no undo could reach.
        // It fails loudly instead, exactly as a tracked property write during a replay does (no active
        // transaction on undo/redo; an aborting one on abort).
        if (IsRolling)
            throw new InvalidOperationException($"Attempted to modify a tracked {kind} while a replay (undo/redo/abort) is in progress");
        // A change outside a transaction is a contract violation, exactly as a tracked property write is.
        if (ActiveTransaction == null)
            throw new InvalidOperationException($"Attempted to modify a tracked {kind} without an active transaction");
        return true;
    }

    public IReadOnlyList<PhoneWaveRecordedTransaction> RecordedTransactions => _recorded;

    public int CurrentIndex
    {
        get => _currentIndex;
        private set
        {
            if (_currentIndex == value) return;
            _currentIndex = value;
            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(CurrentItem));
        }
    }
    public PhoneWaveRecordedTransaction? CurrentItem => (CurrentIndex >= 0 && CurrentIndex < _recorded.Count) ? _recorded[CurrentIndex] : null;

    internal PhoneWaveTransaction? ActiveTransaction { get; private set; }

    /// <summary>
    /// True while a transaction is open: from <see cref="BeginTransaction"/> until the transaction is
    /// committed, rolled back or disposed. Raises <see cref="PropertyChanged"/> when it changes, so a
    /// caller can wait for an open edit to end before it writes.
    /// </summary>
    public bool HasActiveTransaction => ActiveTransaction is not null;

    // The HasActiveTransaction value the observers were last told. The notification is raised only when
    // the value differs from it, so an observer that opens or ends a transaction from inside the
    // notification gets a consistent sequence, and a deferred announcement (the abort path) never
    // repeats a value that was already announced.
    private bool _announcedActiveTransaction;

    public PhoneWaveTransaction BeginTransaction(object? tag = null)
    {
        if (IsSuspended)
            throw new InvalidOperationException("Attempted to begin transaction in suspended state");
        if (IsRolling)
            throw new InvalidOperationException("Attempted to begin transaction while a replay (undo/redo/abort) is in progress");
        if (ActiveTransaction != null)
            throw new InvalidOperationException();
        var transaction = new PhoneWaveTransaction(this, tag);
        ActiveTransaction = transaction;
        List<Exception>? failures = null;
        AnnounceActiveTransaction(ref failures);
        if (failures is not null)
        {
            // The caller never receives the transaction, so it must not stay open: release the context
            // (the transaction has no changes yet) and announce that, then surface the failures.
            transaction.Abandon();
            EndActiveTransaction(ref failures);
            ReplayFailures.Combine(failures, "HasActiveTransaction observers failed.")?.Throw();
        }
        return transaction;
    }

    /// <summary>
    /// Releases the context from its active transaction WITHOUT announcing it. Only the abort path uses
    /// it: it announces the end after the replay completed (<see cref="AnnounceActiveTransaction"/>).
    /// </summary>
    internal void ClearActiveTransaction() => ActiveTransaction = null;

    /// <summary>Releases the context from its active transaction and announces
    /// <see cref="HasActiveTransaction"/>; each observer failure is appended to <paramref name="failures"/>.</summary>
    internal void EndActiveTransaction(ref List<Exception>? failures)
    {
        ActiveTransaction = null;
        AnnounceActiveTransaction(ref failures);
    }

    /// <summary>
    /// Raises <see cref="HasActiveTransaction"/> to every observer in isolation when its value differs
    /// from the value last announced; each observer failure is appended to <paramref name="failures"/>.
    /// </summary>
    internal void AnnounceActiveTransaction(ref List<Exception>? failures)
    {
        var hasActiveTransaction = HasActiveTransaction;
        if (hasActiveTransaction == _announcedActiveTransaction)
            return;
        _announcedActiveTransaction = hasActiveTransaction;
        RaiseIsolated(nameof(HasActiveTransaction), ref failures);
    }

    // The ambient merge scope (UseMergeKey): while it is open, a plain PhoneWaveTransaction.Commit()
    // commits as CommitMerging(AmbientMergeKey, AmbientMergeTags). Scopes do not nest.
    private object? _ambientMergeKey;

    /// <summary>
    /// The merge key that a plain <see cref="PhoneWaveTransaction.Commit"/> uses while a
    /// <see cref="UseMergeKey"/> scope is open, or null when no scope is open.
    /// </summary>
    public object? AmbientMergeKey => _ambientMergeKey;

    /// <summary>The tag function of the open <see cref="UseMergeKey"/> scope, or null.</summary>
    internal Func<object?, object?, object?>? AmbientMergeTags { get; private set; }

    /// <summary>
    /// Opens an ambient merge scope until the returned object is disposed. While it is open, a plain
    /// <see cref="PhoneWaveTransaction.Commit"/> behaves as
    /// <see cref="PhoneWaveTransaction.CommitMerging"/> with <paramref name="mergeKey"/> and
    /// <paramref name="mergeTags"/>. When such a commit records a NEW entry, the entry's tag is
    /// <c>mergeTags(null, transaction tag)</c> when <paramref name="mergeTags"/> is set. An explicit
    /// <see cref="PhoneWaveTransaction.CommitMerging"/> call keeps its own arguments.
    /// </summary>
    /// <remarks>The key applies when a transaction commits, not when it begins. Scopes do not nest: a
    /// second call while a scope is open throws. Disposing the scope twice is harmless.</remarks>
    /// <param name="mergeKey">The merge key of the scope; not null.</param>
    /// <param name="mergeTags">Optional: computes an entry's tag from (previous tag, this tag); for a new
    /// entry the previous tag is null.</param>
    /// <returns>The scope; dispose it to end the scope.</returns>
    public IDisposable UseMergeKey(object mergeKey, Func<object?, object?, object?>? mergeTags = null)
    {
        if (mergeKey is null)
            throw new ArgumentNullException(nameof(mergeKey));
        if (_ambientMergeKey is not null)
            throw new InvalidOperationException("Attempted to open a merge key scope while one is open");
        _ambientMergeKey = mergeKey;
        AmbientMergeTags = mergeTags;
        return new MergeKeyScope(this);
    }

    private sealed class MergeKeyScope(PhoneWaveContext context) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            // Scopes do not nest, so the open scope is this one.
            context._ambientMergeKey = null;
            context.AmbientMergeTags = null;
        }
    }

    // The ONLY record a CommitMerging may merge into: the newest record, set when it was recorded,
    // merged or retagged, and cleared by every undo, redo, Suspend and EndMerge. So a merge never reaches
    // past another commit (it replaces the candidate), an undo or a redo. An aborted transaction changes
    // neither the state nor the history, so it leaves the candidate in place.
    private PhoneWaveRecordedTransaction? _mergeCandidate;

    internal void AddRecord(PhoneWaveRecordedTransaction record)
    {
        // Overwrite entries
        while (CurrentIndex < _recorded.Count - 1)
            _recorded.RemoveAt(_recorded.Count - 1);
        _recorded.Add(record);
        _mergeCandidate = record;

        CurrentIndex = _recorded.Count - 1;
    }

    /// <summary>
    /// The record a transaction committed with <paramref name="mergeKey"/> merges into, or null to record
    /// it as a new entry: the merge candidate, when it is still the current AND newest record and its
    /// merge key equals <paramref name="mergeKey"/>. A null key never merges.
    /// </summary>
    internal PhoneWaveRecordedTransaction? FindMergeTarget(object? mergeKey)
    {
        if (mergeKey is null || _mergeCandidate is not { } candidate)
            return null;
        if (CurrentIndex != _recorded.Count - 1 || !ReferenceEquals(CurrentItem, candidate))
            return null;
        return Equals(mergeKey, candidate.MergeKey) ? candidate : null;
    }

    /// <summary>Puts the merged record in place of the current (newest) record it was merged from.</summary>
    internal void ReplaceCurrentRecord(PhoneWaveRecordedTransaction merged)
    {
        _mergeCandidate = merged;
        _recorded[CurrentIndex] = merged;
        // The position stays; the entry at it is a new object.
        OnPropertyChanged(nameof(CurrentItem));
    }

    /// <summary>
    /// Ends the open merge chain, whatever its key: the next commit with a merge key records a new entry.
    /// The history and the ambient merge scope do not change.
    /// </summary>
    public void EndMerge() => _mergeCandidate = null;

    /// <summary>
    /// True when a commit with <paramref name="mergeKey"/> would merge into the newest entry now. It has no
    /// side effect. (A commit with no changes still records nothing.)
    /// </summary>
    /// <param name="mergeKey">The merge key to test.</param>
    public bool CanMerge(object mergeKey) => FindMergeTarget(mergeKey) is not null;

    /// <summary>
    /// When the newest entry is the open merge chain of <paramref name="mergeKey"/> (<see cref="CanMerge"/>
    /// is true), replaces that entry's tag with <paramref name="tag"/> and returns true. The entry is
    /// replaced by a new entry object, as a merge does: a Replace notification of
    /// <see cref="RecordedTransactions"/> and <see cref="PropertyChanged"/> for <see cref="CurrentItem"/>.
    /// The replay, the chain and <see cref="CurrentIndex"/> do not change. Otherwise it does nothing and
    /// returns false.
    /// </summary>
    /// <param name="mergeKey">The merge key of the chain.</param>
    /// <param name="tag">The new tag of the entry.</param>
    public bool TryRetagMerge(object mergeKey, object? tag)
    {
        if (FindMergeTarget(mergeKey) is not { } current)
            return false;
        ReplaceCurrentRecord(current.Retag(tag));
        return true;
    }

    public bool CanRollback => CurrentIndex >= 0;
    public bool CanRollForward => CurrentIndex < _recorded.Count - 1;

    private int _rollDepth = 0;
    public bool IsRolling => _rollDepth > 0;

    /// <summary>
    /// Opens one depth-managed replay. Nested calls (a recorded transaction's rollback replaying
    /// property changes that fire further notifications) join the outermost one. The outermost
    /// ENTRY raises the <see cref="IsRolling"/> rising edge; the matching
    /// <see cref="EndRoll"/> raises the falling edge after the final state.
    /// </summary>
    private void BeginRoll()
    {
        var wasOutermost = Interlocked.Increment(ref _rollDepth) == 1;
        if (!wasOutermost) return;
        // Every rising-edge observer runs in isolation: one throwing handler must not silence the
        // handlers after it in the multicast delegate.
        List<Exception>? entryFailures = null;
        RaiseIsolated(nameof(IsRolling), ref entryFailures);
        if (entryFailures is null) return;
        // A throwing rising-edge observer must not leave IsRolling permanently stuck: unwind the
        // depth and rethrow — the caller's own cleanup aborts the replay attempt. A single failure
        // keeps its original instance.
        Interlocked.Decrement(ref _rollDepth);
        ReplayFailures.Combine(entryFailures, "Replay entry observers failed.")?.Throw();
    }

    /// <summary>Abort-path entry: an uncommitted transaction's rollback joins the same depth-managed
    /// replay lifecycle so its completion fires the same IsRolling edges. The concrete scope lets the
    /// aborting transaction attach the primary replay failure for completion aggregation.</summary>
    internal AbortRollScope BeginAbortRoll()
    {
        BeginRoll();
        return new AbortRollScope(this);
    }

    /// <summary>
    /// RE-ENTRY for the abort path ONLY, after a failed acquisition: <see cref="BeginRoll"/>'s
    /// cleanup already reset the depth (IsRolling is FALSE again — every rising-edge observer,
    /// the failing one included, has seen its one rising edge), so this variant sets the depth WITHOUT raising a second rising-edge
    /// notification. The falling edge at <see cref="EndRoll"/> still fires exactly once.
    /// </summary>
    internal AbortRollScope BeginAbortRollWithoutEntryNotification()
    {
        Interlocked.Increment(ref _rollDepth);
        return new AbortRollScope(this);
    }

    internal sealed class AbortRollScope(PhoneWaveContext context) : IDisposable
    {
        public Exception? PrimaryReplayFailure { get; set; }
        public void Dispose() => context.EndRoll(PrimaryReplayFailure);
    }

    /// <summary>
    /// Ends one replay. On the outermost completion the <see cref="IsRolling"/> falling edge fires
    /// after the final state. <paramref name="primaryReplayFailure"/> is the already-throwing
    /// replay's exception (null on success): if completion observers ALSO throw, the failures are
    /// aggregated with the primary first — the primary replay error is never masked or dropped.
    /// Every falling-edge observer runs, even after an earlier one threw. Always safe on every
    /// path; depth is always reset.
    /// </summary>
    private void EndRoll(Exception? primaryReplayFailure)
    {
        var isOutermost = Interlocked.Decrement(ref _rollDepth) == 0;
        if (!isOutermost) return;
        // The falling edge is the completion notification: deferred observers re-evaluate the FINAL
        // replayed state here. Each handler is invoked separately, so one throwing observer cannot
        // keep a later one (for example a deferred count flush) from ever running.
        List<Exception>? completionFailures = null;
        RaiseIsolated(nameof(IsRolling), ref completionFailures);
        if (completionFailures is not null && primaryReplayFailure is not null)
            throw new AggregateException("Replay failed and the completion observer also failed.", [primaryReplayFailure, .. completionFailures]);
        ReplayFailures.Combine(completionFailures, "Replay completion observers failed.")?.Throw();
    }

    /// <summary>
    /// Raises <paramref name="propertyName"/> to every <see cref="PropertyChanged"/> handler in
    /// isolation: each handler of the multicast delegate is invoked separately, and each failure is
    /// appended, in order, to <paramref name="failures"/>.
    /// </summary>
    private void RaiseIsolated(string propertyName, ref List<Exception>? failures)
    {
        if (PropertyChanged is not { } handlers) return;
        var args = new PropertyChangedEventArgs(propertyName);
        ReplayFailures.ApplyEach(handlers.GetInvocationList(), handler => ((PropertyChangedEventHandler)handler)(this, args), ref failures);
    }

    public void Rollback()
    {
        if (!CanRollback)
            throw new InvalidOperationException();
        ReplayRecord(_recorded[CurrentIndex], forward: false);
    }

    public void RollForward()
    {
        if (!CanRollForward)
            throw new InvalidOperationException();
        ReplayRecord(_recorded[CurrentIndex + 1], forward: true);
    }

    /// <summary>
    /// The ONE undo/redo replay. The record is captured BEFORE the roll opens and the history position
    /// moves only AFTER the replay, inside the roll: an observer of the position notification can
    /// neither see a record selected that was never replayed, nor begin a transaction that truncates
    /// the history between selection and replay (BeginTransaction rejects while IsRolling).
    /// </summary>
    /// <param name="record">The record to replay: the current one for an undo, the next one for a redo.</param>
    /// <param name="forward">True to replay forward (redo), false to replay backward (undo).</param>
    private void ReplayRecord(PhoneWaveRecordedTransaction record, bool forward)
    {
        // An undo or a redo ends every merge run: a later CommitMerging records a new entry.
        _mergeCandidate = null;
        // Entry failure (a throwing rising-edge observer) propagates from here before any change
        // applied and before the position moved: bytes and history are both untouched.
        BeginRoll();
        // Chronological failures: one per failing change replay, then one for a failing history
        // notification. Nothing masks anything else; the list is folded exactly once at the end.
        List<Exception>? failures = null;
        try
        {
            // The record replays BEST-EFFORT: every change is attempted once, a failing change is
            // never retried, and later changes are never stranded. The history position then moves
            // to match the COMPLETED replay — even when a notification observer threw — so the
            // record can never be re-selected and re-toggled (destructive for XOR-backed dump
            // changes). Failures surface only after the position update, so the falling-edge
            // completion observers in EndRoll see the final state AND the final position.
            record.ReplayBestEffort(forward, ref failures);
            try
            {
                CurrentIndex += forward ? 1 : -1;
            }
            catch (Exception notificationFailure)
            {
                (failures ??= []).Add(notificationFailure);
            }
        }
        finally
        {
            // Completion runs on every path after the final state; on the failure path the
            // combined original failures keep unwinding and stay primary.
            var combined = ReplayFailures.Combine(failures, "Recorded replay failed at multiple changes.");
            EndRoll(combined?.SourceException);
            combined?.Throw();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}