#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Xunit;

namespace PhoneWave.Generator.Tests
{
    /// <summary>
    /// The transaction contract of <see cref="PhoneWaveDictionary{TKey,TValue}"/>, the same as the one of
    /// <see cref="PhoneWaveCollection{T}"/>: a change needs an active transaction. Without one it fails
    /// BEFORE the dictionary changes and records nothing; during a replay (undo/redo/abort) it fails before
    /// the dictionary changes too; while the context is suspended it applies untracked; inside a
    /// transaction it joins that ONE entry. A call that changes nothing (removing a missing key, clearing an
    /// empty dictionary) never throws and records nothing.
    /// </summary>
    public class PhoneWaveDictionaryTransactionTests
    {
        private const string Seed = "a=1;b=2;c=3";

        private const string NoTransactionMessage = "Attempted to modify a tracked dictionary without an active transaction";
        private const string ReplayMessage = "Attempted to modify a tracked dictionary while a replay (undo/redo/abort) is in progress";

        private static PhoneWaveContext NewContext()
        {
            var context = new PhoneWaveContext();
            context.Resume();
            return context;
        }

        private static PhoneWaveDictionary<string, string> NewSeededDictionary(PhoneWaveContext context)
        {
            var dictionary = new PhoneWaveDictionary<string, string>(context);
            if (context.IsSuspended)
                Fill(dictionary);
            else
            {
                using var transaction = context.BeginTransaction("seed");
                Fill(dictionary);
                transaction.Commit();
            }
            return dictionary;
        }

        private static void Fill(PhoneWaveDictionary<string, string> dictionary)
        {
            dictionary.Add("a", "1");
            dictionary.Add("b", "2");
            dictionary.Add("c", "3");
        }

        private static string Content(PhoneWaveDictionary<string, string> dictionary) =>
            string.Join(";", dictionary.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

        /// <summary>Applies one dictionary mutator by its test name.</summary>
        private static void Apply(PhoneWaveDictionary<string, string> dictionary, string operation)
        {
            switch (operation)
            {
                case "Add": dictionary.Add("d", "4"); break;
                case "AddPair": ((ICollection<KeyValuePair<string, string>>)dictionary).Add(new KeyValuePair<string, string>("d", "4")); break;
                case "SetNewKey": dictionary["d"] = "4"; break;
                case "SetExistingKey": dictionary["b"] = "9"; break;
                case "Remove": Assert.True(dictionary.Remove("b")); break;
                case "RemovePair": Assert.True(((ICollection<KeyValuePair<string, string>>)dictionary).Remove(new KeyValuePair<string, string>("b", "2"))); break;
                case "Clear": dictionary.Clear(); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        public static IEnumerable<object[]> Operations() => new[]
        {
            new object[] { "Add", "a=1;b=2;c=3;d=4" },
            new object[] { "AddPair", "a=1;b=2;c=3;d=4" },
            new object[] { "SetNewKey", "a=1;b=2;c=3;d=4" },
            new object[] { "SetExistingKey", "a=1;b=9;c=3" },
            new object[] { "Remove", "a=1;c=3" },
            new object[] { "RemovePair", "a=1;c=3" },
            new object[] { "Clear", "" },
        };

        public static IEnumerable<object[]> OperationNames()
        {
            foreach (var row in Operations())
                yield return new[] { row[0] };
        }

        [Theory]
        [MemberData(nameof(OperationNames))]
        public void WithoutATransaction_TheChangeThrows_AndLeavesTheDictionaryUnchanged(string operation)
        {
            var context = NewContext();
            var dictionary = NewSeededDictionary(context);
            var notifications = new List<NotifyCollectionChangedAction>();
            dictionary.CollectionChanged += (_, e) => notifications.Add(e.Action);
            var activeTransactionChanges = 0;
            context.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PhoneWaveContext.HasActiveTransaction))
                    activeTransactionChanges++;
            };

            var error = Assert.Throws<InvalidOperationException>(() => Apply(dictionary, operation));

            Assert.Equal(Seed, Content(dictionary));
            Assert.Empty(notifications);
            Assert.Equal(NoTransactionMessage, error.Message);
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
            var dictionary = NewSeededDictionary(context);

            Apply(dictionary, operation);

            Assert.Equal(expected, Content(dictionary));
            Assert.Empty(context.RecordedTransactions);

            context.Resume();
            Assert.Empty(context.RecordedTransactions);
            Assert.Equal(expected, Content(dictionary));
        }

