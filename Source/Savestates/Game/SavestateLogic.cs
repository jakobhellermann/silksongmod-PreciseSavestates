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
using TeamCherry.NestedFadeGroup;
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
    Music = 1 << 4,
    All = Player | Scene | Enemies | Music,
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
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.FinishedEnteringScene))]
        private static void FinishedEnteringScene() {
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
        List<CoroutineSnapshot>? coroutineSnapshots = null;
        var audioTableSnapshots = new List<RandomAudioTableSnapshot>();

        if (filter.HasFlag(SavestateFilter.Player)) {
            AddComponent(player.transform, "player.transform");
            AddComponent(player.rb2d, "player.rb2d");
            AddComponent(player.GetComponent<MeshRenderer>(), "player.MeshRenderer");
            gameObjectSnapshots.Add(GameObjectSnapshot.Of(player.gameObject)); // restore layer
            SnapshotSerializer.SnapshotRecursive(player, sceneBehaviours, seen, 0);
            AddComponent(player.AnimCtrl, "player.AnimCtrl");
            // Holds a hit buffered until the next LateFixedUpdate
            AddComponent(player.heroBox, "player.heroBox");
            AddComponent(player.AnimCtrl?.animator, "player animator");

            foreach (var fsm in player.GetComponents<PlayMakerFSM>()) {
                fsmSnapshots.Add(PlayMakerFsmSnapshot.Of(fsm));
            }

            // Scriptable objects, survives scene load
            audioTableSnapshots.AddRange(HeroAudioTables(player).Select(RandomAudioTableSnapshot.Of));

            var cameraCtrl = GameManager.SilentInstance.cameraCtrl;
            if (cameraCtrl) {
                AddComponent(cameraCtrl.transform, "camera transform");
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
            var enemies = SceneComponents<HealthManager>().ToList();
            foreach (var hm in enemies) {
                AddComponent(hm, "hm");
                AddComponent(hm.transform, "hm.transform");
                if (hm.TryGetComponent<MeshRenderer>(out var renderer)) {
                    AddComponent(renderer, "hm.MeshRenderer");
                }
                if (hm.TryGetComponent<Rigidbody2D>(out var rb)) {
                    AddComponent(rb, "hm.Rigidbody2D");
                }
            }

            // FSM-anchored snapshots
            var additiveScenes = CurrentAdditiveScenes();
            var sceneNames = new HashSet<string> { gm.sceneName };
            sceneNames.UnionWith(additiveScenes);
            coroutineSnapshots = CoroutineSnapshot.Of(go => sceneNames.Contains(go.scene.name));
            
            var seenFsmObjects = new HashSet<GameObject>();
            var gameObjectTargets = new HashSet<GameObject>();
            // persistent FSMs driven by scene FSM actions
            var persistentFsms = new HashSet<PlayMakerFSM>();
            foreach (var fsm in PlayMakerFSM.FsmList) {
                if (fsm && sceneNames.Contains(fsm.gameObject.scene.name)) {
                    CaptureFsm(fsm);
                }
            }
            foreach (var fsm in persistentFsms) {
                CaptureFsm(fsm);
            }
            
            // the hero FSMs' states are already captured with the player, this adds the objects they toggle
            if (filter.HasFlag(SavestateFilter.Player)) {
                foreach (var fsm in player.GetComponents<PlayMakerFSM>()) {
                    CaptureFsm(fsm, snapshotState: false);
                }
            }

            void CaptureFsm(PlayMakerFSM fsm, bool snapshotState = true) {
                if (snapshotState) {
                    fsmSnapshots.Add(PlayMakerFsmSnapshot.Of(fsm));
                }

                var go = fsm.gameObject;
                gameObjectTargets.Add(go);

                // Only emit game object snapshots for objects that might be toggled
                foreach (var state in fsm.Fsm.States) {
                    foreach (var action in state.Actions) {
                        var ownerDefault = action switch {
                            ActivateGameObject a => a.gameObject,
                            ActivateGameObjectDelay a => a.gameObject,
                            // the animation whose event it waits for
                            ListenForAnimationEvent a => a.Target,
                            _ => null,
                        };
                        if (action is SetMeshRenderer smr) {
                            var smrTarget = action.Fsm.GetOwnerDefaultTarget(smr.gameObject);
                            if (smrTarget) {
                                AddComponent(smrTarget.GetComponent<MeshRenderer>(), "SetMeshRenderer target");
                            }
                        }
                        if (action is SetSpriteRenderer ssr) {
                            var ssrTarget = action.Fsm.GetOwnerDefaultTarget(ssr.gameObject);
                            if (ssrTarget) {
                                AddComponent(ssrTarget.GetComponent<SpriteRenderer>(), "SetSpriteRenderer target");
                            }
                        }
                        if (action is SetCollider setCollider
                            && action.Fsm.GetOwnerDefaultTarget(setCollider.gameObject) is { } colliderTarget && colliderTarget) {
                            foreach (var collider in colliderTarget.GetComponents<Collider2D>()) {
                                AddComponent(collider, "SetCollider target");
                            }
                        }
                        if (action is CallMethodProper call
                            && action.Fsm.GetOwnerDefaultTarget(call.gameObject) is { } callTarget && callTarget
                            && sceneNames.Contains(callTarget.scene.name)) {
                            gameObjectTargets.Add(callTarget);
                            if (callTarget.GetComponent(call.behaviour.Value) is { } behaviour) {
                                AddComponent(behaviour, "CallMethodProper target");
                            }
                        }
                        if (action is StartRoarEmitter roar) {
                            var emitter = gm.gameCams.transform.Find(roar.isSmall.Value ? "Roar Wave Emitter Small" : "Roar Wave Emitter");
                            persistentFsms.Add(emitter.GetComponent<PlayMakerFSM>());
                            // moved to the spawn point on each roar
                            AddComponent(emitter, "roar emitter transform");
                        }
                        if (FsmTransformTargets.Of(action) is { } moved && moved) {
                            AddComponent(moved.transform, "fsm transform target");
                        }
                        if (action is FadeNestedFadeGroup fade) {
                            var fadeTarget = action.Fsm.GetOwnerDefaultTarget(fade.Target);
                            if (fadeTarget) {
                                NestedFadeGroupBase fadeGroup = fadeTarget.GetComponent<NestedFadeGroup>();
                                if (!fadeGroup) {
                                    fadeGroup = fadeTarget.GetComponent<NestedFadeGroupBase>();
                                }
                                AddComponent(fadeGroup, "FadeNestedFadeGroup target");
                            }
                        }
                        if (action is HutongGames.PlayMaker.Actions.ActivateAllChildren { gameObject.Value: { } parent }) {
                            foreach (Transform child in parent.transform) {
                                gameObjectTargets.Add(child.gameObject);
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
                    return;
                }

                foreach (var collider in go.GetComponents<Collider2D>()) {
                    AddComponent(collider, "fsm collider");
                }

                if (go.GetComponent<tk2dSpriteAnimator>() is { } spriteAnimator) {
                    AddComponent(spriteAnimator, "fsm tk2dSpriteAnimator");
                }
            }

            // extra component snapshots
            foreach (var animator in ActiveCurveAnimators()) {
                var animated = animator.CurrentTransform;
                if (animated.GetComponentInChildren<Collider2D>(true)) {
                    AddComponent(animator, "curve animator");
                    AddComponent(animated, "curve animator transform");
                }
            }

            foreach (var breakable in SceneComponents<Breakable>().Where(b => b.IsBroken)) {
                AddComponent(breakable, "breakable");
                AddComponent(breakable.GetComponent<Collider2D>(), "breakable collider");
                gameObjectTargets.UnionWith(breakable.wholeParts.Where(go => go));
                gameObjectTargets.UnionWith(breakable.remnantParts.Where(go => go));
            }

            foreach (var battleScene in UnityEngine.Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Include,
                         FindObjectsSortMode.None)) {
                if (!battleScene) {
                    continue;
                }

                AddComponent(battleScene, "battleScene");
                // toggled from C#
                gameObjectTargets.UnionWith(new[] {
                    battleScene.camLocks, battleScene.activeBeforeBattle, battleScene.activeDuringBattle, battleScene.activeAfterBattle, battleScene.inactiveAfterBattle
                }.Where(go => go));
            }

            gameObjectTargets.UnionWith(enemies.Select(hm => hm.gameObject));

            // skip prefab assets in FSM GameObject variables
            gameObjectTargets.RemoveWhere(go => !go.scene.IsValid());
            var spawnSources = PlayMakerFSM.FsmList
                .Where(fsm => fsm && sceneNames.Contains(fsm.gameObject.scene.name))
                .SelectMany(CreateObjectSpawn.In)
                .GroupBy(s => s.spawned) // a FSM can spawn one prefab from several actions into the same variable (Gloom Beast's Gloomflies)
                .ToDictionary(g => g.Key, g => SpawnSource.Of(g.Key, g.First().spawn));

            // before the other snapshots, whose paths may go through a reparented object and only resolve once it moved
            foreach (var (detached, reparent) in sceneNames.Select(SceneManager.GetSceneByName).SelectMany(FlingChildrenReparent.In)) {
                gameObjectSnapshots.Add(GameObjectSnapshot.Of(detached, reparent: new ReparentSource { FlingChildren = reparent }));
                gameObjectTargets.Remove(detached);

                AddComponent(detached.transform, "reparented transform");

                foreach (var rb in detached.GetComponentsInChildren<Rigidbody2D>()) {
                    AddComponent(rb, "reparented rigidbody");
                    AddComponent(rb.transform, "reparented rigidbody transform");
                }
            }
            
            gameObjectSnapshots.AddRange(gameObjectTargets.Select(go => GameObjectSnapshot.Of(go, spawnSources.GetValueOrDefault(go))));
            foreach (var ps in gameObjectTargets.Where(go => go.activeInHierarchy).SelectMany(go => go.GetComponentsInChildren<ParticleSystem>())) {
                AddComponent(ps, "particle system");
            }
            foreach (var animator in gameObjectTargets.SelectMany(go => go.GetComponents<Animator>())) {
                if (animator.gameObject.activeInHierarchy) {
                    AddComponent(animator, "animator");
                }
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
            Dialogue = DialogueSnapshot.Of(DialogueBox._instance),
            AudioTableSnapshots = audioTableSnapshots,
            RandomState = Random.state,
            GameTime = Time.time,
            GameFrameCount = Time.frameCount,
            FixedUpdateCycle = CustomPlayerLoop.FixedUpdateCycle,
            HazardRespawn = HazardRespawnSnapshot.Of(PlayerData.instance),
            Music = filter.HasFlag(SavestateFilter.Music) ? MusicSnapshot.Of(gm.AudioManager) : null,
            Coroutines = coroutineSnapshots,
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

    /// <paramref name="rebaseTimes"/>: shift restored absolute timestamps onto the current clock (see AbsoluteTimeRebase).
    public static async Task Load(Savestate savestate, SavestateLoadMode loadMode, bool rebaseTimes = false) {
        var total = Stopwatch.StartNew();
        var silent = string.IsNullOrEmpty(savestate.Scene);
        try {
            IsLoadingSavestate = true;
            await LoadInner(savestate, loadMode, rebaseTimes);
            if (!silent) {
                ToastManager.Toast($"Loaded in {total.ElapsedMilliseconds}ms");
            }
        } finally {
            IsLoadingSavestate = false;
        }
    }

    /// Before a scene transition: no pending scene preload (see LoadCoroutineCleanup), PLAYING, no in-flight death.
    public static async Task PrepareSceneLoad() {
        await LoadCoroutineCleanup.WaitForPendingScenePreloads();
        await LoadCoroutineCleanup.EndPendingScenePreloads();
        var state = GameManager.instance.GameState;
        if (state != GameState.PLAYING) {
            throw new Exception($"Can't load in state {state}");
        }
        LoadCoroutineCleanup.Run();
    }

    private static async Task LoadInner(Savestate savestate, SavestateLoadMode loadMode, bool rebaseTimes) {
        var silent = string.IsNullOrEmpty(savestate.Scene);
        Log.Info($"Loading savestate... (scene='{savestate.Scene}', mode={loadMode})");

        var timing = new LoadTiming();
        var gm = GameManager.instance;
        if (!string.IsNullOrEmpty(savestate.Scene)) {
            await PrepareSceneLoad();
        } else {
            // An in-place apply has no transition to race a preload, so it stays synchronous
            if (gm.GameState != GameState.PLAYING) {
                throw new Exception($"Can't load savestate in state {gm.GameState}");
            }
            LoadCoroutineCleanup.Run();
        }

        var sw = Stopwatch.StartNew();
        if (!string.IsNullOrEmpty(savestate.Scene)) {
            // TODO: is this still necessary here? check SceneAdditiveLoadConditional encounteredSongGolem
            if (savestate.PlayerData is { } earlyPlayerData) {
                JsonUtility.FromJsonOverwrite(earlyPlayerData.ToString(), global::PlayerData.instance);
                InvalidatePlayerDataCaches();
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
                // transitions send this, EnterSceneDreamGate doesn't. Steps lingering idle music.
                EventRegister.SendEvent("ENTERING SCENE");

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
            if (rebaseTimes) {
                throw new InvalidOperationException("rebaseTimes is not supported with DeferSnapshotRestore");
            }
            PendingSnapshot = savestate;
            pendingTiming = timing;
        } else {
            ApplySnapshot(savestate, timing, rebaseTimes);
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
                InvalidatePlayerDataCaches();
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

    // the UpdateBatcher registers one animator per GameObject, which drives all of its siblings
    private static IEnumerable<VectorCurveAnimator> ActiveCurveAnimators() =>
        GameManager.instance.GetComponent<UpdateBatcher>().lateUpdates.List
            .OfType<VectorCurveAnimator>()
            .SelectMany(a => a.GetComponents<VectorCurveAnimator>());

    private static IEnumerable<T> SceneComponents<T>() where T : Component {
        for (var i = 0; i < SceneManager.sceneCount; i++) {
            foreach (var root in SceneManager.GetSceneAt(i).GetRootGameObjects()) {
                foreach (var component in root.GetComponentsInChildren<T>(true)) {
                    yield return component;
                }
            }
        }
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

    private static void ApplySnapshot(Savestate savestate, LoadTiming? timing = null, bool rebaseTimes = false) {
        var applyTotal = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        var containsScene = !string.IsNullOrEmpty(savestate.Scene);
        var restoredFsms = new List<PlayMakerFSM>();
        var restoredHero = false;
        var restoredCurveAnimators = new List<VectorCurveAnimator>();
        
        if (savestate.GameObjectSnapshots != null) {
            foreach (var go in savestate.GameObjectSnapshots) {
                go.Restore();
            }

            // Only a scene savestate captured the scene's enemies; an in-place apply knows none of them
            if (containsScene) {
                var snapshotObjects = savestate.GameObjectSnapshots
                    .Select(go => ObjectUtils.LookupPath(go.Path))
                    .OfType<GameObject>()
                    .ToHashSet();
                foreach (var hm in SceneComponents<HealthManager>()) {
                    if (!snapshotObjects.Contains(hm.gameObject)) {
                        Log.Info($"- Destroying enemy {ObjectUtils.ObjectPath(hm.gameObject)}, killed before savestate");
                        hm.gameObject.SetActive(false);
                        UnityEngine.Object.Destroy(hm.gameObject);
                    }
                }
            }
        }

        if (savestate.ComponentSnapshots != null) {
            foreach (var mb in savestate.ComponentSnapshots) {
                switch (mb.Restore()) {
                    case HeroController:
                        restoredHero = true;
                        break;
                    case VectorCurveAnimator animator:
                        restoredCurveAnimators.Add(animator);
                        break;
                }
            }
            
            // In order to make collision trigger enter/exit events stable, load the rigidbody positions first and perform
            // a Physics.Simulate step in place.
            var prevSimulationMode = Physics2D.simulationMode;
            Physics2D.simulationMode = SimulationMode2D.Script;
            Physics2D.Simulate(0f);
            Physics2D.simulationMode = prevSimulationMode;
            
            Log.Info($"- Applied snapshots in {sw.ElapsedMilliseconds}ms");
        }

        if (savestate.FsmSnapshots != null) {
            sw.Restart();
            DialogueSnapshot.RestoreDuring(savestate.Dialogue, () => {
                foreach (var fsm in savestate.FsmSnapshots) {
                    if (fsm.Restore() is { } restored) {
                        restoredFsms.Add(restored);
                    }
                }
            });

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
            CustomPlayerLoop.FixedUpdateCycle = fixedUpdateCycle;
        }

        // must run after scene entry
        savestate.HazardRespawn?.Restore();

        foreach (var coroutine in savestate.Coroutines ?? []) {
            coroutine.Restore();
        }

        if (!string.IsNullOrEmpty(savestate.Scene)) {
            savestate.Music?.RestoreAfterSceneAudio();
            HudFixes.RefreshHealthHud();
            HudFixes.RefreshSilkHud();
        }

        if (rebaseTimes) {
            if (savestate.GameTime is not { } gameTime) {
                throw new InvalidOperationException("Cannot rebase absolute times: savestate has no GameTime");
            }

            var delta = Time.timeAsDouble - gameTime;
            Log.Info($"- Rebasing absolute times by {delta:F3}s");
            if (restoredHero) {
                AbsoluteTimeRebase.RebaseHero(HeroController.instance, delta);
            }
            AbsoluteTimeRebase.RebaseFsms(restoredFsms, delta);
            AbsoluteTimeRebase.RebaseCurveAnimators(restoredCurveAnimators, delta);
        }

        timing?.Add("apply", applyTotal.ElapsedMilliseconds);
    }

    // clear PlayerData caches
    private static void InvalidatePlayerDataCaches() {
        QuestManager.IncrementVersion();
        CollectableItemManager.IncrementVersion();
        ToolItemManager.IncrementVersion();
    }
}
