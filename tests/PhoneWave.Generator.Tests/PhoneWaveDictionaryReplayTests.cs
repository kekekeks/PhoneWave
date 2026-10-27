#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Xunit;

namespace PhoneWave.Generator.Tests
{
    /// <summary>
    /// Undo/redo/abort of <see cref="PhoneWaveDictionary{TKey,TValue}"/>: every replay must drive each
    /// key to its recorded side exactly (present with the value, or absent), and raise the same
    /// collection notifications the normal edit path raises for that move.
    /// </summary>
    public class PhoneWaveDictionaryReplayTests
    {
        private static PhoneWaveContext NewContext()
        {
            var context = new PhoneWaveContext();
            context.Resume();
            return context;
        }

        private static void Commit(PhoneWaveContext context, Action change)
        {
            using var transaction = context.BeginTransaction();
            change();
            transaction.Commit();
        }

        private static List<string> RecordNotifications(PhoneWaveDictionary<string, string?> dictionary)
        {
            var log = new List<string>();
            dictionary.CollectionChanged += (_, e) => log.Add(Describe(e));
            return log;
        }

        private static string Describe(NotifyCollectionChangedEventArgs e) => e.Action switch
        {
            NotifyCollectionChangedAction.Add => "Add " + Items(e.NewItems),
            NotifyCollectionChangedAction.Remove => "Remove " + Items(e.OldItems),
            NotifyCollectionChangedAction.Replace => "Replace " + Items(e.OldItems) + " -> " + Items(e.NewItems),
            _ => e.Action.ToString(),
        };

        private static string Items(IList? items) =>
            items is null
                ? "<none>"
                : string.Join(",", items.Cast<KeyValuePair<string, string?>>().Select(p => p.Key + "=" + (p.Value ?? "<null>")));

        private static string Content(PhoneWaveDictionary<string, string?> dictionary) =>
            string.Join(";", dictionary.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + (p.Value ?? "<null>")));

        /// <summary>Undo, then redo, of the last committed transaction; returns the notifications of each.</summary>
        private static (string[] Undo, string[] Redo) UndoRedo(PhoneWaveContext context, List<string> log,
            PhoneWaveDictionary<string, string?> dictionary, string expectedAfterUndo, string expectedAfterRedo)
        {
            log.Clear();
            context.Rollback();
            Assert.Equal(expectedAfterUndo, Content(dictionary));
            var undo = log.ToArray();

            log.Clear();
            context.RollForward();
            Assert.Equal(expectedAfterRedo, Content(dictionary));
            var redo = log.ToArray();
            return (undo, redo);
        }

        [Fact]
        public void UndoOfAddedKey_RemovesIt_AndRedoAddsItBack()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            var log = RecordNotifications(dictionary);

            Commit(context, () => dictionary.Add("k", "v"));
            Assert.Equal(new[] { "Add k=v" }, log);

