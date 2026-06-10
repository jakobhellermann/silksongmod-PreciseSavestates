using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BepInEx.Configuration;
using DevUtils.Toasts;
using PreciseSavestates.Savestates;
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
    ConfigEntry<KeyboardShortcut> tabPrev
) {
    private static readonly SavestateFilter currentFilter = SavestateFilter.All;
    private static readonly SavestateLoadMode loadMode = SavestateLoadMode.ReloadScene;

    private const string SavestateLayerMain = "main";
    private const string SavestateLayerSecondary = "secondary";

    private readonly SavestateStore savestates = new();

    public bool CreateSavestate(string name, int slot, string layer, SavestateFilter? filter = null) {
        try {
            var sw = Stopwatch.StartNew();
            // Pause() sets acceptingInput=false; restore it before snapshotting so the field captures the in-game value
            HeroController.instance.AcceptInput();
            var savestate = SavestateLogic.Create(filter ?? currentFilter);
            savestates.Save(name, savestate, slot, layer);
            Log.Info($"Created savestate {name} in {sw.ElapsedMilliseconds}ms");

            ToastManager.Toast($"Savestate {name} created");
            return true;
        } catch (Exception e) {
            ToastManager.Toast($"Failed to create savestate: {e.Message}");
            return false;
        }
    }

    public static bool IsLoadingSavestate;

    public async Task<bool> LoadSavestate(Savestate savestate) {
        if (IsLoadingSavestate) {
            Log.Error("Attempted to load savestate while loading savestate");
            return false;
        }

        try {
            IsLoadingSavestate = true;

            await SavestateLogic.Load(savestate, loadMode);
            return true;
        } catch (Exception e) {
            ToastManager.Toast($"Failed to load savestate: {e}");
            return false;
        } finally {
            IsLoadingSavestate = false;
        }
    }

    #region UI

    private void UiSaveToSlot(int slot) {
        var scene = SceneManager.GetActiveScene().name;
        var defaultName = scene;
        CreateSavestate(defaultName, slot, savestateLayer);
    }

    private async void UiLoadFromSlot(int slot) {
        try {
            var bySlot = savestates.List(slot, savestateLayer).ToList();
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
                            savestates.Delete(saveIndex, savestateLayer);
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
