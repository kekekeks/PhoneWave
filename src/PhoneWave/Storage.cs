using System;
using System.Collections.Generic;

namespace PhoneWave;

public class PhoneWavePropertyStorageBase
{
}

public sealed class PhoneWavePropertyStorage<T> : PhoneWavePropertyStorageBase, IObservable<T>
{
    private List<Subscription>? _subscribers;
    private T _value = default!;
    internal PhoneWaveBase? Parent { get; private set; }
    internal string? PropertyName { get; set; }

    public T Value
    {
        get => _value;
        internal set
        {
            _value = value;
            if (PropertyName != null)
                Parent?.NotifyPropertyChanged(PropertyName);
            if (_subscribers != null)
                foreach (var sub in _subscribers)
                {
                    sub.Subscriber.OnNext(_value);
                }
        }
    }
    
    class Subscription : IDisposable
    {
        private PhoneWavePropertyStorage<T> _parent;
        public IObserver<T> Subscriber { get; }

        public Subscription(PhoneWavePropertyStorage<T> parent, IObserver<T> subscriber)
        {
            _parent = parent;
            Subscriber = subscriber;
        }

        public void Dispose()
        {
            _parent?._subscribers?.Remove(this);
            _parent = null!;
        }
    }

    public void Initialize(PhoneWaveBase parent, string propertyName)
    {
        Parent = parent;
        PropertyName = propertyName;
    }


    public IDisposable Subscribe(IObserver<T> observer)
    {
        var sub = new Subscription(this, observer);
        (_subscribers ??= new()).Add(sub);
        observer.OnNext(_value);
        return sub;
    }
}