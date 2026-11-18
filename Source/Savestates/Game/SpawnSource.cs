using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

// How an object that only exists at runtime was created, so a load can create it again. Exactly one option is set
public sealed class SpawnSource {
    public CreateObjectSpawn? CreateObject;

    public Vector3 Position;
    public Quaternion Rotation;

    public static SpawnSource Of(GameObject spawned, CreateObjectSpawn createObject) => new() {
        CreateObject = createObject,
        Position = spawned.transform.position,
        Rotation = spawned.transform.rotation,
    };

    public GameObject? Spawn() {
        if (CreateObject?.Prefab() is not { } prefab) {
            return null;
        }

        var spawned = Object.Instantiate(prefab, Position, Rotation);
        // their snapshots restore the active state, which starting would enter again
        foreach (var fsm in spawned.GetComponentsInChildren<PlayMakerFSM>(true)) {
            fsm.Fsm.Started = true;
        }

        return spawned;
    }
}

// spawned by a PlayMaker CreateObject action, which holds the prefab
public sealed class CreateObjectSpawn {
    public required string FsmPath;
    public required string FsmName;
    public int StateIndex;
    public int ActionIndex;

    // the live objects the FSM's CreateObject actions spawned, each from the action whose prefab it is a clone of
    public static IEnumerable<(GameObject spawned, CreateObjectSpawn spawn)> In(PlayMakerFSM fsm) {
        var states = fsm.Fsm.States;
        for (var stateIndex = 0; stateIndex < states.Length; stateIndex++) {
            for (var actionIndex = 0; actionIndex < states[stateIndex].Actions.Length; actionIndex++) {
                if (states[stateIndex].Actions[actionIndex] is CreateObject { storeObject.Value: { } spawned, gameObject.Value: { } prefab }
                    && spawned && spawned.name == $"{prefab.name}(Clone)") {
                    yield return (spawned, new CreateObjectSpawn {
                        FsmPath = ObjectUtils.ObjectPath(fsm.gameObject),
                        FsmName = fsm.FsmName,
                        StateIndex = stateIndex,
                        ActionIndex = actionIndex,
                    });
                }
            }
        }
    }

    public GameObject? Prefab() {
        var fsm = ObjectUtils.LookupPath(FsmPath)?.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == FsmName);
        return fsm?.Fsm.States[StateIndex].Actions[ActionIndex] is CreateObject { gameObject.Value: { } prefab } ? prefab : null;
    }
}
