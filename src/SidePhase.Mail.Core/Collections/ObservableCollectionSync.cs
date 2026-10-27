using System.Collections.ObjectModel;

namespace SidePhase.Mail.Core.Collections;

public static class ObservableCollectionSync
{
    /// <summary>
    /// Updates <paramref name="target"/> in place so it matches <paramref name="desired"/>, using the smallest
    /// practical number of Remove/Move/Insert operations. Keeps list selection and scroll position stable in the UI.
    /// Items are compared by reference.
    /// </summary>
    public static void SyncTo<T>(this ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);

        var keep = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;

            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], item))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, item);
        }

        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }
}
