using System;
using System.Threading.Tasks;
using GlobalEnums;
using HarmonyLib;
using PreciseSavestates.Source;

namespace PreciseSavestates.DebugSave;

/// A save profile that lives only in memory, never written to disk.
public static class DebugSaveSlot {
    public const int Slot = -1;

    internal static TaskCompletionSource<bool>? entering;

    /// Starts the game from the main menu with <paramref name="data"/>, up to the point where it would transition
    /// to the respawn scene. Returns in GameState.LOADING, ready for a BeginSceneTransition.
    public static async Task Enter(SaveGameData data) {
        var gm = GameManager.instance;
        gm.SetLoadedGameData(data, Slot);

        // RunContinueGame's menu teardown, minus its 2.6s of waiting
        gm.ui.FadeScreenOut();
        gm.noMusicSnapshot.TransitionToSafe(0f);
        gm.noAtmosSnapshot.TransitionToSafe(0f);
        gm.ui.FadeOutBlackThreadLoop();
        gm.audioManager.ApplyMusicCue(gm.noMusicCue, 0f, 0f, applySnapshot: false);
        gm.ui.MakeMenuLean();

        entering = new TaskCompletionSource<bool>();
        gm.StartCoroutine(gm.RunContinueGame(fromMenu: false));
        await entering.Task;
    }
}

[HarmonyPatch]
internal static class DebugSaveSlotPatches {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ReadyForRespawn))]
    private static bool ReadyForRespawn() {
        if (DebugSaveSlot.entering is not { } entering) {
            return true;
        }

        DebugSaveSlot.entering = null;
        entering.SetResult(true);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.SaveGame),
        [typeof(int), typeof(Action<bool>), typeof(bool), typeof(AutoSaveName)])]
    private static bool SaveGame(int saveSlot, Action<bool>? ogCallback) {
        if (saveSlot != DebugSaveSlot.Slot) {
            return true;
        }

        Log.Info("Skipping save to the debug save slot");
        ogCallback?.Invoke(true);
        return false;
    }
}
