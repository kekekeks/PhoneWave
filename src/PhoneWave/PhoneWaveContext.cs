using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PhoneWave
{
    public class PhoneWaveContext : INotifyPropertyChanged
    {
        public static PhoneWaveContext DummyFrozenContext { get; } = new PhoneWaveContext() { IsSuspended = true };

        private ObservableCollection<PhoneWaveRecordedTransaction> _recorded = new();
        private int _currentIndex = -1;
        public bool IsSuspended { get; private set; } = true;
         
        public void Suspend()
        {
            IsSuspended = true;
            CurrentIndex = -1;
            _recorded.Clear();
            ActiveTransaction = null;
        }
        public void Resume()
        {
            IsSuspended = false;
        }

        public bool TryGetChanges<T>(object key,
#if NET6_0_OR_GREATER
            [MaybeNullWhen(false)]
#endif
            out T changes) where T : TransactionChange
        { 
            if (IsSuspended)
            {
                changes = null;
                return false;
            }
            if (ActiveTransaction == null) 
                throw new InvalidOperationException("Attemted TryGetChanges with no active transaction");
            if(ActiveTransaction.TryGetChange(key, out var c))
            {
                changes = (T)c;
                return true;
            }
            changes = null;
            return false;
        }
        public void AddChange(object key, TransactionChange change)
        {
            if (IsSuspended) return;
            if (ActiveTransaction == null) 
                throw new InvalidOperationException("Attemted AddChange with no active transaction");
            ActiveTransaction.AddChange(key, change);
        }

        public IReadOnlyList<PhoneWaveRecordedTransaction> RecordedTransactions => _recorded;

        public int CurrentIndex
        {
            get => _currentIndex;
            private set
            {
                _currentIndex = value;
                OnPropertyChanged(nameof(CurrentIndex));
            }
        }

        internal PhoneWaveTransaction? ActiveTransaction { get; set; }
        public PhoneWaveTransaction BeginTransaction(object? tag = null)
        {
            if (IsSuspended)
                throw new InvalidOperationException("Attempted to begin transaction in suspended state");
            if (ActiveTransaction != null)
                throw new InvalidOperationException();
            ActiveTransaction = new PhoneWaveTransaction(this, tag);
            return ActiveTransaction;
        }

        internal void AddRecord(PhoneWaveRecordedTransaction record)
        {
            // Overwrite entries 
            while (CurrentIndex < _recorded.Count - 1)
                _recorded.RemoveAt(_recorded.Count - 1);
            _recorded.Add(record);
            
            CurrentIndex = _recorded.Count - 1;
        }

        public bool CanRollback => CurrentIndex >= 0;
        public bool CanRollForward => CurrentIndex < _recorded.Count - 1;
        
        public void Rollback()
        {
            if (!CanRollback)
                throw new InvalidOperationException();
            _recorded[CurrentIndex].RollBack();
            CurrentIndex--;
        }

        public void RollForward()
        {
            if (!CanRollForward)
                throw new InvalidOperationException();
            CurrentIndex++;
            _recorded[CurrentIndex].RollForward();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}