using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhoneWave;

public abstract class PhoneWaveBase : INotifyPropertyChanged
{
    private readonly PhoneWaveContext _context;

    public PhoneWaveBase(PhoneWaveContext context)
    {
        _context = context;
        InitializeStorage();
    }

    protected void SetValue<T>(PhoneWavePropertyStorage<T> storage, T value)
    {
        if (_context.ActiveTransaction == null)
            throw new InvalidOperationException("Unable to modify the state without an active transaction");
        _context.ActiveTransaction.Set(storage, value);
    }

    protected virtual void InitializeStorage()
    {
        
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void NotifyPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}