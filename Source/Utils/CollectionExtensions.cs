using System.Collections.Generic;

namespace PreciseSavestates.Utils;

public static class CollectionExtensions {
    public static void AddRange<T>(this HashSet<T> hashSet, params T[] items) {
        foreach (var item in items) {
            hashSet.Add(item);
        }
    }
}
