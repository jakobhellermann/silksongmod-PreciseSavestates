using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using PreciseSavestates.Modules;
using UnityEngine;

namespace PreciseSavestates.Source;

[BepInAutoPlugin("io.github.jakobhellermann.precisesavestates")]
public partial class PreciseSavestatesPlugin : BaseUnityPlugin {
    public static PreciseSavestatesPlugin Instance = null!;

    private Harmony harmony = null!;
    private SavestateModule? savestateModule;

    private void Awake() {
        Instance = this;
        Log.Init(Logger);
        Log.Info($"Plugin {Name} ({Id}) has loaded!");

        try {
            harmony = Harmony.CreateAndPatchAll(GetType().Assembly);

            savestateModule = new SavestateModule(
                Config.Bind("Savestates", "Save", new KeyboardShortcut(KeyCode.KeypadPlus)),
                Config.Bind("Savestates", "Load", new KeyboardShortcut(KeyCode.KeypadEnter)),
                Config.Bind("Savestates", "Delete", new KeyboardShortcut(KeyCode.KeypadMinus)),
                Config.Bind("Savestates", "Page next", new KeyboardShortcut(KeyCode.RightArrow)),
                Config.Bind("Savestates", "Page prev", new KeyboardShortcut(KeyCode.LeftArrow))
            );
        } catch (Exception e) {
            Log.Info($"Plugin {Name} ({Id}) failed to initialize: {e}");
        }
    }

    private void Update() {
        try {
            savestateModule?.Update();
        } catch (Exception e) {
            Log.Error(e);
        }
    }

    private void OnGUI() {
        try {
            savestateModule?.OnGui();
        } catch (Exception e) {
            Log.Error(e);
        }
    }

    private void OnDestroy() {
        // Clean up everything, in order to support hot reloading

        try {
            harmony.UnpatchSelf();
        } catch (Exception e) {
            Log.Info($"Plugin {Name} ({Id}) failed to clean up: {e}");
        }

        Log.Info($"Plugin {Name} ({Id}) has been unloaded!");
    }
}
