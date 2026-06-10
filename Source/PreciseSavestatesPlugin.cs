using System;
using BepInEx;
using HarmonyLib;

namespace PreciseSavestates.Source;

[BepInAutoPlugin("io.github.jakobhellermann.precisesavestates")]
public partial class PreciseSavestatesPlugin : BaseUnityPlugin {
    private Harmony harmony = null!;

    private void Awake() {
        Log.Init(Logger);
        Log.Info($"Plugin {Name} ({Id}) has loaded!");

        try {
            harmony = Harmony.CreateAndPatchAll(GetType().Assembly);
        } catch (Exception e) {
            Log.Info($"Plugin {Name} ({Id}) failed to initialize: {e}");
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
