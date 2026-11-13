#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Xunit;

namespace PhoneWave.Generator.Tests
{
    /// <summary>
    /// The merge API on the context: the ambient merge key (<see cref="PhoneWaveContext.UseMergeKey"/>) that
    /// makes a plain <see cref="PhoneWaveTransaction.Commit"/> merge, <see cref="PhoneWaveContext.EndMerge"/>,
    /// <see cref="PhoneWaveContext.CanMerge"/>, <see cref="PhoneWaveContext.TryRetagMerge"/>,
    /// <see cref="PhoneWaveRecordedTransaction.CommitCount"/> and <see cref="PhoneWaveContext.HasActiveTransaction"/>.
    /// </summary>
    public partial class PhoneWaveTransactionMergeTests
    {
        private static List<NotifyCollectionChangedEventArgs> ObserveHistory(PhoneWaveContext context)
        {
            var history = new List<NotifyCollectionChangedEventArgs>();
            ((INotifyCollectionChanged)context.RecordedTransactions).CollectionChanged += (_, e) => history.Add(e);
            return history;
        }

        /// <summary>Records each HasActiveTransaction notification with the value at that moment.</summary>
        private static List<bool> ObserveActiveTransaction(PhoneWaveContext context)
        {
            var values = new List<bool>();
            context.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PhoneWaveContext.HasActiveTransaction))
                    values.Add(context.HasActiveTransaction);
            };
            return values;
        }

        // ---- Ambient merge key ------------------------------------------------------------------------------

        [Fact]
        public void AmbientScope_PlainCommitsMergeIntoOneEntry()
        {
            var context = NewContext();
            var node = new Node(context);
            Commit(context, () => node.Name = "before");

            using (context.UseMergeKey("run"))
            {
                Assert.Equal("run", context.AmbientMergeKey);
                Commit(context, () => node.Value = 1);
                Commit(context, () => node.Value = 2);
                Commit(context, () => node.Name = "after");
            }

            Assert.Equal(2, context.RecordedTransactions.Count);
            Assert.Equal("run", context.CurrentItem!.MergeKey);
            Assert.Equal(3, context.CurrentItem.CommitCount);

            context.Rollback();
            Assert.Equal((0, (string?)"before"), (node.Value, node.Name));
            context.RollForward();
            Assert.Equal((2, (string?)"after"), (node.Value, node.Name));
        }

        [Fact]
        public void AmbientScope_ACollectionOnlyCommit_Merges()
        {
            var context = NewContext();
            var root = new Node(context);
            Node child = null!;
            Commit(context, () => child = new Node(context, "child"));

            using (context.UseMergeKey("run"))
            {
                Commit(context, () => root.Value = 1);
                // A transaction that changes only a collection merges under the scope like any other.
                Commit(context, () => root.Children.Add(child));
            }

            Assert.Equal(2, context.RecordedTransactions.Count);
            Assert.Equal(2, context.CurrentItem!.CommitCount);
            context.Rollback();
            Assert.Equal((0, ""), (root.Value, Names(root.Children)));
            context.RollForward();
            Assert.Equal((1, "child"), (root.Value, Names(root.Children)));
        }

        [Fact]
        public void AmbientScope_JoinsTheChainOfAnExplicitCommitMerging_AndTheOtherWayRound()
        {
            var context = NewContext();
            var node = new Node(context);

            // A facade call commits explicitly; a tool call inside the same run commits plainly under the scope.
            CommitMerging(context, "run", () => node.Value = 1);
            using (context.UseMergeKey("run"))
                Commit(context, () => node.Value = 2);
            Assert.Equal(PhoneWaveCommitOutcome.Merged, CommitMerging(context, "run", () => node.Value = 3));

            Assert.Single(context.RecordedTransactions);
            Assert.Equal(3, context.CurrentItem!.CommitCount);
        }

        [Fact]
        public void AmbientScope_AnExplicitCommitMergingKeepsItsOwnArguments()
        {
            var context = NewContext();
            var node = new Node(context);

            using (context.UseMergeKey("run", (_, _) => "ambient tag"))
            {
                Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "other", () => node.Value = 1, tag: "own"));
                Assert.Equal("other", context.CurrentItem!.MergeKey);
                // An explicit call applies no tag function to a new entry.
                Assert.Equal("own", context.CurrentItem.Tag);
            }
        }

        [Fact]
        public void AmbientScope_AnEmptyCommitRecordsNothing()
        {
            var context = NewContext();
            var node = new Node(context);

            using (context.UseMergeKey("run"))
            {
                Commit(context, () => { });
                Assert.Empty(context.RecordedTransactions);
                Commit(context, () => node.Value = 1);
                var entry = context.CurrentItem;
                Commit(context, () => { });
                Assert.Same(entry, context.CurrentItem);
            }

            Assert.Single(context.RecordedTransactions);
        }

        [Fact]
        public void OutsideTheScope_CommitIsUnchanged()
        {
            var context = NewContext();
            var node = new Node(context);

            Commit(context, () => node.Value = 1);
            using (context.UseMergeKey("run"))
                Commit(context, () => node.Value = 2);
            Assert.Null(context.AmbientMergeKey);
            Commit(context, () => node.Value = 3);
            // A plain commit outside a scope still records an empty entry.
            Commit(context, () => { });

            Assert.Equal(4, context.RecordedTransactions.Count);
            Assert.Null(context.RecordedTransactions[0].MergeKey);
            Assert.Equal("run", context.RecordedTransactions[1].MergeKey);
            Assert.Null(context.RecordedTransactions[2].MergeKey);
            Assert.Null(context.RecordedTransactions[3].MergeKey);
        }

        [Fact]
        public void UseMergeKey_DoesNotNest()
        {
            var context = NewContext();

            var scope = context.UseMergeKey("run");
            Assert.Throws<InvalidOperationException>(() => context.UseMergeKey("run"));
            Assert.Throws<InvalidOperationException>(() => context.UseMergeKey("other"));
            // The refused call did not change the open scope.
            Assert.Equal("run", context.AmbientMergeKey);

            scope.Dispose();
            Assert.Null(context.AmbientMergeKey);
            // A second dispose is harmless and does not end a later scope.
            using (context.UseMergeKey("next"))
            {
                scope.Dispose();
                Assert.Equal("next", context.AmbientMergeKey);
            }
            Assert.Null(context.AmbientMergeKey);
        }

        [Fact]
        public void UseMergeKey_RefusesANullKey()
        {
            var context = NewContext();
            Assert.Throws<ArgumentNullException>(() => context.UseMergeKey(null!));
            Assert.Null(context.AmbientMergeKey);
        }

        [Fact]
        public void AmbientScope_ACommitThatStartsAnEntry_TakesTheTagOfTheTagFunction()
        {
            var context = NewContext();
            var node = new Node(context);
            var seen = new List<(object?, object?)>();

            using (context.UseMergeKey("run", (previous, current) =>
                   {
                       seen.Add((previous, current));
                       return "AI Changes";
                   }))
            {
                Commit(context, () => node.Value = 1, tag: "tool 1");
                Assert.Equal("AI Changes", context.CurrentItem!.Tag);
                Commit(context, () => node.Value = 2, tag: "tool 2");
            }

            Assert.Equal(new[] { ((object?)null, (object?)"tool 1"), ("AI Changes", "tool 2") }, seen);
            Assert.Equal("AI Changes", context.CurrentItem!.Tag);
            Assert.Single(context.RecordedTransactions);
        }

        [Fact]
        public void AmbientScope_WithoutATagFunction_ANewEntryKeepsItsOwnTag()
        {
            var context = NewContext();
            var node = new Node(context);

            using (context.UseMergeKey("run"))
            {
                Commit(context, () => node.Value = 1, tag: "tool 1");
                Commit(context, () => node.Value = 2, tag: "tool 2");
            }

            Assert.Equal("tool 1", context.CurrentItem!.Tag);
        }

        [Fact]
        public void AmbientScope_AThrowingTagFunctionForANewEntry_LeavesTheTransactionUncommitted()
        {
            var context = NewContext();
            var node = new Node(context);

            using (context.UseMergeKey("run", (_, _) => throw new InvalidOperationException("tag")))
                Assert.Throws<InvalidOperationException>(() => Commit(context, () => node.Value = 1));

            // The using block of the helper rolled the transaction back; nothing was recorded.
            Assert.Equal(0, node.Value);
            Assert.Empty(context.RecordedTransactions);
            Assert.False(context.HasActiveTransaction);
        }

        [Fact]
        public void AmbientScope_UndoAndRedoReplayTheMergedCommitsInOrder()
        {
            var context = NewContext();
            var log = new List<string>();
            var root = new Node(context);
            var sharedKey = new object();

            using (context.UseMergeKey("run"))
            {
                Commit(context, () =>
                {
                    root.Value = 1;
                    root.Children.Add(new Node(context, "a"));
                    context.AddChange(sharedKey, new LogChange(log, "first"));
                });
                Commit(context, () =>
                {
                    root.Value = 2;
                    root.Children.Insert(0, new Node(context, "b"));
                    context.AddChange(sharedKey, new LogChange(log, "second"));
                });
            }

            context.Rollback();
            Assert.Equal((0, ""), (root.Value, Names(root.Children)));
            Assert.Equal(new[] { "undo second", "undo first" }, log);

            log.Clear();
            context.RollForward();
            Assert.Equal((2, "b,a"), (root.Value, Names(root.Children)));
            Assert.Equal(new[] { "redo first", "redo second" }, log);

            // After the undo and redo the chain is closed: the next scoped commit is a new entry.
            using (context.UseMergeKey("run"))
                Commit(context, () => root.Value = 3);
            Assert.Equal(2, context.RecordedTransactions.Count);
        }

        // ---- EndMerge ----------------------------------------------------------------------------------------

        [Fact]
        public void EndMerge_BreaksTheChain()
        {
            var context = NewContext();
            var node = new Node(context);

            CommitMerging(context, "run", () => node.Value = 1);
            context.EndMerge();
            Assert.Equal(PhoneWaveCommitOutcome.Recorded, CommitMerging(context, "run", () => node.Value = 2));

            using (context.UseMergeKey("run"))
            {
                Commit(context, () => node.Value = 3);
                context.EndMerge();
                Commit(context, () => node.Value = 4);
            }

            Assert.Equal(3, context.RecordedTransactions.Count);
            // The history itself did not change: every entry still undoes.
            context.Rollback();
            Assert.Equal(3, node.Value);
            context.Rollback();
            Assert.Equal(1, node.Value);
        }

        [Fact]
        public void EndMerge_WithNoChain_DoesNothing()
        {
            var context = NewContext();
            var properties = new List<string?>();
            context.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

            context.EndMerge();

            Assert.Empty(properties);
            Assert.Empty(context.RecordedTransactions);
        }

        // ---- CanMerge ----------------------------------------------------------------------------------------

        [Fact]
        public void CanMerge_IsTrueOnlyForTheOpenChainOfTheKey()
        {
            var context = NewContext();
            var node = new Node(context);

            Assert.False(context.CanMerge("run"));

            CommitMerging(context, "run", () => node.Value = 1);
            Assert.True(context.CanMerge("run"));
            Assert.True(context.CanMerge(new string(new[] { 'r', 'u', 'n' })));
            Assert.False(context.CanMerge("other"));

            // A rolled-back transaction does not end the chain.
            using (context.BeginTransaction())
                node.Value = 100;
            Assert.True(context.CanMerge("run"));

            Commit(context, () => node.Value = 2);
            Assert.False(context.CanMerge("run"));

            CommitMerging(context, "run", () => node.Value = 3);
            context.EndMerge();
            Assert.False(context.CanMerge("run"));

            CommitMerging(context, "run", () => node.Value = 4);
            context.Rollback();
            Assert.False(context.CanMerge("run"));
        }

        [Fact]
        public void CanMerge_HasNoSideEffect()
        {
            var context = NewContext();
            var node = new Node(context);
            CommitMerging(context, "run", () => node.Value = 1);
            var entry = context.CurrentItem;
            var history = ObserveHistory(context);
            var properties = new List<string?>();
            context.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

            Assert.True(context.CanMerge("run"));
            Assert.False(context.CanMerge("other"));

            Assert.Empty(history);
            Assert.Empty(properties);
            Assert.Same(entry, context.CurrentItem);
            // A failed probe with another key did not end the chain.
            Assert.Equal(PhoneWaveCommitOutcome.Merged, CommitMerging(context, "run", () => node.Value = 2));
        }

        // ---- CommitCount -------------------------------------------------------------------------------------

        [Fact]
        public void CommitCount_CountsTheCommitsOfAnEntry()
        {
            var context = NewContext();
            var log = new List<string>();
            var node = new Node(context);
            var key = new object();

            Commit(context, () => node.Value = 1);
            Assert.Equal(1, context.CurrentItem!.CommitCount);

            CommitMerging(context, "run", () => context.AddChange(key, new AbsorbingLogChange(log, "first")));
            Assert.Equal(1, context.CurrentItem!.CommitCount);
            // A commit whose changes are all absorbed still counts.
            CommitMerging(context, "run", () => context.AddChange(key, new AbsorbingLogChange(log, "second")));
            CommitMerging(context, "run", () => node.Value = 2);
            Assert.Equal(3, context.CurrentItem!.CommitCount);

            // An empty commit records nothing, so it does not count.
            CommitMerging(context, "run", () => { });
            Assert.Equal(3, context.CurrentItem!.CommitCount);
            Assert.Equal(1, context.RecordedTransactions[0].CommitCount);
        }

        // ---- HasActiveTransaction ----------------------------------------------------------------------------

        [Fact]
        public void HasActiveTransaction_RisesOnBegin_AndFallsOnCommit()
        {
            var context = NewContext();
            var node = new Node(context);
            var values = ObserveActiveTransaction(context);

            Assert.False(context.HasActiveTransaction);
            var transaction = context.BeginTransaction();
            Assert.True(context.HasActiveTransaction);
            node.Value = 1;
            transaction.Commit();
            Assert.False(context.HasActiveTransaction);
            // Disposing the committed transaction changes nothing.
            transaction.Dispose();

            Assert.Equal(new[] { true, false }, values);
        }

        [Fact]
        public void HasActiveTransaction_FallsOnEveryKindOfEnd()
        {
            var context = NewContext();
            var node = new Node(context);
            var values = ObserveActiveTransaction(context);

            CommitMerging(context, "run", () => node.Value = 1);
            CommitMerging(context, "run", () => node.Value = 2);
            CommitMerging(context, "run", () => { });
            using (context.BeginTransaction())
                node.Value = 3;
            var rolledBack = context.BeginTransaction();
            rolledBack.Rollback();
            using (context.UseMergeKey("run"))
                Commit(context, () => node.Value = 4);

            Assert.Equal(new[] { true, false, true, false, true, false, true, false, true, false, true, false }, values);
        }

        [Fact]
        public void HasActiveTransaction_DoesNotChangeOnUndoOrRedo_OrOnARejectedCollectionEdit()
        {
            var context = NewContext();
            var node = new Node(context);
            Commit(context, () => node.Value = 1);
            var values = ObserveActiveTransaction(context);

            context.Rollback();
            context.RollForward();
            Assert.Empty(values);

            // A collection edit outside a transaction is refused; it never opens a transaction of its own.
            Assert.Throws<InvalidOperationException>(() => node.Children.Add(new Node(context)));
            Assert.Empty(values);

            Commit(context, () => node.Children.Add(new Node(context)));
            Assert.Equal(new[] { true, false }, values);
        }

        [Fact]
        public void HasActiveTransaction_AfterAnAbort_FallsAfterTheReplayCompleted()
        {
            var context = NewContext();
            var node = new Node(context);
            var events = new List<string>();
            context.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PhoneWaveContext.IsRolling))
                    events.Add("IsRolling=" + context.IsRolling);
                else if (e.PropertyName == nameof(PhoneWaveContext.HasActiveTransaction))
                    events.Add("HasActiveTransaction=" + context.HasActiveTransaction);
            };

            using (context.BeginTransaction())
                node.Value = 1;

            Assert.Equal(new[]
            {
                "HasActiveTransaction=True",
                "IsRolling=True",
                "IsRolling=False",
                "HasActiveTransaction=False",
            }, events);
        }

        [Fact]
        public void HasActiveTransaction_AnObserverOfTheEnd_CanBeginANewTransaction()
        {
            var context = NewContext();
            var node = new Node(context);
            var armed = 0;
            context.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(PhoneWaveContext.HasActiveTransaction) || context.HasActiveTransaction || armed == 0)
                    return;
                armed--;
                using var next = context.BeginTransaction("observer");
                node.Name = "observer " + armed;
                next.Commit();
            };

            armed = 1;
            Commit(context, () => node.Value = 1);
            Assert.Equal("observer 0", node.Name);

            armed = 1;
            using (context.BeginTransaction())
                node.Value = 2;
            Assert.Equal((1, (string?)"observer 0"), (node.Value, node.Name));
            Assert.Equal(3, context.RecordedTransactions.Count);
            Assert.False(context.HasActiveTransaction);
        }

        [Fact]
        public void HasActiveTransaction_AThrowingObserverOnBegin_LeavesTheContextFree()
        {
            var context = NewContext();
            var node = new Node(context);
            var probe = new InvalidOperationException("observer");
            void Throwing(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(PhoneWaveContext.HasActiveTransaction) && context.HasActiveTransaction)
                    throw probe;
            }
            var values = ObserveActiveTransaction(context);
            context.PropertyChanged += Throwing;

            var thrown = Assert.Throws<InvalidOperationException>(() => context.BeginTransaction());
            Assert.Same(probe, thrown);
            Assert.False(context.HasActiveTransaction);
            Assert.Equal(new[] { true, false }, values);

            context.PropertyChanged -= Throwing;
            Commit(context, () => node.Value = 1);
            Assert.Single(context.RecordedTransactions);
        }

        [Fact]
        public void HasActiveTransaction_FallsOnSuspend()
        {
            var context = NewContext();
            var values = ObserveActiveTransaction(context);

            context.BeginTransaction();
            context.Suspend();

            Assert.False(context.HasActiveTransaction);
            Assert.Equal(new[] { true, false }, values);
        }

        // ---- TryRetagMerge -----------------------------------------------------------------------------------

        [Fact]
        public void TryRetagMerge_ReplacesTheTagOfTheOpenChain()
        {
            var context = NewContext();
            var node = new Node(context);
            Commit(context, () => node.Name = "before");
            CommitMerging(context, "run", () => node.Value = 1, tag: "AI Changes");
            CommitMerging(context, "run", () => node.Value = 2);
            var previous = context.CurrentItem!;
            var history = ObserveHistory(context);
            var properties = new List<string?>();
            context.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

            Assert.True(context.TryRetagMerge("run", "Rescale"));

            var replace = Assert.Single(history);
            Assert.Equal(NotifyCollectionChangedAction.Replace, replace.Action);
            Assert.Equal(1, replace.NewStartingIndex);
            Assert.Same(previous, replace.OldItems![0]);
            Assert.Same(context.CurrentItem, replace.NewItems![0]);
            Assert.Equal(new[] { nameof(PhoneWaveContext.CurrentItem) }, properties);
            Assert.Equal(1, context.CurrentIndex);
            Assert.Equal("Rescale", context.CurrentItem!.Tag);
            Assert.Equal("run", context.CurrentItem.MergeKey);
            Assert.Equal(2, context.CurrentItem.CommitCount);
            Assert.Throws<InvalidOperationException>(() => previous.RollBack());

            // The chain stays open, and the replay did not change.
            Assert.True(context.CanMerge("run"));
            Assert.Equal(PhoneWaveCommitOutcome.Merged, CommitMerging(context, "run", () => node.Value = 3));
            Assert.Equal("Rescale", context.CurrentItem!.Tag);
            context.Rollback();
            Assert.Equal((0, (string?)"before"), (node.Value, node.Name));
            context.RollForward();
            Assert.Equal(3, node.Value);
        }

        [Fact]
        public void TryRetagMerge_ReturnsFalseWhenTheChainIsGone()
        {
            var context = NewContext();
            var node = new Node(context);
            var history = ObserveHistory(context);

            // No entry.
            Assert.False(context.TryRetagMerge("run", "title"));

            CommitMerging(context, "run", () => node.Value = 1, tag: "AI Changes");
            history.Clear();
            // Another key.
            Assert.False(context.TryRetagMerge("other", "title"));

            // A user edit between.
            Commit(context, () => node.Name = "user edit");
            history.Clear();
            Assert.False(context.TryRetagMerge("run", "title"));

            // An ended chain.
            CommitMerging(context, "run", () => node.Value = 2, tag: "AI Changes");
            context.EndMerge();
            history.Clear();
            Assert.False(context.TryRetagMerge("run", "title"));

            // An undo.
            CommitMerging(context, "run", () => node.Value = 3, tag: "AI Changes");
            context.Rollback();
            history.Clear();
            Assert.False(context.TryRetagMerge("run", "title"));

            Assert.Empty(history);
            foreach (var entry in context.RecordedTransactions)
                Assert.NotEqual("title", entry.Tag);
        }

        [Fact]
        public void TryRetagMerge_WorksOnAnEntryThatTheAmbientScopeStarted()
        {
            var context = NewContext();
            var node = new Node(context);
            var title = "AI Changes";

            using (context.UseMergeKey("run", (_, _) => title))
            {
                Commit(context, () => node.Value = 1, tag: "tool");
                title = "Rescale";
                Assert.True(context.TryRetagMerge("run", title));
                Commit(context, () => node.Value = 2, tag: "tool");
            }

            Assert.Single(context.RecordedTransactions);
            Assert.Equal("Rescale", context.CurrentItem!.Tag);
        }
    }
}
