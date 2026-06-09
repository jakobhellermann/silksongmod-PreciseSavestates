using BepInEx;

namespace ExampleMod.Source;

// TODO - adjust the plugin guid as needed
[BepInAutoPlugin("io.github.yourgithubusername.examplemod")]
public partial class ExampleModPlugin : BaseUnityPlugin {
    private void Awake() {
        Log.Init(Logger);

        // Put your initialization logic here
        Log.Info($"Plugin {Name} ({Id}) has loaded!");
    }

    private void OnDestroy() {
        // Clean up everything, in order to support hot reloading
        Log.Info($"Plugin {Name} ({Id}) has been unloaded!");
    }
}
