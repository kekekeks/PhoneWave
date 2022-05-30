using System;
using System.Collections.Generic;
using System.Linq;

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

    Dictionary<PhoneWavePropertyStorageBase, TransactionChange> _changes = new ();

    internal void Set<T>(PhoneWavePropertyStorage<T> storage, T updated)
    {
        if (!_changes.TryGetValue(storage, out var change))
            _changes[storage] = new TransactionChange<T>(storage, updated);
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
        foreach(var ch in _changes)
            ch.Value.Rollback();
        _context.ActiveTransaction = null;
        
    }

    public void Dispose() => Rollback();
}

public class PhoneWaveRecordedTransaction
{
    private readonly List<TransactionChange> _changes;
    public object Tag { get; }

    internal PhoneWaveRecordedTransaction(List<TransactionChange> changes, object? tag)
    {
        Tag = tag;
        _changes = changes;
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