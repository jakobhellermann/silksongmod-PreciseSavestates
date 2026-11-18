using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PreciseSavestates.Source;
using PreciseSavestates.Savestates.Snapshot;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// Coroutines don't survive a load. For registered game coroutines whose progress is gameplay state, the iterator's
// fields and the rest of its current WaitForSeconds are captured, and a fresh iterator in that state resumes after it
[HarmonyPatch]
public class CoroutineSnapshot {
    public required string Owner;
    public required string Method;
    public required JObject Fields;
    public float Wait;
    public float Remaining;

    // the arena's start pause and its waves' start delays
    private static readonly MethodInfo[] registered = [
        AccessTools.Method(typeof(BattleScene), nameof(BattleScene.DoStartBattle)),
        AccessTools.Method(typeof(BattleScene), nameof(BattleScene.StartWave)),
    ];

    private static readonly Dictionary<Type, MethodInfo> iteratorMethods =
        registered.ToDictionary(method => AccessTools.EnumeratorMoveNext(method).DeclaringType!);

    private static readonly FieldInfo waitSeconds =
        typeof(WaitForSeconds).GetFieldInfo("m_Seconds", ReflectionExtension.InstanceAnyVisibility)
        ?? throw new MissingFieldException(nameof(WaitForSeconds), "m_Seconds");

    // running iterators with when their current wait started and how long it is
    private static readonly Dictionary<IEnumerator, (float start, float seconds)> running = [];

    private static IEnumerable<MethodBase> TargetMethods() => registered.Select(AccessTools.EnumeratorMoveNext);

    private static void Postfix(IEnumerator __instance, bool __result) {
        if (!__result) {
            running.Remove(__instance);
        } else if (__instance.Current is WaitForSeconds wait) {
            running[__instance] = (Time.time, (float)waitSeconds.GetValue(wait));
        } else {
            // throwing would abort the game's coroutine
            Log.Error($"{__instance.GetType().Name} yielded {__instance.Current}, only WaitForSeconds can be resumed");
            running.Remove(__instance);
        }
    }

    public static List<CoroutineSnapshot> Of(Func<GameObject, bool> captured) {
        var snapshots = new List<CoroutineSnapshot>();
        foreach (var (iterator, wait) in running.ToList()) {
            var owner = (MonoBehaviour)OwnerField(iterator.GetType()).GetValue(iterator);
            // deactivating the owner's object stops its coroutines for good
            if (!owner || !owner.gameObject.activeInHierarchy) {
                running.Remove(iterator);
                continue;
            }
            if (!captured(owner.gameObject)) continue;

            var fields = (JObject)SnapshotSerializer.Snapshot(iterator);
            fields.Remove("<>2__current");
            fields.Remove("<>4__this");
            var method = iteratorMethods[iterator.GetType()];
            snapshots.Add(new CoroutineSnapshot {
                Owner = ObjectUtils.ObjectPath(owner.gameObject),
                Method = $"{method.DeclaringType!.Name}.{method.Name}",
                Fields = fields,
                Wait = wait.seconds,
                Remaining = Mathf.Max(0f, wait.seconds - (Time.time - wait.start)),
            });
        }
        return snapshots;
    }

    public void Restore() {
        var (iteratorType, method) = iteratorMethods.Single(m => $"{m.Value.DeclaringType!.Name}.{m.Value.Name}" == Method);
        if (ObjectUtils.LookupPath(Owner)?.GetComponent(method.DeclaringType) is not MonoBehaviour owner) {
            Log.Error($"Savestate stored coroutine {Method} on {Owner}, which does not exist at load time");
            return;
        }

        Log.Info($"- Resuming {Method} on {Owner} after {Remaining:F2}s");
        var iterator = (IEnumerator)Activator.CreateInstance(iteratorType, 0);
        OwnerField(iteratorType).SetValue(iterator, owner);
        SnapshotSerializer.Populate(iterator, Fields);
        running[iterator] = (Time.time - (Wait - Remaining), Wait);
        owner.StartCoroutine(AfterWait(Remaining, iterator));
    }

    private static FieldInfo OwnerField(Type iteratorType) =>
        iteratorType.GetFieldInfo("<>4__this", ReflectionExtension.InstanceAnyVisibility)
        ?? throw new MissingFieldException(iteratorType.Name, "<>4__this");

    private static IEnumerator AfterWait(float remaining, IEnumerator iterator) {
        yield return new WaitForSeconds(remaining);
        yield return iterator;
    }
}
