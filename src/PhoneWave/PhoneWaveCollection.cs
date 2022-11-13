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

        public override void Rollback()
        {
            foreach (var (isRemove, index, item) in Enumerable.Reverse(_changes))
            {
                if (isRemove) _target.InsertItemNoTransaction(index, item);
                else _target.RemoveItemNoTransaction(index);
            }
        }
        public override void RollForward()
        {
            foreach (var (isRemove, index, item) in _changes)
            {
                if (!isRemove) _target.InsertItemNoTransaction(index, item);
                else _target.RemoveItemNoTransaction(index);
            }
        }
    }
    static void UpdateCollection(PhoneWaveContext context, PhoneWaveCollection<T> target, bool isRemoval, int index, T item)
    {
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
