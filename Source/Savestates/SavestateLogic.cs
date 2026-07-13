using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DevUtils.Toasts;
using GlobalEnums;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Newtonsoft.Json.Linq;
using PreciseSavestates.Savestates.Snapshot;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using Component = UnityEngine.Component;
using Random = UnityEngine.Random;

namespace PreciseSavestates.Savestates;

[Flags]
public enum SavestateFilter {
    None = 0,
    Player = 1 << 1,
    Scene = 1 << 2,
    // Bosses/enemies (HealthManager actors) and the active + additive scenes' PlayMaker FSMs — separate from Scene
    // (SceneData) because it's live actor state, not persisted save data.
    Enemies = 1 << 3,
    All = Player | Scene | Enemies,
}

[Flags]
public enum SavestateLoadMode {
    None = 0,
    ResetScene = 1 << 0,
    ReloadScene = 1 << 1,
}

public static class SavestateLogic {
    [HarmonyPatch]
    private class Patches {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), "FinishedEnteringScene")]
#pragma warning disable HARMONIZE001
        private static void FinishedEnteringScene() {
#pragma warning restore HARMONIZE001
            sceneLoadedSource?.TrySetResult(true);
        }
    }


    public static Savestate Create(SavestateFilter filter) {
        var gm = GameManager.instance;
        if (gm.GameState != GameState.PLAYING) {
            throw new Exception($"Can't create savestate in state {gm.GameState}");
        }

        var player = HeroController.instance;

        var seen = new HashSet<Component>();
        var sceneBehaviours = new List<ComponentSnapshot>();
        var gameObjectSnapshots = new List<GameObjectSnapshot>();
        var fsmSnapshots = new List<PlayMakerFsmSnapshot>();
        var audioTableSnapshots = new List<RandomAudioTableSnapshot>();

        if (filter.HasFlag(SavestateFilter.Player)) {
            sceneBehaviours.Add(ComponentSnapshot.Of(player.transform));
            sceneBehaviours.Add(ComponentSnapshot.Of(player.GetFieldValue<Rigidbody2D>("rb2d")!));
            // Hero MeshRenderer — captures its enabled flag (see the Renderer allowlist); the recursive snapshot below
            // doesn't reach it.
            sceneBehaviours.Add(ComponentSnapshot.Of(player.GetComponent<MeshRenderer>()));
            // GameObject-level state (physics layer) — a hazard death moves the hero off its normal layer.
            gameObjectSnapshots.Add(GameObjectSnapshot.Of(player.gameObject));
            SnapshotSerializer.SnapshotRecursive(player, sceneBehaviours, seen, 0);

            // tk2d animation state (current clip + frame) — only the animator field is captured (allowlist), via
            // Tk2dAnimatorConverter, so the hero resumes mid-animation instead of snapping to a default pose.
            sceneBehaviours.Add(ComponentSnapshot.Of(player.AnimCtrl));

            // PlayMaker FSM runtime state (sprint, tools, etc.) lives in separate PlayMakerFSM components, not in
            // HeroController fields — capture it so e.g. a savestate taken mid-sprint restores correctly.
            foreach (var fsm in player.GetComponents<PlayMakerFSM>()) {
                fsmSnapshots.Add(PlayMakerFsmSnapshot.Of(fsm));
            }

            // Hornet's audio tables (attack/wound/...) are shared ScriptableObjects whose runtime selection state
            // survives scene loads and isn't captured by the recursive snapshot — and they draw from the global
            // RNG, so leftover state desyncs a TAS. See RandomAudioTableSnapshot. (footStepTables, an array, is
            // skipped for now.)
            audioTableSnapshots.AddRange(HeroAudioTables(player).Select(RandomAudioTableSnapshot.Of));

            // Camera: CameraController drives the main camera toward the hero via Vector3.SmoothDamp every frame. A
            // load that doesn't restore it starts the camera at the scene-entry default and *pans* in (with a fade) —
            // a visible, and (since it is traced) determinism-breaking, divergence from a continuous run. Capture the
            // controller's own state (its private SmoothDamp velocity + damp/lock/look fields) and the camera
            // transform, so playback frame 0 has the camera exactly where it was captured, at rest — no pan. Both live
            // on persistent (DontDestroyOnLoad) objects, so their paths resolve at load time for the restore.
            if (GameManager.instance.cameraCtrl is { } cameraCtrl) {
                sceneBehaviours.Add(ComponentSnapshot.Of(cameraCtrl));
                if (GameCameras.instance is { mainCamera: { } mainCamera }) {
                    sceneBehaviours.Add(ComponentSnapshot.Of(mainCamera.transform));
                }

                // The camera doesn't aim at the hero directly — it follows a separate CameraTarget whose mode
                // (LOCK_ZONE/FREE), lock-zone bounds, offsets, destination and position decide where the camera
                // goes. Without capturing it, a load leaves the target stuck in the previous lock zone (e.g. a boss
                // arena), so the restored camera sits at that zone's position instead of following the restored hero
                // (CameraController.destination is computed relative to this target, not the hero).
                if (cameraCtrl.camTarget is { } camTarget) {
                    sceneBehaviours.Add(ComponentSnapshot.Of(camTarget));
                    sceneBehaviours.Add(ComponentSnapshot.Of(camTarget.transform));
                }
            }
        }

        if (filter.HasFlag(SavestateFilter.Enemies)) {
            // Bosses / enemies live in the active scene and its additive sub-scenes (e.g. Cog_Dancers_boss), not under
            // the hero root, so the recursive hero snapshot never reaches them. Without capturing them a load reloads
            // the scene and every enemy resets to full HP / spawn pose / a fresh FSM — a mid-fight resume desyncs
            // immediately. Capture, for a faithful resume:
            //  - every active HealthManager: its HP + internal hit/stun state, plus its `animator` field, which is a
            //    tk2dSpriteAnimator so PropertyConverters serializes the current clip/frame inline (same converter the
            //    hero's animator uses) — the enemy actor state;
            //  - its transform (position);
            //  - every PlayMakerFSM in the active + additive scenes: boss AI/phase/attack-pattern FSMs (Dancer Control,
            //    Dive Patterns, …) plus arena state (battle-gate + fight-sequence FSMs). Captured broadly ("all scene
            //    FSMs") — a cosmetic FSM that round-trips is harmless; a non-deterministic one surfaces as a resume diff
            //    to fix rather than being silently missed.
            // All resolve by hierarchy path at load time (the additive scenes are preloaded before the snapshot applies).
            var activeHealthManagers = typeof(HealthManager).GetFieldValue<List<HealthManager>>("_activeHealthManagers");
            if (activeHealthManagers != null) {
                foreach (var hm in activeHealthManagers) {
                    if (!hm) {
                        continue;
                    }

                    sceneBehaviours.Add(ComponentSnapshot.Of(hm));
                    sceneBehaviours.Add(ComponentSnapshot.Of(hm.transform));
                    // The body's velocity: physics-driven enemies (e.g. a MossBone Fly's IdleBuzzV3, a
                    // RigidBody2dActionBase that accelerates rb2d.velocity toward a roam target) resume with a stale
                    // velocity otherwise — the transform restores the position but the body keeps drifting off it,
                    // which cascades into a divergent movement/turn. The Rigidbody2D allowlist captures
                    // position/linearVelocity/gravityScale/bodyType.
                    if (hm.GetComponent<Rigidbody2D>() is { } rb) {
                        sceneBehaviours.Add(ComponentSnapshot.Of(rb));
                    }
                }
            }

            var additiveScenes = CurrentAdditiveScenes();
            var sceneNames = new HashSet<string> { gm.sceneName };
            sceneNames.UnionWith(additiveScenes);
            // Anchor on FSM-bearing objects: a PlayMakerFSM drives runtime state (boss AI, gates), and its one-time
            // OnEnter actions set object-level state — activeSelf (ActivateGameObject), collider `enabled`
            // (SetCollider), sprite clip (Tk2dPlayAnimation). A scene reload resets those to the prefab default and the
            // FSM restore resumes the active state directly without re-running OnEnter, so that object-level state is
            // lost unless captured explicitly. Static scene geometry has no FSM and no such state, so skip it (a full
            // component snapshot of every object was far too broad).
            //
            // For activeSelf we snapshot exactly the objects whose active flag is *runtime-changeable* by these FSMs:
            // the FSM owners themselves, plus every GameObject targeted by an ActivateGameObject(Delay) action in any
            // captured FSM. That's the precise set the OnEnter-skipping restore would otherwise leave at the prefab
            // default — e.g. Dancer Control's "Deactivate Positions" toggles the non-FSM Pos1..12 markers (prefab-
            // active), so they come back active after a reload unless restored. Resolved via the FSM's own owner-
            // default resolution, so variable targets (`var "Pos1"`) resolve to the right instance. Capturing only the
            // toggle targets (not the whole subtree) keeps the snapshot small; both toggle directions are covered
            // (a prefab-disabled object an FSM enables is a target too). Colliders/sprite animators stay on the FSM
            // owners (the SetCollider/PlayAnimation targets); widen if a similar gap surfaces.
            var seenFsmObjects = new HashSet<GameObject>();
            var gameObjectTargets = new HashSet<GameObject>();
            foreach (var fsm in PlayMakerFSM.FsmList) {
                if (!fsm || !sceneNames.Contains(fsm.gameObject.scene.name)) {
                    continue;
                }

                fsmSnapshots.Add(PlayMakerFsmSnapshot.Of(fsm));

                var go = fsm.gameObject;
                gameObjectTargets.Add(go);

                // Every GameObject an ActivateGameObject(Delay) action in this FSM can toggle active/inactive.
                foreach (var state in fsm.Fsm.States) {
                    foreach (var action in state.Actions) {
                        var ownerDefault = action switch {
                            ActivateGameObject a => a.gameObject,
                            ActivateGameObjectDelay a => a.gameObject,
                            _ => null,
                        };
                        if (ownerDefault == null) {
                            continue;
                        }

                        GameObject? target = null;
                        try {
                            target = fsm.Fsm.GetOwnerDefaultTarget(ownerDefault);
                        } catch (Exception e) {
                            Log.Warning($"Could not resolve ActivateGameObject target in {fsm.Fsm.Name}: {e.Message}");
                        }

                        if (target) {
                            gameObjectTargets.Add(target!);
                        }
                    }
                }

                if (!seenFsmObjects.Add(go)) {
                    continue;
                }

                foreach (var collider in go.GetComponents<Collider2D>()) {
                    sceneBehaviours.Add(ComponentSnapshot.Of(collider));
                }

                if (go.GetComponent<tk2dSpriteAnimator>() is { } spriteAnimator) {
                    sceneBehaviours.Add(ComponentSnapshot.Of(spriteAnimator));
                }
            }

            foreach (var target in gameObjectTargets) {
                gameObjectSnapshots.Add(GameObjectSnapshot.Of(target));
            }

            // BattleScene is a plain MonoBehaviour (no FSM / HealthManager) holding the wave progression, so nothing
            // above reaches it; without it a reload resets the arena to an un-started battle.
            foreach (var battleScene in UnityEngine.Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Include,
                         FindObjectsSortMode.None)) {
                if (!battleScene || !sceneNames.Contains(battleScene.gameObject.scene.name)) {
                    continue;
                }

                sceneBehaviours.Add(ComponentSnapshot.Of(battleScene));
            }
        }

        JToken? playerData = null;
        if (filter.HasFlag(SavestateFilter.Player)) {
            // The player save-data singleton. Excluded from the recursive HeroController snapshot (see
            // SnapshotSerializer FieldDenylist) and captured here so it can be restored before scene-object init.
            playerData = JToken.Parse(JsonUtility.ToJson(global::PlayerData.instance));
        }

        JToken? sceneData = null;
        if (filter.HasFlag(SavestateFilter.Scene)) {
            // Flush live persistent objects (levers, dead enemies, …) into SceneData first, then capture the whole
            // singleton via Unity serialization. JsonUtility handles its ISerializationCallbackReceiver collections;
            // the recursive component snapshot can't reach standalone SceneData.instance.
            gm.SaveLevelState();
            sceneData = JToken.Parse(JsonUtility.ToJson(global::SceneData.instance));
            CanonicalizeSerializedLists(sceneData);
        }

        var savestate = new Savestate {
            Scene = gm.sceneName,
            AdditiveScenes = CurrentAdditiveScenes(),
            ComponentSnapshots = sceneBehaviours,
            GameObjectSnapshots = gameObjectSnapshots,
            FsmSnapshots = fsmSnapshots,
            AudioTableSnapshots = audioTableSnapshots,
            RandomState = Random.state,
            GameTime = Time.time,
            GameFrameCount = Time.frameCount,
            FixedUpdateCycle = CustomPlayerLoop.FixedUpdateCycle,
            HazardRespawn = HazardRespawnSnapshot.Of(global::PlayerData.instance),
            PlayerData = playerData,
            SceneData = sceneData,
        };

        return savestate;
    }

    // SceneData's PersistentItemDataCollection flattens its per-scene dictionaries into `serializedList` in
    // dictionary-iteration order, which depends on scene load history — so two captures of the same state emit the
    // same entries in a different order (a spurious diff, not a state difference). Sort each serializedList by
    // (SceneName, ID) for a canonical, comparable capture; restore rebuilds the keyed dictionary regardless of order.
    private static void CanonicalizeSerializedLists(JToken token) {
        foreach (var list in token.SelectTokens("$..serializedList").OfType<JArray>().ToList()) {
            var sorted = new JArray(list.OfType<JObject>()
                .OrderBy(e => (string?)e["SceneName"], StringComparer.Ordinal)
                .ThenBy(e => (string?)e["ID"], StringComparer.Ordinal)
                .Select(e => e.DeepClone()));
            list.Replace(sorted);
        }
    }

    /// The distinct RandomAudioClipTable ScriptableObjects referenced by single-valued HeroController fields
    /// (attackAudioTable / wound* / ...). Reflection-enumerated so it auto-covers all such fields. The
    /// footStepTables[] array is intentionally skipped for now.
    private static IEnumerable<RandomAudioClipTable> HeroAudioTables(HeroController player) {
        var seen = new HashSet<RandomAudioClipTable>();
        foreach (var field in typeof(HeroController).GetFields(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (field.FieldType == typeof(RandomAudioClipTable)
                && field.GetValue(player) is RandomAudioClipTable table && table && seen.Add(table)) {
                yield return table;
            }
        }
    }

    /*
    public static Savestate Create(SavestateFilter filter) {
        var gameCore = GameCore.Instance;
        if (!gameCore.gameLevel) {
            throw new Exception("Can't create savestate outside of game level");
        }*/

    /*var player = Player.i;

    var sceneBehaviours = new List<ComponentSnapshot>();
    var gameObjects = new List<GameObjectSnapshot>();
    var monsterLoveFsmSnapshots = new List<MonsterLoveFsmSnapshot>();
    var fsmSnapshots = new List<GeneralFsmSnapshot>();
    var flagsJson = new JObject();

    // TODO:
    // - jades
    //  - revival jade
    // - qi in UI
    // - broken floor

    var seen = new HashSet<Component>();

    if (filter.HasFlag(SavestateFilter.Player)) {
        sceneBehaviours.Add(ComponentSnapshot.Of(player.transform));
        gameObjects.Add(GameObjectSnapshot.Of(player.pushAwayCollider.gameObject));
        SnapshotSerializer.SnapshotRecursive(player, sceneBehaviours, seen);
        foreach (var (_, state) in player.fsm.GetStates()) {
            SnapshotSerializer.SnapshotRecursive(state, sceneBehaviours, seen, 0);
        }

        SnapshotSerializer.SnapshotRecursive(CameraManager.Instance.camera2D, sceneBehaviours, seen, 0);
    }

    if (filter.HasFlag(SavestateFilter.Monsters)) {
        foreach (var monster in Object.FindObjectsOfType<MonsterBase>()) {
            sceneBehaviours.Add(ComponentSnapshot.Of(monster.transform));
            SnapshotSerializer.SnapshotRecursive(monster, sceneBehaviours, seen);
            if (monster.fsm == null) {
                Log.Warning($"{monster} fsm was null, skipping");
                continue;
            }

            monsterLoveFsmSnapshots.Add(MonsterLoveFsmSnapshot.Of(monster.fsm));

            foreach (var attackSensor in monster.AttackSensorsCompat()) {
                SnapshotSerializer.SnapshotRecursive(attackSensor, sceneBehaviours, seen);
            }

            foreach (var (_, state) in monster.fsm.GetStates()) {
                SnapshotSerializer.SnapshotRecursive(state, sceneBehaviours, seen);
            }

            monsterLoveFsmSnapshots.Add(MonsterLoveFsmSnapshot.Of(monster.fsm));
        }
    }

    if (filter.HasFlag(SavestateFilter.FSMs)) {
        foreach (var smo in Object.FindObjectsOfType<StateMachineOwner>()) {
            if (!smo.FsmContext?.fsm?.State) {
                Log.Warning($"{smo} fsm was null, skipping");
                continue;
            }

            fsmSnapshots.Add(GeneralFsmSnapshot.Of(smo));
        }
    }

    if (filter.HasFlag(SavestateFilter.Player)) {
        monsterLoveFsmSnapshots.Add(MonsterLoveFsmSnapshot.Of(player.fsm));
    }

    if (filter.HasFlag(SavestateFilter.Flags)) {
        // PERF: remove parse(encode(val))
#pragma warning disable CS0618 // Type or member is obsolete
        flagsJson = JObject.Parse(GameFlagManager.FlagsToJson(SaveManager.Instance.allFlags));
#pragma warning restore CS0618 // Type or member is obsolete
    }

    var savestate = new Savestate {
        Flags = flagsJson.Count == 0 ? null : flagsJson,
        Scene = gameCore.gameLevel.gameObject.scene.name,
        PlayerPosition = filter.HasFlag(SavestateFilter.Player) ? player.transform.position : null,
        LastTeleportId = ApplicationCore.Instance.lastSaveTeleportPoint.FinalSaveID,
        MonobehaviourSnapshots = sceneBehaviours,
        GameObjectSnapshots = gameObjects,
        FsmSnapshots = monsterLoveFsmSnapshots.Count == 0 ? null : monsterLoveFsmSnapshots,
        GeneralFsmSnapshots = fsmSnapshots.Count == 0 ? null : fsmSnapshots,
        RandomState = UnityEngine.Random.state,
    };

    return savestate;
}*/


    public static bool IsLoadingSavestate;
    private static TaskCompletionSource<bool>? sceneLoadedSource;

    public static async Task Load(Savestate savestate, SavestateLoadMode loadMode) {
        var total = Stopwatch.StartNew();
        // A scene-less savestate is an in-place normalization apply (the `load` command's idle fixture), not a
        // user-facing scene load — keep it quiet (no toast, no timing log; see LoadInner).
        var silent = string.IsNullOrEmpty(savestate.Scene);
        try {
            IsLoadingSavestate = true;
            await LoadInner(savestate, loadMode);
            if (!silent) {
                ToastManager.Toast($"Loaded in {total.ElapsedMilliseconds}ms");
            }
        } finally {
            IsLoadingSavestate = false;
        }
    }

    private static async Task LoadInner(Savestate savestate, SavestateLoadMode loadMode) {
        var silent = string.IsNullOrEmpty(savestate.Scene);
        if (!silent) {
            Log.Info($"Loading savestate... (scene='{savestate.Scene}', mode={loadMode})");
        }

        var timing = new LoadTiming();
        var gm = GameManager.instance;
        if (gm.GameState != GameState.PLAYING) {
            throw new Exception($"Can't load savestate in state {gm.GameState}");
        }

        // Cancel any in-flight death/respawn/invuln coroutines and reset the effects they drive — state a snapshot
        // can't restore. Runs for every load (including the `load` command's idle-fixture normalization); a no-op when
        // no death is in flight.
        LoadCoroutineCleanup.Run();

        // Restore SceneData while the target scene is loading — after the outgoing scene's unload-write
        // (GameManager.SaveLevelState fires early in the transition) but before the incoming scene's persistent
        // objects read it in their Start (they've Awoken by sceneLoaded, Start runs after). Doing it here, rather
        // than in the post-load restore loop below, is why no dummy scene is needed. Component/FSM snapshots are
        // different: they overwrite already-live objects in place, so post-load is fine for them.
        void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if (scene.name != savestate.Scene) {
                return;
            }

            SceneManager.sceneLoaded -= OnSceneLoaded;

            if (savestate.PlayerData is { } playerData) {
                JsonUtility.FromJsonOverwrite(playerData.ToString(), global::PlayerData.instance);
            }

            if (savestate.SceneData is { } sceneData) {
                JsonUtility.FromJsonOverwrite(sceneData.ToString(), global::SceneData.instance);
            }

            timing.Mark("sceneAssets");
            Log.Info($"- Restored PlayerData/SceneData for scene '{scene.name}' (before persistent objects' Start)");
        }

        var sw = Stopwatch.StartNew();
        if (!string.IsNullOrEmpty(savestate.Scene)) {
            // Restore PlayerData up front, before the scene's additive-load conditionals evaluate it. Those loaders
            // (SceneAdditiveLoadConditional) read flags like encounteredSongGolem in OnEnable/Start to pick which boss
            // sub-scene to load, and latch the decision (loadAlt is set once and never reset) — so if they run before
            // the OnSceneLoaded restore below, a stale value (e.g. encounteredSongGolem=true after a fight) permanently
            // selects the wrong variant (or none) and the sub-scene never reloads. SceneData stays in OnSceneLoaded:
            // it must land after the outgoing scene's SaveLevelState write, and nothing reads it this early.
            if (savestate.PlayerData is { } earlyPlayerData) {
                JsonUtility.FromJsonOverwrite(earlyPlayerData.ToString(), global::PlayerData.instance);
            }

            // Unload the current scene's additive sub-scenes before reloading it. A transition into the scene you're
            // already in does not unload them, and SceneAdditiveLoadConditional.Start() skips reloading a sub-scene it
            // finds already loaded — so an object destroyed in a sub-scene during play persists across the load (e.g.
            // the song golem's resting statue, broken on wake, lives in the additive Bone_East_08_boss_golem_rest
            // scene; without this it stays a childless shell). Unloading them lets the reloaded scene's loaders
            // re-instantiate them fresh; PlayerData is restored before their Start (OnSceneLoaded) so they pick the
            // right variant. Await each so the fresh loaders don't see a half-unloaded scene and skip the reload.
            //
            // Unload by scene reference, not via the loaders' stored Addressables handles: a sub-scene loaded through
            // ScenePreloader carries a handle that is invalid to Addressables.UnloadSceneAsync ("invalid operation
            // handle") and would abort the whole load. (Trade-off: SceneManager unload doesn't release the Addressables
            // bundle ref, a small per-load leak.)
            var activeScene = SceneManager.GetActiveScene();
            var subScenes = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++) {
                var scene = SceneManager.GetSceneAt(i);
                if (scene != activeScene && scene.isLoaded && scene.name != "DontDestroyOnLoad") {
                    subScenes.Add(scene);
                }
            }

            foreach (var scene in subScenes) {
                if (SceneManager.UnloadSceneAsync(scene) is { } unloadOp) {
                    var unloaded = new TaskCompletionSource<bool>();
                    unloadOp.completed += _ => unloaded.SetResult(true);
                    await unloaded.Task;
                }
            }

            timing.Mark("unload");
            ScenePhaseTiming.Reset();
            // Skip the scene load's synchronous full GC (GCManager.Collect, the single biggest phase — ~40% of a
            // reload's wall-clock). A savestate reload happens often and doesn't need a full collection each time;
            // the game's own DisabledManualCollect switch turns GCManager.Collect into a no-op. Restored after the
            // transition so normal (non-savestate) scene loads still collect.
            var wasManualCollectDisabled = GCManager.DisabledManualCollect;
            GCManager.DisabledManualCollect = true;
            sceneLoadedSource = new TaskCompletionSource<bool>();
            SceneManager.sceneLoaded += OnSceneLoaded;
            try {
                gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
                    SceneName = savestate.Scene,
                    HeroLeaveDirection = GatePosition.unknown,
                    EntryGateName = "dreamGate",
                    EntryDelay = 0f,
                    PreventCameraFadeOut = true,
                    WaitForSceneTransitionCameraFade = false,
                    Visualization = GameManager.SceneLoadVisualizations.Default,
                });
                await sceneLoadedSource.Task;

                // The reloaded scene's boss SceneAdditiveLoadConditional registers only *after* the load and then
                // decides its load path by LoadInSequence: it's set true at every scene-load start and flipped false
                // by the LoadBoss phase's LoadAll(). But that LoadAll only runs if a loader was already registered at
                // the LoadBoss check — the fresh loader isn't yet, so whether LIS ends up false depends on racy stale
                // loaders from the previous scene (the "boss loads by parity" bug). A loader whose Start sees LIS=false
                // self-loads its sub-scene; LIS=true leaves it deferred forever. Force LIS=false so the post-load
                // loader always self-loads deterministically (its own TryTestLoad still gates whether it loads at all,
                // so a no-boss savestate stays boss-less).
                SceneAdditiveLoadConditional.LoadInSequence = false;

                await PreloadAdditiveScenes(savestate.AdditiveScenes);
            } finally {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                GCManager.DisabledManualCollect = wasManualCollectDisabled;
            }

            timing.Mark("enterTail");
            // Fold in the per-phase scene-load timing (Fetch/ClearMem/Activation/GarbageCollect). Measured by our own
            // wall-clock hook on SceneLoad.Record{Begin,End}Time — the game's own GetDuration reads
            // Time.realtimeSinceStartup, which DeterministicTimePatch freezes during a TAS load (so it reports 0).
            foreach (var (phase, ms) in ScenePhaseTiming.Durations()) {
                timing.Add($"scene.{phase}", ms);
            }

            Log.Info($"- Loaded scene in {sw.ElapsedMilliseconds}ms");
        }

        // The scene + pre-Start save data (PlayerData/SceneData) is now in place. The rest of the snapshot
        // (components/FSM/audio/RNG/clock) overwrites already-live objects, so it can be applied at any point after
        // the scene load. When DeferSnapshotRestore is set, hold it as PendingSnapshot for the driver to apply at a
        // controlled player-loop phase (symmetric with the capture) instead of here in the async scene-load
        // continuation — that's what makes a resumed run land byte-identical to a continuous one (no dead frame).
        // The timing summary is logged after the apply: inline here, or in ApplyPendingSnapshot for the deferred path.
        if (DeferSnapshotRestore) {
            PendingSnapshot = savestate;
            pendingTiming = timing;
        } else {
            ApplySnapshot(savestate, timing);
            if (!silent) {
                timing.LogSummary();
            }
        }
    }

    // Additive scenes loaded alongside the active scene — everything except the active scene and DontDestroyOnLoad.
    // These are the boss-arena sub-scenes; captured so a load can reproduce exactly them.
    private static List<string> CurrentAdditiveScenes() {
        var active = SceneManager.GetActiveScene();
        var scenes = new List<string>();
        for (var i = 0; i < SceneManager.sceneCount; i++) {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != active && scene.isLoaded && scene.name != "DontDestroyOnLoad") {
                scenes.Add(scene.name);
            }
        }

        return scenes;
    }

    // The boss arena is an additive sub-scene loaded by a SceneAdditiveLoadConditional whose Start (and thus its async
    // LoadRoutine) runs only once script updates resume — i.e. during playback, after this load window closes — so the
    // load completes on a real-time-variable frame mid-playback and desyncs the trace. Preload the captured scenes here
    // via the same Addressables key the loader uses ("Scenes/" + name), which loads *and activates* them. When the
    // loader's Start later runs it finds the scene already loaded and takes its already-loaded shortcut (skipping the
    // racy async load). Loaded independently of any loader coroutine, so there's no double-load conflict.
    //
    // The shortcut sets sceneLoaded=true but never sets the loader's `loadOp` handle, so its Unload() couldn't release
    // the sub-scene on a later transition (→ orphaned, the HK "room dupe"). Stash our Addressables handle so
    // AdditiveScenePreload can inject it into the loader's loadOp when it takes the shortcut.
    private static async Task PreloadAdditiveScenes(List<string>? wanted) {
        if (wanted is not { Count: > 0 }) {
            return;
        }

        foreach (var name in wanted) {
            if (SceneManager.GetSceneByName(name).isLoaded) {
                continue;
            }

            var handle = Addressables.LoadSceneAsync("Scenes/" + name, LoadSceneMode.Additive);
            await handle.Task;
            AdditiveScenePreload.PendingUnloadHandles[name] = handle;
        }
    }

    /// When set, LoadInner restores only the scene + pre-Start save data and holds the rest as PendingSnapshot,
    /// leaving the driver to call ApplyPendingSnapshot at a controlled phase. Off = restore fully inline (default).
    public static bool DeferSnapshotRestore;
    public static Savestate? PendingSnapshot { get; private set; }

    /// The load's timing accumulator, held across the deferred boundary so ApplyPendingSnapshot can record the apply
    /// phase and log the full summary once the restore actually runs.
    private static LoadTiming? pendingTiming;

    public static void ApplyPendingSnapshot() {
        if (PendingSnapshot is not { } savestate) {
            return;
        }

        PendingSnapshot = null;
        var timing = pendingTiming;
        pendingTiming = null;
        timing?.Mark("deferred");
        ApplySnapshot(savestate, timing);
        timing?.LogSummary();
    }

    /// Applies the parts of the snapshot that overwrite already-live objects in place (everything except the scene
    /// and the pre-Start save data restored during the transition).
    private static void ApplySnapshot(Savestate savestate, LoadTiming? timing = null) {
        var applyTotal = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        // Scene-less applies are the `load` command's idle fixture — keep them quiet (see Load/LoadInner).
        var silent = string.IsNullOrEmpty(savestate.Scene);
        // Restoring a snapshot teleports bodies, which makes Box2D fire phantom OnCollision/OnTrigger enter/exit
        // edges on the first step after the load (a hero placed on ground it wasn't touching "lands", etc.) — a
        // divergence vs a continuous run, and the edge handlers can mutate captured state (e.g. a landing setting a
        // SceneData persistentBool). To neutralize both: (1) restore just the position-bearing snapshots so every
        // body sits at its captured position, (2) step physics by 0 so Box2D builds the contact pairs and fires
        // those edges *here*, inside the untraced load window, then (3) restore everything else so whatever the edge
        // handlers touched is overwritten by the snapshot. Net: no phantom edges in playback, byte-identical state.
        if (savestate.ComponentSnapshots != null) {
            foreach (var mb in savestate.ComponentSnapshots) {
                if (ObjectUtils.ComponentTypeName(mb.Path) is "Transform" or "Rigidbody2D") {
                    mb.Restore();
                }
            }
        }

        var prevSimulationMode = Physics2D.simulationMode;
        Physics2D.simulationMode = SimulationMode2D.Script;
        Physics2D.Simulate(0f);
        Physics2D.simulationMode = prevSimulationMode;

        if (savestate.ComponentSnapshots != null) {
            foreach (var mb in savestate.ComponentSnapshots) {
                mb.Restore();
            }

            if (!silent) {
                Log.Info($"- Applied snapshots to scene in {sw.ElapsedMilliseconds}ms");
            }
        }

        if (savestate.GameObjectSnapshots != null) {
            foreach (var go in savestate.GameObjectSnapshots) {
                go.Restore();
            }
        }


        if (savestate.FsmSnapshots != null) {
            sw.Restart();
            foreach (var fsm in savestate.FsmSnapshots) {
                fsm.Restore();
            }

            if (!silent) {
                Log.Info($"- Applied FSM snapshots in {sw.ElapsedMilliseconds}ms");
            }
        }

        if (savestate.AudioTableSnapshots is { Count: > 0 } audioTableSnapshots) {
            // RandomAudioClipTable assets are shared, so re-resolve them from HeroController and match by name.
            var tables = HeroAudioTables(HeroController.instance).ToList();
            foreach (var snap in audioTableSnapshots) {
                var table = tables.FirstOrDefault(t => t.name == snap.Name);
                if (table) {
                    snap.Restore(table);
                } else {
                    Log.Warning($"Savestate audio table '{snap.Name}' not found on HeroController at load time");
                }
            }
        }

        // Restore the global RNG state. The scene reload during the load draws from UnityEngine.Random (and would
        // otherwise leave it reseeded/default), so without this a resumed run diverges from a continuous one even
        // though the snapshot captured it. Must run after the scene load, which itself consumes RNG.
        if (savestate.RandomState is { } randomState) {
            UnityEngine.Random.state = randomState;
        }

        // Restore the LateFixedUpdate cycle counter (private setter → reflection) so the restored FixedUpdateCache
        // instances are consistent with it. Without this the global counter keeps its current (session-age-dependent)
        // value, leaving every captured cache's lastUpdate stale and making the next savestate non-reproducible.
        if (savestate.FixedUpdateCycle is { } fixedUpdateCycle) {
            typeof(CustomPlayerLoop).SetPropertyValue("FixedUpdateCycle", fixedUpdateCycle);
        }

        // Override FinishedEnteringScene's non-deterministic hazard-respawn re-derivation with the captured value
        // (ApplySnapshot runs after scene entry in both load paths).
        savestate.HazardRespawn?.Restore();

        // The HUD canvas persists across the scene reload, so its health_display FSMs still show the pre-load HP —
        // re-init them against the now-restored PlayerData.health. Silent (no break sound / DAMAGE TAKEN), so safe
        // for a traced resume too. Gated on a non-empty scene because that's exactly when PlayerData (and thus
        // health) was restored — the scene-load path is the only one that overwrites it; the scene-less idle-fixture
        // apply leaves PlayerData untouched, so there's nothing for the HUD to catch up to.
        if (!string.IsNullOrEmpty(savestate.Scene)) {
            HudFixes.RefreshHealthHud();
        }

        timing?.Add("apply", applyTotal.ElapsedMilliseconds);
    }

    /*
    public static async Task Load(Savestate savestate, SavestateLoadMode loadMode) {
        try {
            DebugModPlusInterop.IsLoadingSavestate = true;
            await LoadInner(savestate, loadMode);
        } finally {
            DebugModPlusInterop.IsLoadingSavestate = false;
        }
    }

    private static async Task LoadInner(Savestate savestate, SavestateLoadMode loadMode) {
        if (!GameCore.IsAvailable()) {
            throw new Exception("Attempted to load savestate outside of scene");
        }

        if (savestate.LastTeleportId != null) {
            var tp = GameFlagManager.Instance.GetTeleportPointWithPath(savestate.LastTeleportId);
            ApplicationCore.Instance.lastSaveTeleportPoint = tp;
        }


        var sw = Stopwatch.StartNew();

        // Load flags
        sw.Start();
        if (savestate.Flags is { } flags) {
            FlagLogic.LoadFlags(flags, SaveManager.Instance.allFlags);
            Log.Debug($"- Applied flags in {sw.ElapsedMilliseconds}ms");

            SaveManager.Instance.allFlags.AllFlagInitStartAndEquip();
        }

        // Close dialogue
        if (DialoguePlayer.Instance.CanSkip) DialoguePlayer.Instance.ForceClose();

        // Change scene
        var isCurrentScene = savestate.Scene == (GameCore.Instance.gameLevel is { } x ? x.SceneName : null);
        if (savestate.Scene != null) {
            if ((savestate.Scene != null && !isCurrentScene) || loadMode.HasFlag(SavestateLoadMode.ReloadScene)) {
                if (savestate.PlayerPosition is not { } playerPosition) {
                    throw new Exception("Savestate with scene must have `playerPosition`");
                }

                sw.Restart();
                var task = ChangeSceneAsync(new SceneConnectionPoint.ChangeSceneData {
                    sceneName = savestate.Scene,
                    playerSpawnPosition = () => playerPosition,
                });
                if (await Task.WhenAny(task, Task.Delay(5000)) != task) {
                    ToastManager.Toast("Savestate was not loaded after 5s, aborting");
                    return;
                }

                Log.Info($"- Change scene in {sw.ElapsedMilliseconds}ms");
            }
        } else {
            if (savestate.PlayerPosition is { } playerPosition) {
                Player.i.transform.position = playerPosition;
            }
        }

        if (loadMode.HasFlag(SavestateLoadMode.ResetScene)) {
            GameCore.Instance.ResetLevel();
        }

        sw.Restart();
        if (savestate.MonobehaviourSnapshots != null) {
            foreach (var mb in savestate.MonobehaviourSnapshots) {
                mb.Restore();
            }

            Log.Info($"- Applied snapshots to scene in {sw.ElapsedMilliseconds}ms");
        }

        sw.Stop();

        foreach (var go in savestate.GameObjectSnapshots ?? []) {
            go.Restore();
        }

        foreach (var fsm in savestate.FsmSnapshots ?? new List<MonsterLoveFsmSnapshot>()) {
            var targetGo = ObjectUtils.LookupPath(fsm.Path);
            if (targetGo == null) {
                Log.Error($"Savestate stored monsterlove fsm state on {fsm.Path}, which does not exist at load time");
                continue;
            }

            var runner = targetGo.GetComponent<FSMStateMachineRunner>();
            if (!runner) {
                Log.Error($"Savestate stored monsterlove fsm state on {fsm.Path}, which has no FSMStateMachineRunner");
                continue;
            }

            foreach (var machine in runner.GetMachines()) {
                var stateObj = Enum.ToObject(machine.CurrentStateMap.stateObj.GetType(), fsm.CurrentState);

                EnterStateDirectly(machine, stateObj);
            }
        }

        foreach (var fsm in savestate.GeneralFsmSnapshots ?? new List<GeneralFsmSnapshot>()) {
            var targetGo = ObjectUtils.LookupPath(fsm.Path);
            if (targetGo == null) {
                Log.Error($"Savestate stored general fsm state on {fsm.Path}, which does not exist at load time");
                continue;
            }

            var owner = targetGo.GetComponent<StateMachineOwner>();
            if (!owner) {
                Log.Error($"Savestate stored general fsm state on {fsm.Path}, which has no FSMStateMachineRunner");
                continue;
            }

            var state = owner.FsmContext.States.FirstOrDefault(state => state.name == fsm.CurrentState);
            if (!state) {
                Log.Error($"State {fsm.CurrentState} does not exist on {fsm.Path}");
                continue;
            }

            try {
                owner.FsmContext.ChangeState(state);
            } catch (Exception e) {
                Log.Error($"Could not apply fsm state on {owner.FsmContext}/{owner.FsmContext.fsm} {e}");
            }
        }

        // CameraManager.Instance.camera2D.MoveCameraInstantlyToPosition(Player.i.transform.position);
        // hacks
        Player.i.playerInput.RevokeAllMyVote(Player.i.PlayerDeadState);
        Tween.StopAll(); // should restore as well
        foreach (var bossArea in Object.FindObjectsOfType<BossArea>()) {
            bossArea.ForceShowHP();
        }

        foreach (var monster in MonsterManager.Instance.monsterDict.Values) {
            monster.postureSystem.ShowHpViewCheck();
        }

        var votes = Player.i.playerInput.GetFieldValue<List<RuntimeConditionVote>>("conditionVoteList")!;
        foreach (var vote in votes) {
            vote.votes.Clear();
            vote.ManualUpdate();
        }

        if (savestate.RandomState is { } randomState) {
            UnityEngine.Random.state = randomState;
        }

        Player.i.UpdateSpriteFacing();
    }

    private static void EnterStateDirectly(IStateMachine sm, object stateObj) {
        // TODO: handle transitions

        var engine = sm.GetFieldValue<FSMStateMachineRunner>("engine")!;
        var stateLookup = sm.GetFieldValue<IDictionary>("stateLookup")!;
        if (!stateLookup.Contains(stateObj)) {
            throw new Exception($"state {stateObj} not found in fsm");
        }

        var newStateMapping = stateLookup[stateObj];

        var ty = sm.GetType();
        var queuedChangeField = ty.GetFieldInfo("queuedChange")!;
        var currentTransitionField = ty.GetFieldInfo("currentTransition")!;
        var exitRoutineField = ty.GetFieldInfo("exitRoutine")!;
        var enterRoutineField = ty.GetFieldInfo("enterRoutine")!;
        var lastStateField = ty.GetFieldInfo("lastState")!;
        var currentStateField = ty.GetFieldInfo("currentState")!;
        var isInTransitionField = ty.GetFieldInfo("isInTransition")!;

        if (queuedChangeField.GetValue(sm) is IEnumerator queuedChange) {
            engine.StopCoroutine(queuedChange);
            queuedChangeField.SetValue(sm, null);
        }

        if (currentTransitionField.GetValue(sm) is IEnumerator currentTransition) {
            engine.StopCoroutine(currentTransition);
            currentTransitionField.SetValue(sm, null);
        }

        if (exitRoutineField.GetValue(sm) is IEnumerator exitRoutine) {
            engine.StopCoroutine(exitRoutine);
            exitRoutineField.SetValue(sm, null);
        }

        if (enterRoutineField.GetValue(sm) is IEnumerator enterRoutine) {
            engine.StopCoroutine(enterRoutine);
            enterRoutineField.SetValue(sm, null);
        }

        lastStateField.SetValue(sm, newStateMapping);
        currentStateField.SetValue(sm, newStateMapping);
        isInTransitionField.SetValue(sm, false);
    }


    private static Task ChangeSceneAsync(SceneConnectionPoint.ChangeSceneData changeSceneData, bool showTip = false) {
        var completion = new TaskCompletionSource<object?>();
        changeSceneData.ChangedDoneEvent = () => completion.SetResult(null);
        GameCore.Instance.ChangeSceneCompat(changeSceneData, showTip);

        return completion.Task;
    }

*/
}
