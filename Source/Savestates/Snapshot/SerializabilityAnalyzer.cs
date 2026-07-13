using System;
using System.Collections.Generic;
using System.Reflection;

namespace PreciseSavestates.Savestates;

// Pure, engine-agnostic graph walk used by CustomizableContractResolver to decide whether a *collection member*
// can be round-tripped. It has no Unity/Newtonsoft/BepInEx dependency (the engine-specific decisions are injected as
// delegates) so it can be unit-tested by linking this source into a plain test project.
//
// Why the walk goes all the way down: a collection deserializes by *replacing* its elements with fresh instances
// (ObjectCreationHandling.Replace), so the whole element subtree is rebuilt from JSON. If anything reachable from an
// element is dropped during serialization (an ignored type), the reconstructed element comes back partial — a null
// where the live object had a value. The in-place-populate escape hatch that keeps a top-level component safe does
// NOT apply once you cross a collection: from there down every member, plain-object members included, must round-trip.
// Concretely: HealthManager.itemDropGroups is List<ItemDropGroup>; ItemDropGroup holds Drops (List<ItemDropProbability>)
// whose element has a Unity Object ref. A shallow (one-level) check judged ItemDropGroup serializable, so the list was
// written but each element came back with Drops == null -> ClearItemDropsBattleScene() NRE. Recursing catches it.
internal static class SerializabilityAnalyzer {
    // True if reconstructing `type` from scratch would lose content, i.e. some member reachable through it (following
    // both collection element types and plain-object members) is an ignored/unserializable type.
    //   isIgnored     — the resolver's IgnorePropertyType (a member typed like this is dropped on serialize).
    //   isLeaf        — types with no capturable sub-structure to lose: primitives/enum/string, and Unity Object /
    //                   Component refs (those are ref-handled or ignored at the member level, not rebuilt by value).
    //   getMembers    — the resolver's GetSerializableMembers (honours allow/denylist, so a deliberately-denylisted
    //                   member does not count as lost content).
    public static bool HasUnserializableContent(
        Type type,
        Func<Type, bool> isIgnored,
        Func<Type, bool> isLeaf,
        Func<Type, IEnumerable<MemberInfo>> getMembers
    ) {
        return Walk(type, isIgnored, isLeaf, getMembers, new HashSet<Type>());
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

    // Peels array / List<> / HashSet<> / Dictionary<,>-value wrappers down to the contained element type, repeatedly
    // (e.g. List<List<T>> -> T). A non-collection type is returned unchanged.
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
