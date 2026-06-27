using System.Threading.Tasks;
using PreciseSavestates.Modules;
using PreciseSavestates.Savestates;

namespace PreciseSavestates;

/// Stable API surface for other mods to drive savestates programmatically,
/// accessed via reflection through the `Interop` field on
/// <see cref="Source.PreciseSavestatesPlugin" />
/// .
public class Interop(SavestateModule module) {
    /// Captures the current game state into the given slot/layer. Returns whether it succeeded.
    /// <paramref name="filter" />
    /// is a
    /// <see cref="SavestateFilter" />
    /// bitmask; pass a negative value for the default.
    public bool CreateSavestate(string name, string slot, string? layer = null, int filter = -1) {
        return module.CreateSavestate(name, slot, layer, filter < 0 ? null : (SavestateFilter)filter);
    }

    /// Loads the savestate in the given slot/layer. The task resolves to whether one was found and loaded.
    public Task<bool> LoadSavestate(string? slot = null, string? layer = null) {
        return module.LoadSavestate(slot, layer);
    }

    public float? LastLoadedGameTime => SavestateModule.LastLoadedGameTime;
    public int? LastLoadedFrameCount => SavestateModule.LastLoadedFrameCount;

    /// Deletes the savestate(s) in the given slot/layer.
    public void DeleteSavestate(string? slot = null, string? layer = null) {
        module.DeleteSavestate(slot, layer);
    }

    /// Whether a savestate exists in the given slot/layer.
    public bool HasSavestate(string? slot = null, string? layer = null) {
        return module.HasSavestate(slot, layer);
    }

    /// All distinct savestate slots stored in the given layer.
    public string[] ListSlots(string? layer = null) {
        return module.ListSlots(layer);
    }
}
