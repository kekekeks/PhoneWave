using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PhoneWave
{
    public class PhoneWaveContext : INotifyPropertyChanged
    {
        private ObservableCollection<PhoneWaveRecordedTransaction> _recorded = new();
        private int _currentIndex = -1;
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