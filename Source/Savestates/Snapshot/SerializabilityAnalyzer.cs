using System;
using System.Collections.Generic;
using System.Reflection;

namespace PreciseSavestates.Savestates;

// Classes are deserialized in place, updating the current object. That way, partial snapshots are supported
// and leave the unsnapshotted values in place.
// In collections however, you cannot in-place deserialize, since the original entries are replaced entirely.
internal static class SerializabilityAnalyzer {
    // True if reconstructing `type` from scratch would lose content, since the type contains unserializable values
    public static bool HasUnserializableContent(
        Type type,
        Func<Type, bool> isIgnored,
        Func<Type, bool> isLeaf,
        Func<Type, IEnumerable<MemberInfo>> getMembers
    ) {
        return Walk(type, isIgnored, isLeaf, getMembers, []);
    }

    private static bool Walk(
        Type type,
        Func<Type, bool> isIgnored,
        Func<Type, bool> isLeaf,
        Func<Type, IEnumerable<MemberInfo>> getMembers,
        HashSet<Type> visited
    ) {
        if (isLeaf(type)) {
            return false;
        }

        // A non-leaf interface / abstract type can't be reconstructed in a collection
        if (type.IsInterface || type.IsAbstract) {
            return true;
        }

        // cycle guard: a type graph can reference itself (A holds List<B>, B holds List<A>).
        if (!visited.Add(type)) {
            return false;
        }

        foreach (var m in getMembers(type)) {
            var memberType = m is FieldInfo fi ? fi.FieldType : ((PropertyInfo)m).PropertyType;
            var inner = ElementType(memberType);

            if (isIgnored(memberType) || isIgnored(inner)) {
                return true;
            }

            if (Walk(inner, isIgnored, isLeaf, getMembers, visited)) {
                return true;
            }
        }

        return false;
    }

    // Extracts item type from array / List<> / HashSet<> / Dictionary<,>-value wrappers, repeatedly
    // (e.g. List<List<T>> -> T).
    private static Type ElementType(Type type) {
        while (true) {
            if (type.IsArray) {
                type = type.GetElementType()!;
                continue;
            }

            if (type.IsGenericType) {
                var def = type.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(HashSet<>)) {
                    type = type.GetGenericArguments()[0];
                    continue;
                }

                if (def == typeof(Dictionary<,>)) {
                    type = type.GetGenericArguments()[1];
                    continue;
                }
            }

            return type;
        }
    }
}
