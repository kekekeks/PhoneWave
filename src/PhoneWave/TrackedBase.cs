using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace PhoneWave;

public abstract class PhoneWaveBase : INotifyPropertyChanged
{  
    public PhoneWaveContext Context { get; } 

    public PhoneWaveBase(PhoneWaveContext context)
    {
        Context = context;
        InitializeStorage();
    }

    protected void SetValue<T>(PhoneWavePropertyStorage<T> storage, T value)
    { 
        if (Context.ActiveTransaction == null)
        { 
            if (Context.IsSuspended)
            {
                storage.Value = value;
                return;
            }
            throw new InvalidOperationException("Unable to modify the state without an active transaction");
        }
        
        Context.ActiveTransaction.Set(storage, value);
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