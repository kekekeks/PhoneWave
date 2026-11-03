#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Xunit;

namespace PhoneWave.Generator.Tests
{
    /// <summary>
    /// Transaction merge (<see cref="PhoneWaveTransaction.CommitMerging"/>): a transaction committed with
    /// the same merge key as the newest undo entry, with nothing in between, joins that entry, so one
    /// undo restores the state from before the FIRST transaction and one redo the state after the LAST.
    /// </summary>
    public partial class PhoneWaveTransactionMergeTests
    {
        /// <summary>A hand-written tracked model, shaped exactly as the generator emits it.</summary>
        private sealed class Node : PhoneWaveBase
        {
            private readonly PhoneWavePropertyStorage<int> _value = new();
            private readonly PhoneWavePropertyStorage<string?> _name = new();

            public Node(PhoneWaveContext context, string? name = null) : base(context)
            {
                Children = new PhoneWaveCollection<Node>(context);
                Fields = new PhoneWaveDictionary<string, string>(context);
                if (name is not null)
                    Name = name;
            }

            public int Value
            {
                get => _value.Value;
                set => SetValue(_value, value);
            }

            public string? Name
            {
                get => _name.Value;
                set => SetValue(_name, value);
            }

            public PhoneWaveCollection<Node> Children { get; }

            public PhoneWaveDictionary<string, string> Fields { get; }

            protected override void InitializeStorage()
            {
                _value.Initialize(this, nameof(Value));
                _name.Initialize(this, nameof(Name));
                base.InitializeStorage();
            }
        }

        /// <summary>A change that only logs its replays; it never absorbs a later change.</summary>
        private class LogChange : TransactionChange
        {
            private readonly List<string> _log;
            private readonly string _name;

            public LogChange(List<string> log, string name)
            {
                _log = log;
                _name = name;
            }

            public override void Rollback() => _log.Add("undo " + _name);

            public override void RollForward() => _log.Add("redo " + _name);
        }

        /// <summary>A change that opts into merging by absorbing a later change of its own kind.</summary>
        private sealed class AbsorbingLogChange : LogChange
        {
            public AbsorbingLogChange(List<string> log, string name) : base(log, name)
            {
            }

            public int Absorbed { get; private set; }

            protected override bool TryAbsorb(TransactionChange later)
            {
                if (later is not AbsorbingLogChange)
                    return false;
                Absorbed++;
                return true;
            }
        }

        /// <summary>A change whose merge hook fails.</summary>
        private sealed class ThrowingAbsorbChange : LogChange
        {
            public ThrowingAbsorbChange(List<string> log, string name) : base(log, name)
            {
            }

            protected override bool TryAbsorb(TransactionChange later) =>
                throw new InvalidOperationException("absorb failed");
        }

        private static PhoneWaveContext NewContext()
        {
            var context = new PhoneWaveContext();
            context.Resume();
            return context;
        }

        private static void Commit(PhoneWaveContext context, Action change, object? tag = null)
        {
            using var transaction = context.BeginTransaction(tag);
            change();
            transaction.Commit();
        }

        private static PhoneWaveCommitOutcome CommitMerging(PhoneWaveContext context, object? mergeKey, Action change,
            object? tag = null, Func<object?, object?, object?>? mergeTags = null)
        {
            using var transaction = context.BeginTransaction(tag);
            change();
            return transaction.CommitMerging(mergeKey, mergeTags);
        }

        private static string Names(IEnumerable<Node> nodes) => string.Join(",", nodes.Select(n => n.Name));

        private static string Content(PhoneWaveDictionary<string, string> dictionary) =>
            string.Join(";", dictionary.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

        [Fact]
        public void PropertyChanges_MergeIntoOneEntry_UndoRestoresBeforeFirst_RedoAfterLast()
        {
            var context = NewContext();
            var node = new Node(context);
            Commit(context, () => node.Value = 1);

            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "run", () => node.Value = 2));
            Assert.Equal(PhoneWaveCommitOutcome.Merged, CommitMerging(context, "run", () => node.Value = 3));
            Assert.Equal(2, context.RecordedTransactions.Count);

            var notified = new List<string?>();
            node.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            context.Rollback();
            Assert.Equal(1, node.Value);
            // The two writes of the property became one change: one notification.
            Assert.Equal(new[] { nameof(Node.Value) }, notified);

            context.RollForward();
            Assert.Equal(3, node.Value);
            Assert.False(context.CanRollForward);

