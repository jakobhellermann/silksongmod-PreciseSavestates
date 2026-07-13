using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PreciseSavestates.Savestates;

[PublicAPI]
public class CustomizableContractResolver : DefaultContractResolver {
    public BindingFlags FieldBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public BindingFlags PropertyBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public Dictionary<Type, string[]> FieldAllowlist = new();
    public Dictionary<Type, string[]> FieldDenylist = new();

    // custom converter per (exact) field type — lets callers hook arbitrary capture/restore logic for types that
    // can't be round-tripped by raw field copy (e.g. tk2dSpriteAnimator: write clip name + time, restore via PlayFrom).
    // Takes precedence over the built-in Component->RefConverter handling.
    public Dictionary<Type, JsonConverter> PropertyConverters = new();

    // checks exact
    public Type[] ContainerTypesToIgnore = [];

    // checks IsAssignableFrom
    public Type[] FieldTypesToIgnore = [];
    public Type[] ExactFieldTypesToIgnore = [];

    protected override List<MemberInfo> GetSerializableMembers(Type objectType) {
        var list = new List<MemberInfo>();

        for (var ty = objectType; ty != typeof(object) && ty != null; ty = ty.BaseType) {
            var typeStem = ty.IsGenericType ? ty.GetGenericTypeDefinition() : ty;
            if (FieldAllowlist.TryGetValue(typeStem, out var allowlist)) {
                list.AddRange(allowlist
                    .Select(fieldName => {
                        var field = (MemberInfo?)ty.GetField(fieldName, FieldBindingFlags | BindingFlags.DeclaredOnly)
                                    ?? ty.GetProperty(fieldName, PropertyBindingFlags | BindingFlags.DeclaredOnly);
                        if (field == null) {
                            Log.Error($"Field '{fieldName}' in allowlist of '{ty}' does not exist!");
                        }

                        return field;
                    }).Cast<MemberInfo>());
                continue;
            }


            list.AddRange(ty.GetFields(FieldBindingFlags | BindingFlags.DeclaredOnly)
                .Where(field => field.GetCustomAttribute<CompilerGeneratedAttribute>() == null));
            // Capture properties that hold real state: a virtual getter (Unity engine-backed, e.g. Transform.position)
            // or a compiler-generated getter (an auto-property = a field with sugar, e.g.
            // HeroAnimationController.actorState). Setter visibility is irrelevant — a private set still backs real
            // state; it's restored through the private setter (see CreateProperty). Computed properties (hand-written
            // accessors that derive from other state) are neither virtual nor compiler-generated, so they're skipped.
            list.AddRange(ty
                .GetProperties(PropertyBindingFlags | BindingFlags.DeclaredOnly)
                .Where(prop => prop.CanRead && prop.CanWrite && prop.GetMethod is { } getter
                                             && (getter.IsVirtual
                                                 || getter.GetCustomAttribute<CompilerGeneratedAttribute>() != null)));
            /* || prop.GetCustomAttribute<NativePropertyAttribute>() != null TODO */
            // TODO: just removed added from this type
            if (FieldDenylist.TryGetValue(ty, out var denyList)) {
                list.RemoveAll(field => denyList.Contains(field.Name));
            }
        }

        return list;
    }

    protected override JsonContract CreateContract(Type objectType) {
        if (objectType == typeof(Transform)) {
            // Transform is IEnumerable which lets newtonsoft treat it as Array
            return base.CreateObjectContract(objectType);
        }

        return base.CreateContract(objectType);
    }

    private bool IgnorePropertyType(Type? type) {
        return Array.Exists(ExactFieldTypesToIgnore, x => x == type) ||
               Array.Exists(FieldTypesToIgnore, x => x.IsAssignableFrom(type));
    }

    // Returns true if reconstructing `type` from scratch would lose content — i.e. some member reachable through it
    // (following collection element types *and* plain-object members) is ignored during serialization (e.g. a
    // ScriptableObject or GameObject field). Used only to skip collection members, whose elements are rebuilt fresh
    // and so cannot fall back on in-place-populated live values. The recursion lives in SerializabilityAnalyzer (pure,
    // unit-tested); the engine-specific decisions are injected here.
    private bool HasUnserializableContent(Type type) {
        return SerializabilityAnalyzer.HasUnserializableContent(
            type,
            IgnorePropertyType,
            IsReconstructableLeaf,
            objectType => GetSerializableMembers(objectType));
    }

