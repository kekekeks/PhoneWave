#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Xunit;

namespace PhoneWave.Generator.Tests
{
    /// <summary>
    /// The transaction contract of <see cref="PhoneWaveCollection{T}"/>: a change needs an active
    /// transaction, exactly as a tracked property write or a dictionary change does. Without one it
    /// fails BEFORE the collection changes and records nothing (it never opens a transaction of its own);
    /// while the context is suspended it applies untracked; inside a transaction it joins that ONE entry.
    /// </summary>
    public class PhoneWaveCollectionTransactionTests
    {
        private const string Seed = "a,b,c";

        private static PhoneWaveCollection<string> NewSeededCollection(PhoneWaveContext context)
        {
            var collection = new PhoneWaveCollection<string>(context);
            if (context.IsSuspended)
                Fill(collection);
            else
            {
                using var transaction = context.BeginTransaction("seed");
                Fill(collection);
                transaction.Commit();
            }
            return collection;
        }

        private static void Fill(PhoneWaveCollection<string> collection)
        {
            collection.Add("a");
            collection.Add("b");
            collection.Add("c");
        }

        private static string Content(PhoneWaveCollection<string> collection) => string.Join(",", collection);

        /// <summary>Applies one collection operation by its test name.</summary>
        private static void Apply(PhoneWaveCollection<string> collection, string operation)
        {
            switch (operation)
            {
                case "Add": collection.Add("d"); break;
                case "Insert": collection.Insert(0, "d"); break;
                case "Remove": collection.Remove("b"); break;
                case "RemoveAt": collection.RemoveAt(0); break;
                case "Move": collection.Move(0, 2); break;
                case "Set": collection[1] = "d"; break;
                case "Clear": collection.Clear(); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        public static IEnumerable<object[]> Operations() => new[]
        {
            new object[] { "Add", "a,b,c,d" },
            new object[] { "Insert", "d,a,b,c" },
            new object[] { "Remove", "a,c" },
            new object[] { "RemoveAt", "b,c" },
            new object[] { "Move", "b,c,a" },
            new object[] { "Set", "a,d,c" },
            new object[] { "Clear", "" },
        };

        public static IEnumerable<object[]> OperationNames()
        {
            foreach (var row in Operations())
                yield return new[] { row[0] };
        }

        [Theory]
        [MemberData(nameof(OperationNames))]
        public void WithoutATransaction_TheChangeThrows_AndLeavesTheCollectionUnchanged(string operation)
        {
            var context = new PhoneWaveContext();
            context.Resume();
            var collection = NewSeededCollection(context);
            var notifications = new List<NotifyCollectionChangedAction>();
            collection.CollectionChanged += (_, e) => notifications.Add(e.Action);
            var activeTransactionChanges = 0;
            context.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PhoneWaveContext.HasActiveTransaction))
                    activeTransactionChanges++;
            };

            var error = Assert.Throws<InvalidOperationException>(() => Apply(collection, operation));

            Assert.Equal("Attempted to modify a tracked collection without an active transaction", error.Message);
            Assert.Equal(Seed, Content(collection));
            Assert.Empty(notifications);
            // Nothing recorded and no transaction opened on the side: the seed stays the only entry.
            Assert.Single(context.RecordedTransactions);
            Assert.Equal("seed", context.CurrentItem!.Tag);
            Assert.Equal(0, activeTransactionChanges);
            Assert.False(context.HasActiveTransaction);
        }

        [Theory]
        [MemberData(nameof(Operations))]
        public void WhileSuspended_TheChangeApplies_AndRecordsNothing(string operation, string expected)
        {
            // A new context is suspended until Resume (the load path).
            var context = new PhoneWaveContext();
            Assert.True(context.IsSuspended);
            var collection = NewSeededCollection(context);

            Apply(collection, operation);

            Assert.Equal(expected, Content(collection));
            Assert.Empty(context.RecordedTransactions);

            context.Resume();
            Assert.Empty(context.RecordedTransactions);
        }

        [Theory]
        [MemberData(nameof(Operations))]
        public void InsideATransaction_TheChangeRecordsOneEntry_ThatUndoesAndRedoes(string operation, string expected)
        {
            var context = new PhoneWaveContext();
            context.Resume();
            var collection = NewSeededCollection(context);

            using (var transaction = context.BeginTransaction("edit"))
            {
                Apply(collection, operation);
                transaction.Commit();
            }

            Assert.Equal(expected, Content(collection));
            Assert.Equal(2, context.RecordedTransactions.Count);
            Assert.Equal("edit", context.CurrentItem!.Tag);

            context.Rollback();
            Assert.Equal(Seed, Content(collection));
            Assert.Equal("seed", context.CurrentItem!.Tag);

            context.RollForward();
            Assert.Equal(expected, Content(collection));
            Assert.Equal("edit", context.CurrentItem!.Tag);
        }

        [Fact]
        public void AChangeFromAnObserverDuringUndo_ThrowsBeforeTheCollectionChanges()
        {
            var context = new PhoneWaveContext();
            context.Resume();
            var watched = NewSeededCollection(context);
            var other = new PhoneWaveCollection<string>(context);
            using (var transaction = context.BeginTransaction("edit"))
            {
                watched.Add("d");
                transaction.Commit();
            }
            var errors = new List<Exception>();
            watched.CollectionChanged += (_, _) =>
            {
                try { other.Add("observer"); }
                catch (InvalidOperationException e) { errors.Add(e); }
            };

            context.Rollback();

            Assert.Equal(Seed, Content(watched));
            Assert.Empty(other);
            var error = Assert.Single(errors);
            Assert.Equal("Attempted to modify a tracked collection while a replay (undo/redo/abort) is in progress", error.Message);
        }
    }
}
