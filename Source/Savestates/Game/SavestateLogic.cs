using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DevUtils.Toasts;
using GlobalEnums;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using Newtonsoft.Json.Linq;
using PreciseSavestates.Savestates.Snapshot;
using PreciseSavestates.Source;
using PreciseSavestates.Source.Savestates.Snapshot;
using PreciseSavestates.Utils;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using Component = UnityEngine.Component;
using Random = UnityEngine.Random;

namespace PreciseSavestates.Savestates.Game;

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
            AddComponent(player.transform, "player.transform");
            AddComponent(player.GetFieldValue<Rigidbody2D>("rb2d"), "player.rb2d");
            AddComponent(player.GetComponent<MeshRenderer>(), "player.MeshRenderer");
            gameObjectSnapshots.Add(GameObjectSnapshot.Of(player.gameObject)); // restore layer
            SnapshotSerializer.SnapshotRecursive(player, sceneBehaviours, seen, 0);
            AddComponent(player.AnimCtrl, "player.AnimCtrl");

            foreach (var fsm in player.GetComponents<PlayMakerFSM>()) {
                fsmSnapshots.Add(PlayMakerFsmSnapshot.Of(fsm));
            }

            // Scriptable objects, survives scene load
            audioTableSnapshots.AddRange(HeroAudioTables(player).Select(RandomAudioTableSnapshot.Of));

            var cameraCtrl = GameManager.SilentInstance.cameraCtrl;
            if (cameraCtrl) {
                AddComponent(cameraCtrl, "cameraCtrl");
                var mainCamera = GameCameras.instance;
                AddComponent(mainCamera.transform, "mainCamera.transform");

                if (cameraCtrl.camTarget is { } camTarget) {
                    AddComponent(camTarget, "cameraCtrl.camTarget");
                    AddComponent(camTarget.transform, "cameraCtrl.camTarget.transform");
                }
            }
        }

        if (filter.HasFlag(SavestateFilter.Enemies)) {
            var activeHealthManagers = typeof(HealthManager).GetFieldValue<List<HealthManager>>("_activeHealthManagers");
            if (activeHealthManagers != null) {
                foreach (var hm in activeHealthManagers) {
                    if (!hm) {
                        continue;
                    }

                    AddComponent(hm, "hm");
                    AddComponent(hm.transform, "hm.transform");
                    AddComponent(hm.GetComponent<MeshRenderer>(), "hm.MeshRenderer");
                    AddComponent(hm.GetComponent<Rigidbody2D>(), "hm.Rigidbody2D");
                }
            }

            // FSM-anchored snapshots
            var additiveScenes = CurrentAdditiveScenes();
            var sceneNames = new HashSet<string> { gm.sceneName };
            sceneNames.UnionWith(additiveScenes);
            
            var seenFsmObjects = new HashSet<GameObject>();
            var gameObjectTargets = new HashSet<GameObject>();
            foreach (var fsm in PlayMakerFSM.FsmList) {
                if (!fsm || !sceneNames.Contains(fsm.gameObject.scene.name)) {
                    continue;
                }

                fsmSnapshots.Add(PlayMakerFsmSnapshot.Of(fsm));

                var go = fsm.gameObject;
                gameObjectTargets.Add(go);

                // Only emit game object snapshots for objects that might be toggled
                foreach (var state in fsm.Fsm.States) {
                    foreach (var action in state.Actions) {
                        var ownerDefault = action switch {
                            ActivateGameObject a => a.gameObject,
                            ActivateGameObjectDelay a => a.gameObject,
                            _ => null,
                        };
                        if (action is SetMeshRenderer smr) {
                            var smrTarget = action.Fsm.GetOwnerDefaultTarget(smr.gameObject);
                            if (smrTarget) {
                                AddComponent(smrTarget.GetComponent<MeshRenderer>(), "SetMeshRenderer target");
                            }
                        }
                        if (ownerDefault == null) {
                            continue;
                        }

                        var target = fsm.Fsm.GetOwnerDefaultTarget(ownerDefault);
                        if (target) {
                            gameObjectTargets.Add(target);
                        }
                    }
                }

                if (!seenFsmObjects.Add(go)) {
                    continue;
                }

                foreach (var collider in go.GetComponents<Collider2D>()) {
                    AddComponent(collider, "fsm collider");
                }

                if (go.GetComponent<tk2dSpriteAnimator>() is { } spriteAnimator) {
                    AddComponent(spriteAnimator, "fsm tk2dSpriteAnimator");
                }
            }

            foreach (var target in gameObjectTargets) {
                gameObjectSnapshots.Add(GameObjectSnapshot.Of(target));
            }

            // extra component snapshots
            foreach (var battleScene in UnityEngine.Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Include,
                         FindObjectsSortMode.None)) {
                if (!battleScene) {
                    continue;
                }

                AddComponent(battleScene, "battleScene");
            }
        }

        JToken? playerData = null;
        if (filter.HasFlag(SavestateFilter.Player)) {
            // excluded from HeroController, since it needs pre-scene load init
            playerData = JToken.Parse(JsonUtility.ToJson(PlayerData.instance));
        }

        JToken? sceneData = null;
        if (filter.HasFlag(SavestateFilter.Scene)) {
            gm.SaveLevelState();
            // JsonUtility for ISerializationCallbackReceiver
            sceneData = JToken.Parse(JsonUtility.ToJson(SceneData.instance));
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
            HazardRespawn = HazardRespawnSnapshot.Of(PlayerData.instance),
            PlayerData = playerData,
            SceneData = sceneData,
        };

        return savestate;

        void AddComponent(Component? component, string label) {
            if (!component) {
                Log.Warning($"Skipping missing component for savestate: {label}");
                return;
            }

            if (seen.Add(component)) {
                sceneBehaviours.Add(ComponentSnapshot.Of(component));
            }
        }
    }

    private static void CanonicalizeSerializedLists(JToken token) {
        foreach (var list in token.SelectTokens("$..serializedList").OfType<JArray>().ToList()) {
            var sorted = new JArray(list.OfType<JObject>()
                .OrderBy(e => (string?)e["SceneName"], StringComparer.Ordinal)
                .ThenBy(e => (string?)e["ID"], StringComparer.Ordinal)
                .Select(e => e.DeepClone()));
            list.Replace(sorted);
        }
    }

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

    public static bool IsLoadingSavestate;
    private static TaskCompletionSource<bool>? sceneLoadedSource;

    public static async Task Load(Savestate savestate, SavestateLoadMode loadMode) {
        var total = Stopwatch.StartNew();
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
        Log.Info($"Loading savestate... (scene='{savestate.Scene}', mode={loadMode})");

        var timing = new LoadTiming();
        var gm = GameManager.instance;
        if (gm.GameState != GameState.PLAYING) {
            throw new Exception($"Can't load savestate in state {gm.GameState}");
        }

        // Cancel in-flight death/respawn/invuln coroutines
        LoadCoroutineCleanup.Run();

        var sw = Stopwatch.StartNew();
        if (!string.IsNullOrEmpty(savestate.Scene)) {
            // TODO: is this still necessary here? check SceneAdditiveLoadConditional encounteredSongGolem
            if (savestate.PlayerData is { } earlyPlayerData) {
                JsonUtility.FromJsonOverwrite(earlyPlayerData.ToString(), global::PlayerData.instance);
            }

            // Unload additive scenes, for same-scene reloads (wouldn't be unloaded by vanilla)
            // TODO: reuse more game code? 
            // TODO: don't leak addressables bundle ref
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
            
            // Skip full GC (40% of reload time)
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

                // TODO: reevaluate
                SceneAdditiveLoadConditional.LoadInSequence = false;

                await PreloadAdditiveScenes(savestate.AdditiveScenes);
            } finally {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                GCManager.DisabledManualCollect = wasManualCollectDisabled;
            }

            timing.Mark("enterTail");
            foreach (var (phase, ms) in ScenePhaseTiming.Durations()) {
                timing.Add($"scene.{phase}", ms);
            }

            Log.Info($"- Loaded scene in {sw.ElapsedMilliseconds}ms");
        }

        if (DeferSnapshotRestore) {
            PendingSnapshot = savestate;
            pendingTiming = timing;
        } else {
            ApplySnapshot(savestate, timing);
            if (!silent) {
                timing.LogSummary();
            }
        }

        return;

        // Load player/scene data before persistent objects may read them
        void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if (scene.name != savestate.Scene) {
                return;
            }

            SceneManager.sceneLoaded -= OnSceneLoaded;

            if (savestate.PlayerData is { } playerData) {
                JsonUtility.FromJsonOverwrite(playerData.ToString(), PlayerData.instance);
            }

            if (savestate.SceneData is { } sceneData) {
                JsonUtility.FromJsonOverwrite(sceneData.ToString(), SceneData.instance);
            }

            timing.Mark("sceneAssets");
            Log.Info($"- Restored PlayerData/SceneData for scene '{scene.name}' (before persistent objects' Start)");
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

    // Normally, additive scenes only run on script update through SceneAdditiveLoadConditional.
    // Force them to run synchronously inside the savestate load
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
    /// leaving the driver to call ApplyPendingSnapshot later time. Off = restore fully inline (default).
    public static bool DeferSnapshotRestore = false;
    public static Savestate? PendingSnapshot { get; private set; }
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

    private static void ApplySnapshot(Savestate savestate, LoadTiming? timing = null) {
        var applyTotal = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        var containsScene = !string.IsNullOrEmpty(savestate.Scene);
        if (savestate.ComponentSnapshots != null) {
            foreach (var mb in savestate.ComponentSnapshots) {
                mb.Restore();
            }
            
            // In order to make collision trigger enter/exit events stable, load the rigidbody positions first and perform
            // a Physics.Simulate step in place.
            var prevSimulationMode = Physics2D.simulationMode;
            Physics2D.simulationMode = SimulationMode2D.Script;
            Physics2D.Simulate(0f);
            Physics2D.simulationMode = prevSimulationMode;
            
            Log.Info($"- Applied snapshots in {sw.ElapsedMilliseconds}ms");
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

            if (containsScene) {
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

        if (savestate.RandomState is { } randomState) {
            Random.state = randomState;
        }

        if (savestate.FixedUpdateCycle is { } fixedUpdateCycle) {
            typeof(CustomPlayerLoop).SetPropertyValue("FixedUpdateCycle", fixedUpdateCycle);
        }

        // must run after scene entry
        savestate.HazardRespawn?.Restore();

        if (!string.IsNullOrEmpty(savestate.Scene)) {
            HudFixes.RefreshHealthHud();
        }

        timing?.Add("apply", applyTotal.ElapsedMilliseconds);
    }
}