        [Theory]
        [MemberData(nameof(Operations))]
        public void InsideATransaction_TheChangeRecordsOneEntry_ThatUndoesAndRedoes(string operation, string expected)
        {
            var context = NewContext();
            var dictionary = NewSeededDictionary(context);

            using (var transaction = context.BeginTransaction("edit"))
            {
                Apply(dictionary, operation);
                transaction.Commit();
            }

            Assert.Equal(expected, Content(dictionary));
            Assert.Equal(2, context.RecordedTransactions.Count);
            Assert.Equal("edit", context.CurrentItem!.Tag);

            context.Rollback();
            Assert.Equal(Seed, Content(dictionary));
            Assert.Equal("seed", context.CurrentItem!.Tag);

            context.RollForward();
            Assert.Equal(expected, Content(dictionary));
            Assert.Equal("edit", context.CurrentItem!.Tag);
        }

        public static IEnumerable<object[]> NoOperations() => new[]
        {
            new object[] { "RemoveMissingKey" },
            new object[] { "RemoveMissingPair" },
            new object[] { "ClearEmpty" },
        };

        [Theory]
        [MemberData(nameof(NoOperations))]
        public void ACallThatChangesNothing_DoesNotThrowWithoutATransaction_AndRecordsNothing(string operation)
        {
            var context = NewContext();
            var dictionary = NewSeededDictionary(context);
            if (operation == "ClearEmpty")
                using (var transaction = context.BeginTransaction("empty"))
                {
                    dictionary.Clear();
                    transaction.Commit();
                }
            var countBefore = context.RecordedTransactions.Count;
            var contentBefore = Content(dictionary);

            switch (operation)
            {
                case "RemoveMissingKey": Assert.False(dictionary.Remove("x")); break;
                case "RemoveMissingPair": Assert.False(((ICollection<KeyValuePair<string, string>>)dictionary).Remove(new KeyValuePair<string, string>("x", "1"))); break;
                case "ClearEmpty": dictionary.Clear(); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }

            Assert.Equal(contentBefore, Content(dictionary));
            Assert.Equal(countBefore, context.RecordedTransactions.Count);
            Assert.False(context.HasActiveTransaction);
        }

        [Fact]
        public void AddOfAnExistingKey_ThrowsBeforeAnythingIsRecorded()
        {
            var context = NewContext();
            var dictionary = NewSeededDictionary(context);

            using (var transaction = context.BeginTransaction("edit"))
            {
                Assert.Throws<ArgumentException>(() => dictionary.Add("a", "other"));
                dictionary["b"] = "9";
                transaction.Commit();
            }
            Assert.Equal("a=1;b=9;c=3", Content(dictionary));

            // The failed Add left no trace in the entry: the undo restores "b" and keeps "a" as it was.
            context.Rollback();
            Assert.Equal(Seed, Content(dictionary));
            context.RollForward();
            Assert.Equal("a=1;b=9;c=3", Content(dictionary));
        }

        [Fact]
        public void AChangeFromAnObserverDuringUndo_ThrowsBeforeTheDictionaryChanges()
        {
            var context = NewContext();
            var watched = NewSeededDictionary(context);
            var other = new PhoneWaveDictionary<string, string>(context);
            using (var transaction = context.BeginTransaction("edit"))
            {
                watched["b"] = "9";
                transaction.Commit();
            }

            var errors = ObserveAndWrite(watched, other);
            // The observer catches its own error, so the replay itself completes cleanly.
            context.Rollback();

            Assert.Equal(Seed, Content(watched));
            Assert.Equal("", Content(other));
            var error = Assert.Single(errors);
            Assert.Equal(ReplayMessage, error.Message);
            Assert.Equal("seed", context.CurrentItem!.Tag);
        }

        [Fact]
        public void AChangeFromAnObserverDuringAbort_ThrowsBeforeTheDictionaryChanges()
        {
            var context = NewContext();
            var watched = NewSeededDictionary(context);
            var other = new PhoneWaveDictionary<string, string>(context);
            List<Exception> errors;

            using (context.BeginTransaction("aborted"))
            {
                watched["b"] = "9";
                other["k"] = "before";
                errors = ObserveAndWrite(watched, other);
                // Disposed without Commit: the transaction aborts and replays its changes back.
            }

            Assert.Equal(Seed, Content(watched));
            Assert.Equal("", Content(other));
            Assert.NotEmpty(errors);
            Assert.All(errors, e => Assert.Equal(ReplayMessage, e.Message));
            Assert.Single(context.RecordedTransactions);
            Assert.False(context.HasActiveTransaction);
        }

        /// <summary>Writes to <paramref name="other"/> from every notification of <paramref name="watched"/>
        /// and collects what the write throws.</summary>
        private static List<Exception> ObserveAndWrite(PhoneWaveDictionary<string, string> watched, PhoneWaveDictionary<string, string> other)
        {
            var errors = new List<Exception>();
            watched.CollectionChanged += (_, _) =>
            {
                try { other["observer"] = "written"; }
                catch (InvalidOperationException e) { errors.Add(e); }
            };
            return errors;
        }
    }
}
