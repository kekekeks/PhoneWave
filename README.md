# PhoneWave

This is a transaction-based changes tracker for your models.

# How to use

For simplest case of tracking property values, see the example project.

TL;DR:

Declaring your model classes:
```cs 
[TrackProperty(typeof(MyType), "MyTypePropertyName")]
public partial class MyModel : PhoneWaveBase
{ 
    public MyModel(PhoneWaveContext context) : base(context) {}
}
```
Making changes to your model:
```cs
using (var tran = myModel.Context.BeginTransaction("Changed MyTypePropertyName"))
{
    myModel.MyTypePropertyName = myNewValue;
    tran.Commit();
}
```
Actually undoing/redoing stuff:
```cs
if (myModel.Context.CanRollback)
    myModel.Context.Rollback();
    
if (myModel.Context.CanRollForward)
    myModel.Context.RollForward();
```

Whenever you're doing something wrong, you're getting an exception.

## Merging transactions into one undo step

Use `CommitMerging` when a series of separate transactions must undo as ONE step (for example, all the
calls that one script run makes). Give the same merge key to each transaction of the series:

```cs
using (var tran = myModel.Context.BeginTransaction("Script: rescale"))
{
    myModel.MyTypePropertyName = myNewValue;
    var outcome = tran.CommitMerging(scriptRunId);   // Recorded, Merged or NothingRecorded
}
```

`CommitMerging(mergeKey, mergeTags = null)` merges the transaction into the previous undo entry when ALL
of these are true:

- `mergeKey` is not null, and it is equal (`object.Equals`) to the `MergeKey` of the previous entry.
- The previous entry is the current AND the newest entry of the history.
- Nothing happened to the history after that entry was recorded or merged: no other commit, no undo, no
  redo, no `Suspend`. (An undo followed by a redo also stops the merge.)

Otherwise the transaction is recorded as a new entry that carries `mergeKey`. A later call with the same
key can then merge into it. A plain `Commit()` records an entry with no key, so nothing merges into it.

Rules:

- **Undo and redo.** One undo of the merged entry restores the state from before the FIRST transaction.
  One redo restores the state after the LAST transaction. Each undo or redo is one replay: `IsRolling`
  rises once and falls once.
- **Property changes.** The change keeps the FIRST old value and the LAST new value of the property.
  One undo raises one `PropertyChanged` for the property. A change whose values end up equal stays in the
  entry (a single transaction keeps such a change too).
- **Collection changes** (`PhoneWaveCollection`). The inserts and removes of the later transaction are
  added after the earlier ones in one sequence. Redo replays the sequence in order; undo replays it in
  reverse. A move or a replace is a remove plus an insert, as always. An object that one transaction
  creates and adds, and a later transaction removes, is absent after the undo and after the redo.
- **Dictionary changes** (`PhoneWaveDictionary`). For each key, the merged change keeps the old side
  (present with a value, or absent) of the first change and the new side of the last change.
- **Other changes** (your own `TransactionChange` subclasses, added with `PhoneWaveContext.AddChange`).
  By default they are NOT combined. The later change stays separate: undo replays the later transaction's
  changes first, redo replays the earlier transaction's changes first. To combine your change with a
  later change under the same key, override `TryAbsorb(TransactionChange later)` and return `true`. Then
  your `Rollback` must restore the state before your change and your `RollForward` the state after the
  later change. If `TryAbsorb` throws, the later change is kept separate, the merge completes, and then
  the exception is thrown.
- **Replay order.** A combined change replays at the position of the earlier change. The final state is
  the same as for the separate entries; the order of notifications between independent objects can be
  different.
- **Empty transaction.** A transaction with no changes records nothing and the history does not change
  (`NothingRecorded`). It does not stop a later merge. (A plain `Commit()` still records an empty entry.)
- **Rolled-back transaction.** A transaction that you dispose without a commit changes neither the state
  nor the history, so it does not stop a later merge.
- **Tag.** The merged entry keeps the tag of the previous entry. To set a different tag, give
  `mergeTags`: it receives (previous entry's tag, this transaction's tag) and returns the merged tag. It
  runs before anything is committed: if it throws, the transaction stays active and uncommitted.
- **History notifications.** A merge replaces the previous entry in `RecordedTransactions` with a new
  entry object (a `Replace` notification) and raises `PropertyChanged` for `CurrentItem`. `CurrentIndex`
  does not change, and the merged entry counts as one entry. The previous entry object is retired: its
  `RollBack()` and `RollForward()` throw.
- **Redo side.** A merge happens only when there is no redo side (the previous entry must be the newest
  one). A new entry drops the redo side, as every commit does.
- **Threads.** Same as `Commit()`: PhoneWave does no thread checks. Use one context from one thread.

Also check out dictionary and collection tracking classes, you might find these useful too.
You will need read-only properties for these, constructed under the parent object's `PhoneWaveContext`.

GLHF.
