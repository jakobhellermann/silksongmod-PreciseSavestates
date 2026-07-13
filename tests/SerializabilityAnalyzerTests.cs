using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PreciseSavestates.Savestates;

namespace PreciseSavestates.Tests;

// Tests for the pure graph walk that decides whether a collection member can be round-tripped.
// The engine-specific decisions the resolver injects are modelled here with test-local types:
//   - UnityObjRef  stands in for a Unity Object / ScriptableObject field (ignored on serialize).
//   - IRefLike     stands in for a Component ref (a leaf: ref-handled, not rebuilt by value).
// getMembers mirrors the resolver's field harvesting with plain reflection over public instance fields.
public class SerializabilityAnalyzerTests {
    private sealed class UnityObjRef {
        public int whatever;
    }

    private interface IRefLike { }

    private static bool IsIgnored(Type t) => typeof(UnityObjRef).IsAssignableFrom(t);

    private static bool IsLeaf(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || typeof(IRefLike).IsAssignableFrom(t);

    private static IEnumerable<MemberInfo> GetMembers(Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Instance);

    private static bool Check(Type t) =>
        SerializabilityAnalyzer.HasUnserializableContent(t, IsIgnored, IsLeaf, GetMembers);

    // ---- clean types round-trip: not flagged ----

    private sealed class AllPrimitives {
        public int a;
        public float b;
        public string s = "";
        public bool flag;
    }

    [Fact]
    public void Primitives_only_is_reconstructable() {
        Assert.False(Check(typeof(AllPrimitives)));
    }

    [Fact]
    public void Primitive_element_collection_is_reconstructable() {
        // List<int> / List<AllPrimitives> both round-trip.
        Assert.False(Check(typeof(List<int>).GetGenericArguments()[0])); // int
        Assert.False(Check(typeof(AllPrimitives)));
    }

    // ---- direct: element type holds an ignored ref (this already worked before the fix) ----

    private sealed class DirectLeaf {
        public int x;
        public UnityObjRef item = new();
    }

    [Fact]
    public void Direct_ignored_member_is_flagged() {
        Assert.True(Check(typeof(DirectLeaf)));
    }

    // ---- the itemDropGroups bug: ignored ref is nested inside the element's OWN collection ----
    // ItemDropProbability(item) -> ItemDropGroup(Drops = List<ItemDropProbability>). The shallow (one-level) check
    // judged ItemDropGroup reconstructable, so the list was written but each element came back with Drops == null.

    private sealed class Inner {
        public UnityObjRef item = new();
    }

    private sealed class Group {
        public float totalProbability;
        public List<Inner> drops = new();
    }

    [Fact]
    public void Transitive_through_nested_collection_is_flagged() {
        Assert.True(Check(typeof(Group)));
    }

    // ---- the refinement: once inside a collection element, plain-object members must round-trip too ----
    // "collections-only" recursion would MISS this (Inner is reached via a plain-object field, not a list), yet a
    // fresh-reconstructed Wrapper still comes back with a partial Inner.

    private sealed class Wrapper {
        public int n;
        public Inner inner = new();
    }

    [Fact]
    public void Transitive_through_plain_object_member_is_flagged() {
        Assert.True(Check(typeof(Wrapper)));
    }

    // ---- other collection shapes peel correctly ----

    private sealed class WithArray {
        public Inner[] items = Array.Empty<Inner>();
    }

    private sealed class WithHashSet {
        public HashSet<Inner> items = new();
    }

    private sealed class WithDictValue {
        public Dictionary<string, Inner> map = new();
    }

    private sealed class WithNestedList {
        public List<List<Inner>> grid = new();
    }

    [Fact]
    public void Array_element_is_flagged() => Assert.True(Check(typeof(WithArray)));

    [Fact]
    public void HashSet_element_is_flagged() => Assert.True(Check(typeof(WithHashSet)));

    [Fact]
    public void Dictionary_value_is_flagged() => Assert.True(Check(typeof(WithDictValue)));

    [Fact]
    public void Nested_list_element_is_flagged() => Assert.True(Check(typeof(WithNestedList)));

    // ---- leaf types stop the walk: a ref-like element is not descended into ----
    // Component refs are handled by RefConverter, so a collection of them is NOT skipped even though the ref type
    // "contains" an ignored member by reflection.

    private sealed class RefLeaf : IRefLike {
        public UnityObjRef hidden = new();
    }

    private sealed class HoldsRefLeaves {
        public List<RefLeaf> refs = new();
    }

    [Fact]
    public void Ref_like_leaf_element_is_not_flagged() {
        Assert.True(IsLeaf(typeof(RefLeaf)));
        Assert.False(Check(typeof(HoldsRefLeaves)));
    }

    // ---- interface / abstract element types can't be reconstructed (no concrete type to instantiate) ----
    // e.g. BattleScene.initialisables is an IInitialisable[]; a rebuilt element has no concrete type to construct.

    private interface INonRef { }

    private abstract class AbstractBase {
        public int x;
    }

    private sealed class HoldsInterface {
        public List<INonRef> items = new();
    }

    private sealed class HoldsAbstract {
        public AbstractBase[] items = Array.Empty<AbstractBase>();
    }

    private sealed class HoldsRefInterface {
        public List<IRefLike> refs = new();
    }

    [Fact]
    public void Interface_element_is_flagged() => Assert.True(Check(typeof(HoldsInterface)));

    [Fact]
    public void Abstract_element_is_flagged() => Assert.True(Check(typeof(HoldsAbstract)));

    [Fact]
    public void Ref_like_interface_element_is_not_flagged() {
        // a leaf wins over the interface/abstract check: ref-handled interfaces aren't rebuilt by value
        Assert.True(IsLeaf(typeof(IRefLike)));
        Assert.False(Check(typeof(HoldsRefInterface)));
    }

    // ---- cycle guard: self-referential graphs terminate ----

    private sealed class Ring {
        public int v;
        public Ring? next;
    }

    private sealed class CycA {
        public List<CycB> bs = new();
    }

    private sealed class CycB {
        public UnityObjRef item = new();
        public List<CycA> as_ = new();
    }

    [Fact]
    public void Clean_cycle_terminates_and_is_reconstructable() {
        Assert.False(Check(typeof(Ring)));
    }

    [Fact]
    public void Cycle_with_ignored_ref_terminates_and_is_flagged() {
        Assert.True(Check(typeof(CycA)));
        Assert.True(Check(typeof(CycB)));
    }
}
