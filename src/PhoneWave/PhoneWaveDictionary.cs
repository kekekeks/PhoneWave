using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections;
using System.Linq;

namespace PhoneWave;

public class PhoneWaveDictionary<TKey, TValue> : IDictionary<TKey, TValue>, INotifyCollectionChanged where TKey : notnull
{
    class PhoneWaveDictionaryChangeAggregator : TransactionChange
    {
        class State
        {
            public TValue? OldValue { get; set; }
            public TValue? NewValue { get; set; }
            public bool HasOldValue { get; set; }
            public bool HasNewValue { get; set; }

            public State(bool hasOldValue, TValue? oldValue, bool hasNewValue, TValue? newValue)
            {
                OldValue = oldValue;
                NewValue = newValue;
                HasOldValue = hasOldValue;
                HasNewValue = hasNewValue;
            }
        }
        private readonly Dictionary<TKey, State> _states = new();
        private readonly PhoneWaveDictionary<TKey, TValue> _target;
        public PhoneWaveDictionaryChangeAggregator(PhoneWaveDictionary<TKey, TValue> target) => _target = target;

        State GetState(TKey key, bool hasOldValue, TValue? oldValue)
        {
            if (_states.TryGetValue(key, out var diff))
                return diff;
            return _states[key] = new State(hasOldValue, oldValue, false, default);
        }

        public void AddAdd(TKey key, TValue value)
        {
            var state = GetState(key, false, default);
            state.HasNewValue = true;
            state.NewValue = value;
        }
        public void AddRemove(TKey key, TValue value)
        {
            var state = GetState(key, true, value);
            state.HasNewValue = false;
            state.NewValue = default;
        }
        public void AddChange(TKey key, TValue oldValue, TValue newValue)
        {
            var state = GetState(key, true, oldValue);
            state.HasNewValue = true;
            state.NewValue = newValue;
        }

        public override void Rollback()
        {
            foreach (var s in _states)
            {
                if (s.Value.HasOldValue) _target.SetNoTransaction(s.Key, s.Value.OldValue!);
                else if (!s.Value.HasNewValue) _target.RemoveNoTransaction(s.Key);
            }
        }

        public override void RollForward()
        {
            foreach (var s in _states)
            {
                if (s.Value.HasNewValue) _target.SetNoTransaction(s.Key, s.Value.NewValue!);
                else if (!s.Value.HasOldValue) _target.RemoveNoTransaction(s.Key);
            }
        }
    }

    static void AddToDictionary(PhoneWaveContext context, PhoneWaveDictionary<TKey, TValue> target,
        TKey key, TValue value)
    {
        if (context.TryGetChanges<PhoneWaveDictionaryChangeAggregator>(key, out var ch))
        {
            ch.AddAdd(key, value);
        }
        else
        {
            var newChange = new PhoneWaveDictionaryChangeAggregator(target);
            newChange.AddAdd(key, value);
            context.AddChange(key, newChange);
        }
    }
    static void RemoveFromDictionary(PhoneWaveContext context, PhoneWaveDictionary<TKey, TValue> target,
        TKey key, TValue value)
    {
        if (context.TryGetChanges<PhoneWaveDictionaryChangeAggregator>(key, out var ch))
        {
            ch.AddRemove(key, value);
        }
        else
        {
            var newChange = new PhoneWaveDictionaryChangeAggregator(target);
            newChange.AddRemove(key, value);
            context.AddChange(key, newChange);
        }
    }
    static void UpdateDictionary(PhoneWaveContext context, PhoneWaveDictionary<TKey, TValue> target,
        TKey key, TValue oldValue, TValue newValue)
    {
        if (context.TryGetChanges<PhoneWaveDictionaryChangeAggregator>(key, out var ch))
        {
            ch.AddChange(key, oldValue, newValue);
        }
        else
        {
            var newChange = new PhoneWaveDictionaryChangeAggregator(target);
            newChange.AddChange(key, oldValue, newValue);
            context.AddChange(key, newChange);
        }
    }

    /// <summary>
    /// This is only supposed to be used from Rollback/RollForward.
    /// Adds or replaces the value for given key.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    void SetNoTransaction(TKey key, TValue value) => _dictionary[key] = value;
    /// <summary>
    /// This is only supposed to be used from Rollback/RollForward.
    /// Removes the entry with given key. 
    /// </summary>
    /// <param name="key"></param>
    void RemoveNoTransaction(TKey key) => _dictionary.Remove(key);

    public PhoneWaveContext Context { get; }

    public PhoneWaveDictionary(PhoneWaveContext context) => Context = context;

    public event NotifyCollectionChangedEventHandler CollectionChanged
    {
        add => _dictionary.CollectionChanged += value;
        remove => _dictionary.CollectionChanged -= value;
    }

    private readonly AvaloniaDictionary<TKey, TValue> _dictionary = new();

    public TValue this[TKey key]
    {
        get => _dictionary[key];
        set
        {
            if (_dictionary.ContainsKey(key))
            {
                var oldValue = _dictionary[key];
                _dictionary[key] = value;
                UpdateDictionary(Context, this, key, oldValue, value);
            }
            else
            {
                _dictionary[key] = value;
                AddToDictionary(Context, this, key, value);
            }
        }
    }

    public ICollection<TKey> Keys => _dictionary.Keys;

    public ICollection<TValue> Values => _dictionary.Values;

    public int Count => _dictionary.Count;

    public bool IsReadOnly => false;

    public void Add(TKey key, TValue value)
    {
        _dictionary.Add(key, value);
        AddToDictionary(Context, this, key, value);
    }

    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    public void Clear()
    {
        var kvps = this.ToList();
        _dictionary.Clear();
        foreach (var item in kvps)
            RemoveFromDictionary(Context, this, item.Key, item.Value);
    }
    public bool Remove(TKey key)
    {
        _dictionary.TryGetValue(key, out var old);
        var result = _dictionary.Remove(key);
        if (result) RemoveFromDictionary(Context, this, key, old);
        return result;
    }

    public bool Contains(KeyValuePair<TKey, TValue> item) => _dictionary.Contains(item);

    public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).CopyTo(array, arrayIndex);

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dictionary.GetEnumerator();

    public bool Remove(KeyValuePair<TKey, TValue> item)
    {
        if (((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).Remove(item))
        {
            RemoveFromDictionary(Context, this, item.Key, item.Value);
            return true;
        }
        return false;
    }

    public bool TryGetValue(TKey key,
#if NET6_0_OR_GREATER
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] 
#endif
        out TValue value) => _dictionary.TryGetValue(key, out value);

    IEnumerator IEnumerable.GetEnumerator() => _dictionary.GetEnumerator();
}