using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;

namespace PhoneWave;

public class PhoneWaveCollection<T> : ObservableCollection<T>
{
    class PhoneWaveCollectionChangeAggregator : TransactionChange
    {
        private readonly List<(bool isRemove, int index, T item)> _changes = new();

        private readonly PhoneWaveCollection<T> _target;

        public PhoneWaveCollectionChangeAggregator(PhoneWaveCollection<T> target)
        {
            _target = target;
        }

        public void AddItem(T item, int index) => _changes.Add((false, index, item));
        public void RemoveItem(T item, int index) => _changes.Add((true, index, item));

        // Every recorded insert/remove is replayed EXACTLY ONCE, best-effort: a CollectionChanged
        // handler throwing on one item must not strand the items after it (the history position
        // still moves to match the completed replay). The failures surface together at the end.
        // Both directions walk a snapshot, so the enumeration itself can never be broken.
        public override void Rollback() =>
            ReplayFailures.ApplyEachThenThrow(Enumerable.Reverse(_changes.ToArray()), ApplyInverse, ReplayFailureMessage);

        public override void RollForward() =>
            ReplayFailures.ApplyEachThenThrow(_changes.ToArray(), Apply, ReplayFailureMessage);

        private const string ReplayFailureMessage = "Tracked collection replay applied every change; some collection observers failed.";

        private void Apply((bool isRemove, int index, T item) change)
        {
            if (!change.isRemove) _target.InsertItemNoTransaction(change.index, change.item);
            else _target.RemoveItemNoTransaction(change.index);
        }

        private void ApplyInverse((bool isRemove, int index, T item) change)
        {
            if (change.isRemove) _target.InsertItemNoTransaction(change.index, change.item);
            else _target.RemoveItemNoTransaction(change.index);
        }
    }
    static void UpdateCollection(PhoneWaveContext context, PhoneWaveCollection<T> target, bool isRemoval, int index, T item)
    {
        if (context.IsSuspended) return;
        // A replay (undo/redo/abort) applies recorded changes through the NoTransaction paths only,
        // so an edit that reaches here while one runs comes from a notification observer. It used
        // to be applied silently and never recorded — a permanent change no undo could reach. It
        // now fails loudly BEFORE the collection changes, exactly as a tracked property write
        // during a replay does (no active transaction on undo/redo; an aborting one on abort).
        if (context.IsRolling)
            throw new InvalidOperationException("Attempted to modify a tracked collection while a replay (undo/redo/abort) is in progress");

        // Auto-create transaction if none active (for standalone collection ops)
        bool autoTx = context.ActiveTransaction == null;
        PhoneWaveTransaction? tx = null;
        if (autoTx)
            tx = context.BeginTransaction(null);

        void MakeChange(PhoneWaveCollectionChangeAggregator x)
        {
            if (isRemoval) x.RemoveItem(item, index);
            else x.AddItem(item, index);
        }

        if (context.TryGetChanges<PhoneWaveCollectionChangeAggregator>(target, out var x))
            MakeChange(x);
        else
        {
            var da = new PhoneWaveCollectionChangeAggregator(target);
            MakeChange(da);
            context.AddChange(target, da);
        }
        
        if (autoTx)
            tx?.Commit();
    }

    public PhoneWaveContext Context { get; }

    public PhoneWaveCollection(PhoneWaveContext context)
    {
        Context = context;
    }
    void InsertItemNoTransaction(int index, T item) => base.InsertItem(index, item);
    void RemoveItemNoTransaction(int index) => base.RemoveItem(index);
    protected override void ClearItems()
    {
        for (int i = Count - 1; i >= 0; i--)
        {
            UpdateCollection(Context, this, true, i, this[i]);
        }
        base.ClearItems();
    }
    protected override void InsertItem(int index, T item)
    {
        UpdateCollection(Context, this, false, index, item);
        base.InsertItem(index, item);
    }
    protected override void MoveItem(int oldIndex, int newIndex)
    {
        var item = this[oldIndex];
        UpdateCollection(Context, this, true, oldIndex, item);
        UpdateCollection(Context, this, false, newIndex, item);

        base.MoveItem(oldIndex, newIndex);
    }
    protected override void RemoveItem(int index)
    {
        UpdateCollection(Context, this, true, index, this[index]);
        base.RemoveItem(index);
    }
    protected override void SetItem(int index, T item)
    {
        UpdateCollection(Context, this, true, index, this[index]);
        UpdateCollection(Context, this, false, index, item);
        base.SetItem(index, item);
    }
}
