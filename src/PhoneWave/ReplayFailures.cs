using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace PhoneWave;

/// <summary>
/// The ONE best-effort replay loop and the ONE failure fold, shared by every replay path: a recorded
/// transaction's undo/redo, an uncommitted transaction's abort, the tracked collection and dictionary
/// aggregators, and the context's IsRolling edge notifications. Every item is attempted EXACTLY
/// ONCE — a failing item is never retried, and a failure never strands the items after it. The
/// failures are retained chronologically and surface only after the whole loop ran.
/// </summary>
internal static class ReplayFailures
{
    /// <summary>Applies <paramref name="apply"/> to every item exactly once and appends each failure,
    /// in order, to <paramref name="failures"/>. No list is allocated on the clean path.</summary>
    /// <param name="items">The items to apply, in replay order. Pass a snapshot when an observer
    /// could otherwise change the source while it is enumerated.</param>
    /// <param name="apply">The per-item replay step.</param>
    /// <param name="failures">The chronological failure list; created on the first failure.</param>
    public static void ApplyEach<T>(IEnumerable<T> items, Action<T> apply, ref List<Exception>? failures)
    {
        foreach (var item in items)
        {
            try
            {
                apply(item);
            }
            catch (Exception failure)
            {
                // A throwing notification observer is precisely the condition this loop exists to
                // survive: keep the original failure and continue with the next item.
                (failures ??= []).Add(failure);
            }
        }
    }

    /// <summary>Applies every item once (see <see cref="ApplyEach{T}"/>), then surfaces the collected
    /// failures through <see cref="Combine"/>.</summary>
    /// <param name="items">The items to apply, in replay order.</param>
    /// <param name="apply">The per-item replay step.</param>
    /// <param name="message">The AggregateException message used when more than one item failed.</param>
    public static void ApplyEachThenThrow<T>(IEnumerable<T> items, Action<T> apply, string message)
    {
        List<Exception>? failures = null;
        ApplyEach(items, apply, ref failures);
        Combine(failures, message)?.Throw();
    }

    /// <summary>Folds a chronological failure list into one exception: null for zero failures; a
    /// single failure keeps its original instance/stack; multiple failures become one chronological
    /// AggregateException.</summary>
    /// <param name="failures">The chronological failure list, or null.</param>
    /// <param name="message">The AggregateException message used when more than one item failed.</param>
    public static ExceptionDispatchInfo? Combine(List<Exception>? failures, string message)
    {
        if (failures is not { Count: > 0 }) return null;
        return failures.Count == 1
            ? ExceptionDispatchInfo.Capture(failures[0])
            : ExceptionDispatchInfo.Capture(new AggregateException(message, failures));
    }
}
