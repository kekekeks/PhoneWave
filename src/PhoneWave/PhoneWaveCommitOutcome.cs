namespace PhoneWave;

/// <summary>What <see cref="PhoneWaveTransaction.CommitMerging"/> did with the transaction.</summary>
public enum PhoneWaveCommitOutcome
{
    /// <summary>The transaction was recorded as a new entry at the top of the undo history.</summary>
    Recorded,

    /// <summary>
    /// The transaction was merged into the previous entry: the history holds ONE entry for both, and
    /// that entry replaced the previous one at the same position.
    /// </summary>
    Merged,

    /// <summary>
    /// Nothing was recorded and the history did not change: the transaction had no changes, or it was
    /// already committed or rolled back.
    /// </summary>
    NothingRecorded,
}
