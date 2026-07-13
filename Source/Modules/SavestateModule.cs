using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Configuration;
using DevUtils.Toasts;
using PreciseSavestates.Savestates;
using PreciseSavestates.Savestates.Game;
using PreciseSavestates.Source;
using PreciseSavestates.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PreciseSavestates.Modules;

public class SavestateModule(
    // ConfigEntry<SavestateFilter> currentFilter,
    // ConfigEntry<SavestateLoadMode> loadMode,
    ConfigEntry<KeyboardShortcut> openSave,
    ConfigEntry<KeyboardShortcut> openLoad,
    ConfigEntry<KeyboardShortcut> openDelete,
    ConfigEntry<KeyboardShortcut> tabNext,
    ConfigEntry<KeyboardShortcut> tabPrev,
    ConfigEntry<KeyboardShortcut> quickSave,
    ConfigEntry<KeyboardShortcut> quickLoad
) {
    private static readonly SavestateFilter currentFilter = SavestateFilter.All;
    private static readonly SavestateLoadMode loadMode = SavestateLoadMode.ReloadScene;

    private const string SavestateLayerMain = "main";
    private const string SavestateLayerSecondary = "secondary";

    private const string SavestateLayerQuick = "quicksave";
    private const string SavestateSlotQuick = "quick";

    private readonly SavestateStore savestates = new();

    public bool CreateSavestate(string name, string slot, string? layer = null, SavestateFilter? filter = null) {
        try {
            var sw = Stopwatch.StartNew();
            var savestate = SavestateLogic.Create(filter ?? currentFilter);
            savestates.Save(name, savestate, slot, layer);
            Log.Info($"Created savestate {name} in {sw.ElapsedMilliseconds}ms");

            ToastManager.Toast($"Savestate {name} created");
            return true;
        } catch (Exception e) {
            ToastManager.Toast($"Failed to create savestate: {e}");
            return false;
        }
    }

    public static bool IsLoadingSavestate;

    public static float? LastLoadedGameTime;
    public static int? LastLoadedFrameCount;

    /// RandomState of the last-applied snapshot. The driver re-applies this at the load→playback edge to wipe
    /// load-window RNG churn back to the value the load intended — the captured RNG for a real resume, the idle
    /// fixture's (InitState-seed) RNG for a `load` command — instead of a blanket re-seed that would clobber a
    /// resume's restored RNG.
    public static UnityEngine.Random.State? LastLoadedRandomState;

    /// Loads the (first) savestate stored in the given slot/layer. Returns whether one was found and loaded.
    public async Task<bool> LoadSavestate(string? slot = null, string? layer = null) {
        var bySlot = savestates.List(slot, layer).ToList();
        switch (bySlot.Count) {
            case 0: return false;
            case > 1:
                Log.Warning($"Multiple savestates found at slot {slot}, picking {bySlot[0].FullName}");
                break;
        }

        if (!SavestateStore.TryGetValue(bySlot[0], out var savestate)) {
            return false;
        }

        return await LoadSavestate(savestate);
    }

    /// Whether a savestate is stored in the given slot/layer.
    public bool HasSavestate(string? slot = null, string? layer = null) {
        return savestates.List(slot, layer).Any();
    }

    /// Deletes the savestate(s) in the given slot/layer.
    public void DeleteSavestate(string? slot = null, string? layer = null) {
        savestates.Delete(slot, layer);
    }

    /// All distinct slots stored in the given layer.
    public string[] ListSlots(string? layer = null) {
        return savestates.List(layer: layer).Select(info => info.Slot).Distinct().ToArray();
    }

    /// Create a savestate and write it directly to an absolute file path, bypassing the slot/layer store. Lets a
    /// test corpus ship a self-contained baseline fixture instead of depending on machine-local slots.
    public bool CreateSavestateToFile(string path, SavestateFilter? filter = null) {
        try {
            var savestate = SavestateLogic.Create(filter ?? currentFilter);
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) {
                Directory.CreateDirectory(dir);
            }

            using var writer = new StreamWriter(path);
            savestate.SerializeTo(writer);
            Log.Info($"Created savestate file {path}");
            return true;
        } catch (Exception e) {
            ToastManager.Toast($"Failed to create savestate file: {e}");
            return false;
        }
    }

    /// Load a savestate directly from an absolute file path, bypassing the slot/layer store.
    public async Task<bool> LoadSavestateFromFile(string path) {
        if (!File.Exists(path)) {
            Log.Error($"Savestate file not found: {path}");
            return false;
        }

        Savestate savestate;
        using (var reader = new StreamReader(path)) {
            savestate = Savestate.DeserializeFrom(reader);
        }

        return await LoadSavestate(savestate);
    }

    public async Task<bool> LoadSavestate(Savestate savestate) {
        // Loading is exclusive. Reject not only while a load runs, but also while a deferred snapshot is still pending:
        // with DeferSnapshotRestore the component/FSM/RNG restore is held until the driver applies it, and
        // IsLoadingSavestate is already false in that window. A reentrant load there overlaps scene transitions and
        // leaves stale entries in SceneAdditiveLoadConditional's loader list, which NREs the next transition and
        // strands the game mid-load.
        if (IsLoadingSavestate || SavestateLogic.PendingSnapshot != null) {
            Log.Error("Attempted to load savestate while another load is in progress");
            return false;
        }

        try {
            IsLoadingSavestate = true;

            await SavestateLogic.Load(savestate, loadMode);
            LastLoadedGameTime = savestate.GameTime;
            LastLoadedFrameCount = savestate.GameFrameCount;
            LastLoadedRandomState = savestate.RandomState;
            return true;
        } catch (Exception e) {
            ToastManager.Toast($"Failed to load savestate: {e}");
            return false;
        } finally {
            IsLoadingSavestate = false;
        }
    }

    private void QuickSave() {
        var scene = SceneManager.GetActiveScene().name;
        CreateSavestate(scene, SavestateSlotQuick, SavestateLayerQuick);
    }

    private async void QuickLoad() {
        try {
            if (!HasSavestate(SavestateSlotQuick, SavestateLayerQuick)) {
                ToastManager.Toast("No quicksave to load");
                return;
            }

            var sw = Stopwatch.StartNew();
            await LoadSavestate(SavestateSlotQuick, SavestateLayerQuick);
            Log.Info($"Loaded quicksave in {sw.ElapsedMilliseconds}ms");
        } catch (Exception e) {
            ToastManager.Toast(e);
        }
    }

    #region UI

    private void UiSaveToSlot(int slot) {
        // The savestate UI pauses the game (Pause() sets acceptingInput=false), so restore it before snapshotting
        // so the captured field reflects the in-game value. Only needed for the UI path — CreateSavestate itself
        // must not mutate game state (it's also called mid-TAS, where AcceptInput would perturb playback).
        HeroController.instance.AcceptInput();

        var scene = SceneManager.GetActiveScene().name;
        var defaultName = scene;
        CreateSavestate(defaultName, slot.ToString(), savestateLayer);
    }

    private async void UiLoadFromSlot(int slot) {
        try {
            var bySlot = savestates.List(slot.ToString(), savestateLayer).ToList();
            switch (bySlot.Count) {
                case 0:
                    ToastManager.Toast($"Savestate '{slot}' not found");
                    return;
                case > 1:
                    ToastManager.Toast($"Multiple savestates found at slot {slot}, picking {bySlot[0].FullName}");
                    break;
            }

            if (!SavestateStore.TryGetValue(bySlot[0], out var savestate)) {
                return;
            }

            var sw = Stopwatch.StartNew();
            await LoadSavestate(savestate);
            Log.Info(
                $"Loaded savestate {slot} in {sw.ElapsedMilliseconds}ms");
        } catch (Exception e) {
            ToastManager.Toast(e);
        }
    }

    private enum SavestateUIState {
        Off,
        Save,
        Load,
        Delete,
    }


    private string savestateLayer = SavestateLayerMain;

    private SavestateUIState uiState = SavestateUIState.Off;

    private SavestateUIState UiState {
        get => uiState;
        set {
            uiState = value;
            if (uiState == SavestateUIState.Off) {
                Time.timeScale = 1;
                HeroController.instance.UnPause();
            } else {
                Time.timeScale = 0;
                HeroController.instance.Pause();
            }
        }
    }

    private readonly Dictionary<int, SavestateInfo> infos = [];

    private void LoadInfos() {
        infos.Clear();
        foreach (var info in savestates.List(layer: savestateLayer)) {
            if (info.Index is not { } index) {
                continue;
            }

            infos.TryAdd(index, info);
        }
    }

    private static string SavestateLayerFromModifiers() {
        return Input.GetKey(KeyCode.LeftControl) | Input.GetKey(KeyCode.LeftControl)
            ? SavestateLayerSecondary
            : SavestateLayerMain;
    }

    private void UpdateLayer() {
        var newLayer = SavestateLayerFromModifiers();
        if (newLayer != savestateLayer) {
            currentPage = 0;
        }

        savestateLayer = newLayer;
    }

    public void Update() {
        try {
            if (KeybindManager.CheckShortcutOnly(quickSave.Value)) {
                QuickSave();
            } else if (KeybindManager.CheckShortcutOnly(quickLoad.Value)) {
                QuickLoad();
            }

            if (KeybindManager.CheckShortcutOnly(openLoad.Value)) {
                UiState = UiState == SavestateUIState.Load ? SavestateUIState.Off : SavestateUIState.Load;
                UpdateLayer();
            } else if (KeybindManager.CheckShortcutOnly(openSave.Value)) {
                UiState = UiState == SavestateUIState.Save ? SavestateUIState.Off : SavestateUIState.Save;
                UpdateLayer();
            } else if (KeybindManager.CheckShortcutOnly(openDelete.Value)) {
                UiState = UiState == SavestateUIState.Delete ? SavestateUIState.Off : SavestateUIState.Delete;
                UpdateLayer();
            } else if (UiState != SavestateUIState.Off && KeybindManager.CheckShortcutOnly(tabNext.Value)) {
                currentPage++;
            } else if (UiState != SavestateUIState.Off && KeybindManager.CheckShortcutOnly(tabPrev.Value)) {
                currentPage = Math.Max(currentPage - 1, 0);
            }

            if (UiState != SavestateUIState.Off) {
                LoadInfos();
            }

            if (UiState != SavestateUIState.Off) {
                if (Input.GetKeyDown(KeyCode.Escape)) {
                    UiState = SavestateUIState.Off;
                }

                for (var i = 0; i < 10; i++) {
                    if (Input.GetKeyDown(KeyCode.Alpha0 + i)
                        || Input.GetKeyDown(KeyCode.Keypad0 + i)) {
                        var saveIndex = currentPage * ItemsPerPage + i;

                        if (UiState == SavestateUIState.Save) {
                            UiSaveToSlot(saveIndex);
                            UiState = SavestateUIState.Off;
                        } else if (UiState == SavestateUIState.Load) {
                            UiLoadFromSlot(saveIndex);
                            UiState = SavestateUIState.Off;
                        } else if (UiState == SavestateUIState.Delete) {
                            savestates.Delete(saveIndex.ToString(), savestateLayer);
                            UiState = SavestateUIState.Off;
                        }
                    }
                }
            }
        } catch (Exception e) {
            ToastManager.Toast(e);
        }
    }

    private const int ItemsPerPage = 10;
    private int currentPage;

    public void OnGui() {
        style ??= new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = false };
        styleBox ??= new GUIStyle(GUI.skin.box) { fontSize = 18 };

        if (UiState != SavestateUIState.Off) {
            var maxSlotIndex = infos.Count > 0 ? infos.Max(kv => kv.Key) : 0;
            var totalPages = Math.Max(Mathf.CeilToInt((float)maxSlotIndex / ItemsPerPage), currentPage + 1);

            const int itemHeight = 27;
            // var visibleItems = Mathf.Min(ItemsPerPage, infos.Count);
            var visibleItems = ItemsPerPage;
            var boxHeight = (visibleItems + 2) * itemHeight;

            const int boxWidth = 450;
            const int boxInset = 10;
            var boxX = Screen.width / 2 - boxWidth / 2;
            const int boxY = 50;

            var boxRect = new Rect(boxX, boxY, boxWidth, boxHeight);
            var layer = savestateLayer != "main" ? $" {savestateLayer}" : "";
            GUI.Box(boxRect, $"Page {currentPage + 1}/{totalPages}{layer} ({UiState})", styleBox);

            // Display Items Dynamically
            GUILayout.BeginArea(new Rect(boxX + boxInset, boxY + 25, boxWidth - boxInset * 2, boxHeight));
            for (var i = 0; i < 10; i++) {
                var index = currentPage * ItemsPerPage + i;

                if (infos.TryGetValue(index, out var info)) {
                    GUILayout.Label($"{index}    {info.Name}", style);
                } else {
                    GUILayout.Label($"{index}    (free)", style);
                }
            }

            GUILayout.EndArea();

            /*// Pagination Controls
            if (GUI.Button(new Rect(20, boxHeight + 10, 80, 30), "Prev") && currentPage > 0) {
                currentPage--;
            }

            if (GUI.Button(new Rect(120, boxHeight + 10, 80, 30), "Next") &&
                (currentPage + 1) * itemsPerPage < names.Length) {
                currentPage++;
            }*/
        }
    }

    private GUIStyle? style;
    private GUIStyle? styleBox;

    #endregion
}