    // Types with no capturable sub-structure that could be lost: primitives/enum/string, and Unity Object / Component
    // refs (handled by RefConverter or ignored at the member level, not rebuilt by value). Note value types other than
    // the above are NOT leaves: a struct reached through a collection is rebuilt fresh too, so it must round-trip.
    private bool IsReconstructableLeaf(Type type) {
        return type.IsPrimitive || type.IsEnum || type == typeof(string)
               || typeof(Object).IsAssignableFrom(type)
               || RefType(type);
    }

    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization) {
        // default MemberSerialization ignores private fields, unless DefaultMembersSearchFlags.NonPublic ist set,
        // but that field is deprecated in favor of GetSerializableMembers.
        var property = base.CreateProperty(member, MemberSerialization.Fields);

        property.Ignored = false;

        // A property we chose to capture is real state — restore it even through a private/non-public setter
        // (Newtonsoft marks such properties read-only by default, which would silently drop them on load).
        if (member is PropertyInfo { CanWrite: true }) {
            property.Writable = true;
        }

        var shouldSerialize = true;

        var type = property.PropertyType;
        if (type == null) {
            return property;
        }

        shouldSerialize &= !IgnorePropertyType(type);

        if (type.IsArray) {
            shouldSerialize &= !IgnorePropertyType(type.GetElementType());
        }

        var itemType = type;
        // collections deserialize by replacing elements with fresh instances, so a type with ignored
        // (unserializable) members loses those refs. plain object fields are populated in-place, so they're safe.
        var isCollection = false;

        if (type.IsArray) {
            itemType = type.GetElementType()!;
            shouldSerialize &= !IgnorePropertyType(itemType);
            isCollection = true;
        } else if (type.IsGenericType) {
            if (type.GetGenericTypeDefinition() == typeof(List<>)) {
                itemType = type.GetGenericArguments()[0];
                shouldSerialize &= !IgnorePropertyType(itemType);
                isCollection = true;

                property.ObjectCreationHandling = ObjectCreationHandling.Replace;
            }

            if (type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) {
                var generics = type.GetGenericArguments();
                // TODO Dict<EffectDealer, bool>
                shouldSerialize &= generics[0].IsPrimitive || generics[0] == typeof(string);
                itemType = type.GetGenericArguments()[1];
                shouldSerialize &= !IgnorePropertyType(itemType);
                isCollection = true;
            }

            if (type.GetGenericTypeDefinition() == typeof(HashSet<>)) {
                itemType = type.GetGenericArguments()[0];
                shouldSerialize &= !IgnorePropertyType(itemType);
                isCollection = true;
            }
        }

        var isItemRefType = RefType(itemType);
        if (PropertyConverters.TryGetValue(itemType, out var customConverter)) {
            property.Converter = customConverter;
        } else if (isItemRefType) {
            property.Converter = new RefConverter(0);
        }

        if (ContainerTypesToIgnore.Contains(member.DeclaringType)) {
            shouldSerialize = false;
        }

        if (shouldSerialize && isCollection && HasUnserializableContent(itemType)) {
            shouldSerialize = false;
            Log.Warning(
                $"Auto-skipping {member.DeclaringType?.Name}.{member.Name}: {itemType.Name} has unserializable members");
        }

        if (!isCollection && isItemRefType) {
            // Omit prefab components (invalid scene)
            property.ShouldSerialize = instance => {
                if (!shouldSerialize) {
                    return false;
                }

                var value = property.ValueProvider?.GetValue(instance);
                return value is not Component c || c.gameObject.scene.IsValid();
            };
        } else {
            property.ShouldSerialize = _ => shouldSerialize;
        }

        return property;
    }

    private bool RefType(Type type) {
        return typeof(Component).IsAssignableFrom(type) && type != typeof(Animator);
    }
}

// remove zero-argument constructor to prevent the game's newtonsoft collection to try to apply it
#pragma warning disable CS9113 // Parameter is unread.
internal class RefConverter(int dummy) : JsonConverter {
#pragma warning restore CS9113 // Parameter is unread.
    public const bool RestoreRefs = true;