            // The entry before the merged one is untouched.
            context.Rollback();
            context.Rollback();
            Assert.Equal(0, node.Value);
        }

        [Fact]
        public void PropertyChanges_ThatEndEqual_KeepTheEntry()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run", () => node.Value = 5);
            CommitMerging(context, "run", () => node.Value = 0);

            Assert.Single(context.RecordedTransactions);
            context.Rollback();
            Assert.Equal(0, node.Value);
            context.RollForward();
            Assert.Equal(0, node.Value);
        }

        [Fact]
        public void DifferentPropertiesAndObjects_AllReplay()
        {
            var context = NewContext();
            var a = new Node(context);
            var b = new Node(context);

            CommitMerging(context, "run", () =>
            {
                a.Value = 1;
                a.Name = "a1";
            });
            CommitMerging(context, "run", () =>
            {
                b.Value = 2;
                a.Name = "a2";
            });

            context.Rollback();
            Assert.Equal((0, (string?)null, 0), (a.Value, a.Name, b.Value));
            context.RollForward();
            Assert.Equal((1, (string?)"a2", 2), (a.Value, a.Name, b.Value));
        }

        [Fact]
        public void CollectionChanges_MergeInOrder()
        {
            var context = NewContext();
            var root = new Node(context);
            Node x = null!, y = null!, z = null!;
            Commit(context, () =>
            {
                x = new Node(context, "x");
                y = new Node(context, "y");
                z = new Node(context, "z");
                root.Children.Add(x);
            });

            CommitMerging(context, "run", () =>
            {
                root.Children.Add(y);
                root.Children.Insert(0, z);
            });
            CommitMerging(context, "run", () =>
            {
                root.Children.Move(0, 2);
                root.Children.Remove(x);
                root.Children[0] = x;
            });
            Assert.Equal("x,z", Names(root.Children));
            Assert.Equal(2, context.RecordedTransactions.Count);

            var log = new List<NotifyCollectionChangedAction>();
            root.Children.CollectionChanged += (_, e) => log.Add(e.Action);

            context.Rollback();
            Assert.Equal("x", Names(root.Children));
            context.RollForward();
            Assert.Equal("x,z", Names(root.Children));

            // Every recorded insert/remove (2 + 2 + 1 + 2) replays once in each direction.
            Assert.Equal(2 * 7, log.Count);
        }

        [Fact]
        public void ObjectCreatedInFirst_AndDeletedInSecond_UndoAndRedoBothRestoreTheEmptyCollection()
        {
            var context = NewContext();
            var root = new Node(context);
            Node created = null!;

            CommitMerging(context, "run", () =>
            {
                created = new Node(context, "new");
                created.Value = 7;
                root.Children.Add(created);
            });
            CommitMerging(context, "run", () =>
            {
                created.Value = 8;
                root.Children.Remove(created);
            });

            Assert.Single(context.RecordedTransactions);
            Assert.Empty(root.Children);

            context.Rollback();
            Assert.Empty(root.Children);
            Assert.Equal((0, (string?)null), (created.Value, created.Name));

            context.RollForward();
            Assert.Empty(root.Children);
            Assert.Equal((8, (string?)"new"), (created.Value, created.Name));
        }

        [Fact]
        public void ObjectDeletedInFirst_AndAddedBackInSecond_ReplaysTheMove()
        {
            var context = NewContext();
            var root = new Node(context);
            var other = new Node(context);
            Node a = null!, b = null!;
            Commit(context, () =>
            {
                a = new Node(context, "a");
                b = new Node(context, "b");
                root.Children.Add(a);
                root.Children.Add(b);
            });

            CommitMerging(context, "run", () => root.Children.Remove(a));
            CommitMerging(context, "run", () => other.Children.Add(a));

            context.Rollback();
            Assert.Equal("a,b", Names(root.Children));
            Assert.Empty(other.Children);

            context.RollForward();
            Assert.Equal("b", Names(root.Children));
            Assert.Equal("a", Names(other.Children));
        }

        [Fact]
        public void DictionaryChanges_KeepTheFirstOldSide_AndTheLastNewSide()
        {
            var context = NewContext();
            var node = new Node(context);
            Commit(context, () =>
            {
                node.Fields.Add("kept", "1");
                node.Fields.Add("removed", "2");
            });

            CommitMerging(context, "run", () =>
            {
                node.Fields.Add("added", "a");
                node.Fields["kept"] = "1b";
                node.Fields.Add("transient", "t");
            });
            CommitMerging(context, "run", () =>
            {
                node.Fields["added"] = "a2";
                node.Fields["kept"] = "1c";
                node.Fields.Remove("removed");
                node.Fields.Remove("transient");
            });
            Assert.Equal("added=a2;kept=1c", Content(node.Fields));

            context.Rollback();
            Assert.Equal("kept=1;removed=2", Content(node.Fields));
            context.RollForward();
            Assert.Equal("added=a2;kept=1c", Content(node.Fields));
        }

        [Fact]
        public void AnotherCommitInBetween_PreventsTheMerge()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run", () => node.Value = 1);
            Commit(context, () => node.Name = "user edit");
            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "run", () => node.Value = 2));

            Assert.Equal(3, context.RecordedTransactions.Count);
        }

        [Fact]
        public void AnAutoCommittedCollectionEditInBetween_PreventsTheMerge()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run", () => node.Value = 1);
            node.Children.Add(new Node(context));
            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "run", () => node.Value = 2));

            Assert.Equal(3, context.RecordedTransactions.Count);
        }

        [Fact]
        public void AfterAnUndo_TheNextCommitIsANewEntry()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run", () => node.Value = 1);
            CommitMerging(context, "run", () => node.Name = "n");
            context.Rollback();
            Assert.Equal((0, (string?)null), (node.Value, node.Name));

            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "run", () => node.Value = 2));
            // The undone entry was dropped from the redo side, as every commit drops it.
            Assert.Single(context.RecordedTransactions);
            context.Rollback();
            Assert.Equal(0, node.Value);
        }

        [Fact]
        public void AfterAnUndoOfAnotherEntry_TheNowCurrentEntryIsNotAMergeTarget()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run", () => node.Value = 1);
            Commit(context, () => node.Name = "user edit");
            context.Rollback();

            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "run", () => node.Value = 2));
            Assert.Equal(2, context.RecordedTransactions.Count);
        }

        [Fact]
        public void AfterAnUndoAndRedo_TheNextCommitIsANewEntry()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run", () => node.Value = 1);
            context.Rollback();
            context.RollForward();

            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "run", () => node.Value = 2));
            Assert.Equal(2, context.RecordedTransactions.Count);
        }

        [Fact]
        public void ADifferentKey_RecordsANewEntry()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run 1", () => node.Value = 1);
            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "run 2", () => node.Value = 2));

            Assert.Equal(2, context.RecordedTransactions.Count);
            Assert.Equal("run 2", context.CurrentItem!.MergeKey);
        }

        [Fact]
        public void KeysAreComparedWithEquals()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, new string('k', 3), () => node.Value = 1);
            Assert.Equal(PhoneWaveCommitOutcome.Merged, CommitMerging(context, new string('k', 3), () => node.Value = 2));
            Assert.Single(context.RecordedTransactions);
        }

        [Fact]
        public void ANullKey_NeverMerges()
        {
            var context = NewContext();
            var node = new Node(context);

            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, null, () => node.Value = 1));
            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, null, () => node.Value = 2));

            Assert.Equal(2, context.RecordedTransactions.Count);
            Assert.Null(context.CurrentItem!.MergeKey);
        }

        [Fact]
        public void APlainCommit_CarriesNoKey_AndIsNotAMergeTarget()
        {
            var context = NewContext();
            var node = new Node(context);

            Commit(context, () => node.Value = 1);
            Assert.Null(context.CurrentItem!.MergeKey);
            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "run", () => node.Value = 2));
            Assert.Equal(2, context.RecordedTransactions.Count);
        }

        [Fact]
        public void AnEmptyTransaction_RecordsNothing_AndKeepsTheRunOpen()
        {
            var context = NewContext();
            var node = new Node(context);

            // No entry to merge into, and nothing to record: no empty entry appears.
            Assert.Equal(PhoneWaveCommitOutcome.NothingRecorded, CommitMerging(context, "run", () => { }));
            Assert.Empty(context.RecordedTransactions);

            CommitMerging(context, "run", () => node.Value = 1);
            var entry = context.CurrentItem;
            Assert.Equal(PhoneWaveCommitOutcome.NothingRecorded, CommitMerging(context, "run", () => { }));
            Assert.Same(entry, context.CurrentItem);

            // The empty call did not end the run.
            Assert.Equal(PhoneWaveCommitOutcome.Merged, CommitMerging(context, "run", () => node.Value = 2));
            Assert.Single(context.RecordedTransactions);

            // And the context accepts a new transaction after the empty commit.
            Assert.Equal(PhoneWaveCommitOutcome.NothingRecorded, CommitMerging(context, "other", () => { }));
            using var next = context.BeginTransaction();
        }

        [Fact]
        public void ARolledBackTransactionInBetween_DoesNotPreventTheMerge()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run", () => node.Value = 1);
            using (context.BeginTransaction())
                node.Value = 100;
            Assert.Equal(1, node.Value);

            Assert.Equal(PhoneWaveCommitOutcome.Merged, CommitMerging(context, "run", () => node.Value = 2));
            context.Rollback();
            Assert.Equal(0, node.Value);
        }

        [Fact]
        public void ACommittedTransaction_CannotBeCommittedAgain()
        {
            var context = NewContext();
            var node = new Node(context);

            using var transaction = context.BeginTransaction();
            node.Value = 1;
            Assert.Equal(PhoneWaveCommitOutcome.Recorded, transaction.CommitMerging("run"));
            Assert.Equal(PhoneWaveCommitOutcome.NothingRecorded, transaction.CommitMerging("run"));
            Assert.Single(context.RecordedTransactions);
        }

        [Fact]
        public void UndoReplaysTheLaterTransactionFirst_RedoTheEarlierFirst()
        {
            var context = NewContext();
            var log = new List<string>();
            var a = new Node(context);
            var b = new Node(context);
            var sharedKey = new object();
            var notified = new List<string>();
            a.PropertyChanged += (_, e) => notified.Add("a." + e.PropertyName);
            b.PropertyChanged += (_, e) => notified.Add("b." + e.PropertyName);

            CommitMerging(context, "run", () =>
            {
                a.Value = 1;
                context.AddChange(sharedKey, new LogChange(log, "shared 1"));
                context.AddChange(new object(), new LogChange(log, "first"));
            });
            CommitMerging(context, "run", () =>
            {
                b.Value = 2;
                context.AddChange(sharedKey, new LogChange(log, "shared 2"));
                context.AddChange(new object(), new LogChange(log, "second"));
            });

            var rolling = new List<bool>();
            context.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PhoneWaveContext.IsRolling))
                    rolling.Add(context.IsRolling);
            };

            notified.Clear();
            context.Rollback();
            // A change that does not absorb stays separate: the later one replays first on undo.
            Assert.Equal(new[] { "undo shared 2", "undo second", "undo shared 1", "undo first" }, log);
            Assert.Equal(new[] { "b.Value", "a.Value" }, notified);
            // One undo is one replay: one rising and one falling IsRolling edge.
            Assert.Equal(new[] { true, false }, rolling);

            log.Clear();
            notified.Clear();
            rolling.Clear();
            context.RollForward();
            Assert.Equal(new[] { "redo shared 1", "redo first", "redo shared 2", "redo second" }, log);
            Assert.Equal(new[] { "a.Value", "b.Value" }, notified);
            Assert.Equal(new[] { true, false }, rolling);
        }

        [Fact]
        public void AChangeThatOptsIn_AbsorbsTheLaterChangeUnderTheSameKey()
        {
            var context = NewContext();
            var log = new List<string>();
            var key = new object();
            var first = new AbsorbingLogChange(log, "first");

            CommitMerging(context, "run", () => context.AddChange(key, first));
            CommitMerging(context, "run", () => context.AddChange(key, new AbsorbingLogChange(log, "second")));
            CommitMerging(context, "run", () => context.AddChange(key, new AbsorbingLogChange(log, "third")));

            Assert.Equal(2, first.Absorbed);
            context.Rollback();
            Assert.Equal(new[] { "undo first" }, log);
        }

        [Fact]
        public void AFailingAbsorb_KeepsTheChangeSeparate_AndSurfacesAfterTheMerge()
        {
            var context = NewContext();
            var log = new List<string>();
            var key = new object();
            var node = new Node(context);

            CommitMerging(context, "run", () =>
            {
                node.Value = 1;
                context.AddChange(key, new ThrowingAbsorbChange(log, "first"));
            });
            var failure = Assert.Throws<InvalidOperationException>(() => CommitMerging(context, "run", () =>
            {
                node.Value = 2;
                context.AddChange(key, new LogChange(log, "second"));
            }));
            Assert.Equal("absorb failed", failure.Message);

            // The merge completed: one entry, and the context is free again.
            Assert.Single(context.RecordedTransactions);
            context.Rollback();
            Assert.Equal(0, node.Value);
            Assert.Equal(new[] { "undo second", "undo first" }, log);
            context.RollForward();
            Assert.Equal(2, node.Value);
        }

        [Fact]
        public void TheMergedEntry_KeepsTheFirstTagByDefault()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run", () => node.Value = 1, tag: "first");
            CommitMerging(context, "run", () => node.Value = 2, tag: "second");

            Assert.Equal("first", context.CurrentItem!.Tag);
            Assert.Equal("run", context.CurrentItem.MergeKey);
        }

        [Fact]
        public void TheMergedEntry_TakesTheTagTheCallerComputes()
        {
            var context = NewContext();
            var node = new Node(context);
            var seen = new List<(object?, object?)>();

            CommitMerging(context, "run", () => node.Value = 1, tag: "call 1");
            CommitMerging(context, "run", () => node.Value = 2, tag: "call 2",
                mergeTags: (previous, current) =>
                {
                    seen.Add((previous, current));
                    return "script: 2 calls";
                });

            Assert.Equal(new[] { ((object?)"call 1", (object?)"call 2") }, seen);
            Assert.Equal("script: 2 calls", context.CurrentItem!.Tag);
        }

        [Fact]
        public void AThrowingTagDelegate_LeavesTheTransactionUncommitted()
        {
            var context = NewContext();
            var node = new Node(context);
            CommitMerging(context, "run", () => node.Value = 1, tag: "first");
            var entry = context.CurrentItem;

            Assert.Throws<InvalidOperationException>(() => CommitMerging(context, "run", () => node.Value = 2,
                mergeTags: (_, _) => throw new InvalidOperationException("tag")));

            // The using block rolled the uncommitted transaction back; the history did not change.
            Assert.Equal(1, node.Value);
            Assert.Same(entry, context.CurrentItem);
            Assert.Equal(PhoneWaveCommitOutcome.Merged, CommitMerging(context, "run", () => node.Value = 3));
        }

        [Fact]
        public void AMerge_ReplacesTheEntryInTheHistory_AndRetiresThePreviousEntry()
        {
            var context = NewContext();
            var node = new Node(context);
            CommitMerging(context, "run", () => node.Value = 1);
            var previous = context.CurrentItem!;

            var history = new List<NotifyCollectionChangedEventArgs>();
            ((INotifyCollectionChanged)context.RecordedTransactions).CollectionChanged += (_, e) => history.Add(e);
            var properties = new List<string?>();
            // HasActiveTransaction rises and falls around every transaction; this test is about the history.
            context.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(PhoneWaveContext.HasActiveTransaction))
                    properties.Add(e.PropertyName);
            };

            CommitMerging(context, "run", () => node.Value = 2);

            var replace = Assert.Single(history);
            Assert.Equal(NotifyCollectionChangedAction.Replace, replace.Action);
            Assert.Same(previous, replace.OldItems![0]);
            Assert.Same(context.CurrentItem, replace.NewItems![0]);
            Assert.NotSame(previous, context.CurrentItem);
            Assert.Equal(new[] { nameof(PhoneWaveContext.CurrentItem) }, properties);
            Assert.Equal(0, context.CurrentIndex);

            // The previous entry handed its changes over: replaying it on its own is refused.
            Assert.Throws<InvalidOperationException>(() => previous.RollBack());
        }

        [Fact]
        public void ALongRun_MergesIntoOneEntry()
        {
            var context = NewContext();
            var root = new Node(context);
            Commit(context, () => root.Name = "root");

            for (var call = 1; call <= 5; call++)
            {
                var n = call;
                var outcome = CommitMerging(context, "script", () =>
                {
                    root.Value = n;
                    root.Children.Add(new Node(context, "c" + n));
                    root.Fields["last"] = "c" + n;
                }, tag: "call " + n);
                Assert.Equal(n == 1 ? PhoneWaveCommitOutcome.Recorded : PhoneWaveCommitOutcome.Merged, outcome);
            }

            Assert.Equal(2, context.RecordedTransactions.Count);
            Assert.Equal("call 1", context.CurrentItem!.Tag);

            context.Rollback();
            Assert.Equal((0, ""), (root.Value, Names(root.Children)));
            Assert.Empty(root.Fields);
            Assert.Equal("root", root.Name);

            context.RollForward();
            Assert.Equal((5, "c1,c2,c3,c4,c5"), (root.Value, Names(root.Children)));
            Assert.Equal("last=c5", Content(root.Fields));
        }
    }
}
