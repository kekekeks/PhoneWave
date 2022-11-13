namespace PhoneWave;

public abstract class TransactionChange
{
    public abstract void Rollback();
    public abstract void RollForward();
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
}