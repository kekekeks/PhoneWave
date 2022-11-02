using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

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

    internal void Set<T>(PhoneWavePropertyStorage<T> storage, T updated)
    {
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
        if (TryGetChange(key, out _))
            throw new InvalidOperationException("Attempted to add with a change already present");
        _changes[key] = firstChange; 
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

/// <summary>
/// Stores a completed, now-immutable transaction
/// </summary>
public class PhoneWaveRecordedTransaction
{
    private readonly List<TransactionChange> _changes;
    public object? Tag { get; }

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