            var (undo, redo) = UndoRedo(context, log, dictionary, "", "k=v");
            Assert.Equal(new[] { "Remove k=v" }, undo);
            Assert.Equal(new[] { "Add k=v" }, redo);
        }

        [Fact]
        public void UndoOfKeyAddedThroughIndexer_RemovesIt()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            var log = RecordNotifications(dictionary);

            Commit(context, () => dictionary["k"] = "v");

            var (undo, redo) = UndoRedo(context, log, dictionary, "", "k=v");
            Assert.Equal(new[] { "Remove k=v" }, undo);
            Assert.Equal(new[] { "Add k=v" }, redo);
        }

        [Fact]
        public void UndoOfRemovedKey_RestoresIt_AndRedoRemovesItAgain()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            Commit(context, () => dictionary.Add("k", "v"));
            var log = RecordNotifications(dictionary);

            Commit(context, () => Assert.True(dictionary.Remove("k")));
            Assert.Equal(new[] { "Remove k=v" }, log);

            var (undo, redo) = UndoRedo(context, log, dictionary, "k=v", "");
            Assert.Equal(new[] { "Add k=v" }, undo);
            Assert.Equal(new[] { "Remove k=v" }, redo);
        }

        [Fact]
        public void UndoOfReplacedValue_RestoresTheOldValue_AndRedoTheNewOne()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            Commit(context, () => dictionary.Add("k", "a"));
            var log = RecordNotifications(dictionary);

            Commit(context, () => dictionary["k"] = "b");
            Assert.Equal(new[] { "Replace k=a -> k=b" }, log);

            var (undo, redo) = UndoRedo(context, log, dictionary, "k=a", "k=b");
            Assert.Equal(new[] { "Replace k=b -> k=a" }, undo);
            Assert.Equal(new[] { "Replace k=a -> k=b" }, redo);
        }

        [Fact]
        public void AddThenChangeInOneTransaction_UndoRemovesTheKey_RedoAddsTheFinalValue()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            var log = RecordNotifications(dictionary);

            Commit(context, () =>
            {
                dictionary.Add("k", "v1");
                dictionary["k"] = "v2";
            });

            var (undo, redo) = UndoRedo(context, log, dictionary, "", "k=v2");
            Assert.Equal(new[] { "Remove k=v2" }, undo);
            Assert.Equal(new[] { "Add k=v2" }, redo);
        }

        [Fact]
        public void AddThenRemoveInOneTransaction_ReplaysAsNoChange_WithoutNotifications()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            var log = RecordNotifications(dictionary);

            Commit(context, () =>
            {
                dictionary.Add("k", "v");
                Assert.True(dictionary.Remove("k"));
            });

            var (undo, redo) = UndoRedo(context, log, dictionary, "", "");
            Assert.Empty(undo);
            Assert.Empty(redo);
        }

        [Fact]
        public void RemoveThenAddInOneTransaction_UndoRestoresTheOldValue_RedoTheNewOne()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            Commit(context, () => dictionary.Add("k", "a"));
            var log = RecordNotifications(dictionary);

            Commit(context, () =>
            {
                Assert.True(dictionary.Remove("k"));
                dictionary.Add("k", "b");
            });

            var (undo, redo) = UndoRedo(context, log, dictionary, "k=a", "k=b");
            Assert.Equal(new[] { "Replace k=b -> k=a" }, undo);
            Assert.Equal(new[] { "Replace k=a -> k=b" }, redo);
        }

        [Fact]
        public void NullValues_AreValues_NotAbsence()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            var log = RecordNotifications(dictionary);

            // Added with a null value: undo removes the key, redo adds it back WITH the null value.
            Commit(context, () => dictionary.Add("k", null));
            var (undo, redo) = UndoRedo(context, log, dictionary, "", "k=<null>");
            Assert.Equal(new[] { "Remove k=<null>" }, undo);
            Assert.Equal(new[] { "Add k=<null>" }, redo);

            // Removed while holding a null value: undo restores the key with the null value.
            Commit(context, () => Assert.True(dictionary.Remove("k")));
            (undo, redo) = UndoRedo(context, log, dictionary, "k=<null>", "");
            Assert.Equal(new[] { "Add k=<null>" }, undo);
            Assert.Equal(new[] { "Remove k=<null>" }, redo);

            // Replaced from null to a value: undo restores null.
            Commit(context, () => dictionary.Add("n", null));
            Commit(context, () => dictionary["n"] = "x");
            (undo, redo) = UndoRedo(context, log, dictionary, "n=<null>", "n=x");
            Assert.Equal(new[] { "Replace n=x -> n=<null>" }, undo);
            Assert.Equal(new[] { "Replace n=<null> -> n=x" }, redo);
        }

        [Fact]
        public void SeveralKeysInOneTransaction_ReplayEachKey()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            Commit(context, () =>
            {
                dictionary.Add("changed", "old");
                dictionary.Add("removed", "gone");
            });
            var log = RecordNotifications(dictionary);

            Commit(context, () =>
            {
                dictionary.Add("added", "new");
                dictionary["changed"] = "new";
                Assert.True(dictionary.Remove("removed"));
            });

            UndoRedo(context, log, dictionary, "changed=old;removed=gone", "added=new;changed=new");
        }

        [Fact]
        public void Clear_UndoRestoresEveryKey_RedoClearsAgain()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            Commit(context, () =>
            {
                dictionary.Add("a", "1");
                dictionary.Add("b", "2");
            });
            var log = RecordNotifications(dictionary);

            Commit(context, () => dictionary.Clear());

            UndoRedo(context, log, dictionary, "a=1;b=2", "");
        }

        [Fact]
        public void SameKeyInTwoDictionariesInOneTransaction_ReplaysBothDictionaries()
        {
            var context = NewContext();
            var first = new PhoneWaveDictionary<string, string?>(context);
            var second = new PhoneWaveDictionary<string, string?>(context);
            Commit(context, () => second.Add("k", "old"));

            Commit(context, () =>
            {
                first.Add("k", "one");
                second["k"] = "two";
            });

            context.Rollback();
            Assert.Equal("", Content(first));
            Assert.Equal("k=old", Content(second));

            context.RollForward();
            Assert.Equal("k=one", Content(first));
            Assert.Equal("k=two", Content(second));
        }

        [Fact]
        public void AbortOfUncommittedTransaction_RemovesAnAddedKey()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            Commit(context, () => dictionary.Add("kept", "v"));
            var log = RecordNotifications(dictionary);

            using (context.BeginTransaction())
            {
                dictionary.Add("k", "v");
                dictionary["kept"] = "w";
            }

            Assert.Equal("kept=v", Content(dictionary));
            Assert.Equal(new[] { "Add k=v", "Replace kept=v -> kept=w", "Remove k=v", "Replace kept=w -> kept=v" }, log);
        }

        [Fact]
        public void RemoveOfPair_UndoRestoresTheValueThatWasStored()
        {
            var context = NewContext();
            var dictionary = new PhoneWaveDictionary<string, string?>(context);
            Commit(context, () => dictionary.Add("k", "stored"));

            Commit(context, () => dictionary.Remove(new KeyValuePair<string, string?>("k", "other")));

            context.Rollback();
            Assert.Equal("k=stored", Content(dictionary));
        }
    }
}
