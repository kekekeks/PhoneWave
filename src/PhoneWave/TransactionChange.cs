namespace PhoneWave;

public abstract class TransactionChange
{
    public abstract void Rollback();
    public abstract void RollForward();

    /// <summary>
    /// Merge support (<see cref="PhoneWaveTransaction.CommitMerging"/>): offers <paramref name="later"/>,
    /// the change recorded under the SAME key by a later transaction that is being merged into the
    /// record holding this change. Return true to take it into this change; the caller then drops
    /// <paramref name="later"/>. Return false to keep it as a separate change, replayed after this one on
    /// redo and before it on undo — that is ALWAYS correct, so it is the default.
    /// </summary>
    /// <remarks>
    /// Called only while both changes are applied (the record is the top of the undo history and the
    /// later transaction has just been committed). After a true return, <see cref="Rollback"/> must
    /// restore the state from before THIS change and <see cref="RollForward"/> the state after
    /// <paramref name="later"/>, and that combined change is replayed at the position of this one: absorb
    /// only a change whose effect does not depend on the changes recorded between the two.
    /// </remarks>
    /// <param name="later">The later change recorded under the same key.</param>
    /// <returns>True when this change now also represents <paramref name="later"/>.</returns>
    protected internal virtual bool TryAbsorb(TransactionChange later) => false;
}
class TransactionChange<T> : TransactionChange
{
    private readonly PhoneWavePropertyStorage<T> _storage;
    public T InitialValue { get; }
    public T UpdatedValue { get; private set; }

    public TransactionChange(PhoneWavePropertyStorage<T> storage, T updatedValue)
    {
        _storage = storage;
        InitialValue = storage.Value;
        UpdatedValue = updatedValue;
        storage.Value = updatedValue;
    }
    public void SetValue(T value)
    {
        UpdatedValue = value;
        _storage.Value = value;
    }

    public override void Rollback() => _storage.Value = InitialValue;

    public override void RollForward() => _storage.Value = UpdatedValue;

    // A property write stores an absolute value, so the merged change keeps the FIRST initial value and
    // the LAST updated value. The storage already holds the later value: nothing is written here. An
    // entry whose two values end up equal is kept, as a single transaction keeps one (it still replays).
    protected internal override bool TryAbsorb(TransactionChange later)
    {
        if (later is not TransactionChange<T> next || !ReferenceEquals(next._storage, _storage))
            return false;
        UpdatedValue = next.UpdatedValue;
        return true;
    }
}