    public static HashSet<Component> References = [];

    private static void WriteReference(JsonWriter writer, object? component) {
        if (component == null) {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("$ref");
        writer.WriteValue(GetRef(component));
        writer.WriteEndObject();
    }

    // returns (found: true, null) for explicit null, (found: false, null) for unresolvable ref
    private static (bool found, Component? value) ReadReference(JsonReader reader) {
        if (reader.TokenType == JsonToken.Null) {
            return (true, null);
        }

        if (reader.TokenType != JsonToken.StartObject) {
            throw new JsonSerializationException($"Expected StartObject token, got {reader.TokenType}");
        }

        string? refPath = null;
        while (reader.Read()) {
            if (reader.TokenType == JsonToken.PropertyName && reader.Value?.ToString() == "$ref") {
                reader.Read();
                refPath = reader.Value?.ToString();
                break;
            }
        }

        if (refPath == null) {
            throw new JsonSerializationException($"Expected $ref property at {reader.Path}");
        }

        while (reader.Read() && reader.TokenType != JsonToken.EndObject) {
        }

        var resolved = ObjectUtils.LookupObjectComponentPath(refPath);
        return resolved != null ? (true, resolved) : (false, null);
    }

    private static IEnumerable<Component?> ReadReferenceArray(JsonReader reader) {
        if (reader.TokenType != JsonToken.StartArray) {
            throw new JsonSerializationException($"Expected StartArray token, got {reader.TokenType}");
        }

        reader.Read();

        while (reader.TokenType != JsonToken.EndArray) {
            var (found, value) = ReadReference(reader);
            if (!found) {
                Log.Warning($"Could not resolve $ref in array at {reader.Path}, skipping element");
            }

            yield return value;

            reader.Read();
        }
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        if (value == null) {
            writer.WriteNull();
            return;
        }

        if (value is Component component) {
            if (!component) {
                throw new NotImplementedException("!component");
            }

            References.Add(component);
            WriteReference(writer, value);
        } else if (value is IEnumerable list) {
            writer.WriteStartArray();
            foreach (var item in list) {
                WriteReference(writer, item);
            }

            writer.WriteEndArray();
        } else {
            throw new NotImplementedException($"RefConverter got {value.GetType()}");
        }
    }


    public override object? ReadJson(JsonReader reader, Type type, object? existingValue,
        JsonSerializer serializer) {
        if (!RestoreRefs) {
            JToken.ReadFrom(reader);
            return existingValue;
        }

        if (reader.TokenType == JsonToken.Null) {
            return null;
        }

        if (type.IsArray) {
            var arr = ReadReferenceArray(reader).ToArray();
            var newArray = Array.CreateInstance(type.GetElementType()!, arr.Length);
            for (var i = 0; i < arr.Length; i++) {
                newArray.SetValue(arr[i], i);
            }

            return newArray;
        }

        if (typeof(IList).IsAssignableFrom(type)) {
            if (existingValue is not IList list) {
                throw new JsonSerializationException("List requires existing value");
            }

            list.Clear();
            foreach (var item in ReadReferenceArray(reader)) {
                list.Add(item);
            }

            return list;
        }

        if (type.IsGenericType) {
            if (existingValue is null) {
                throw new JsonSerializationException("reference collection requires existing value");
            }

            if (type.GetGenericTypeDefinition() == typeof(HashSet<>)) {
                existingValue.InvokeMethod("Clear");
                foreach (var item in ReadReferenceArray(reader)) {
                    existingValue.InvokeMethod<bool>("Add", item);
                }

                return existingValue;
            }

            throw new NotImplementedException($"RefConverter called for {type}");
        }

        if (typeof(Component).IsAssignableFrom(type)) {
            var (found, value) = ReadReference(reader);
            if (!found) {
                Log.Warning($"Could not resolve $ref for {type.Name}, keeping existing value");
                return existingValue;
            }

            return value;
        }

        throw new NotImplementedException($"RefConverter called for {type}");
    }

    // we only assign the in the contract resolver
    public override bool CanConvert(Type objectType) {
        return true;
    }

    private static string GetRef(object obj) {
        if (obj is Component component) {
            return ObjectUtils.ObjectComponentPath(component);
        }

        throw new NotImplementedException("GetRef not for component");
    }
}
