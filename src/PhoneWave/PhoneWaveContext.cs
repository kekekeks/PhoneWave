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
        _recorded.Clear();
        ActiveTransaction = null;
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

    internal PhoneWaveTransaction? ActiveTransaction { get; set; }
    public PhoneWaveTransaction BeginTransaction(object? tag = null)
    {
        if (IsSuspended)
            throw new InvalidOperationException("Attempted to begin transaction in suspended state");
        if (IsRolling)
            throw new InvalidOperationException("Attempted to begin transaction while a replay (undo/redo/abort) is in progress");
        if (ActiveTransaction != null)
            throw new InvalidOperationException();
        ActiveTransaction = new PhoneWaveTransaction(this, tag);
        return ActiveTransaction;
    }

    internal void AddRecord(PhoneWaveRecordedTransaction record)
    {
        // Overwrite entries 
        while (CurrentIndex < _recorded.Count - 1)
            _recorded.RemoveAt(_recorded.Count - 1);
        _recorded.Add(record);
        
        CurrentIndex = _recorded.Count - 1;
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