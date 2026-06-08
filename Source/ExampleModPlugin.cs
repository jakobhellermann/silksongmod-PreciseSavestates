using BepInEx;

namespace ExampleMod.Source;

// TODO - adjust the plugin guid as needed
[BepInAutoPlugin("io.github.yourgithubusername.examplemod")]
public partial class ExampleModPlugin : BaseUnityPlugin {
    private void Awake() {
        // Put your initialization logic here
        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");
    }
}
