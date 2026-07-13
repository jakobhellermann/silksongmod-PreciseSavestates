using System.Threading.Tasks;
using JetBrains.Annotations;
using PreciseSavestates.Modules;
using PreciseSavestates.Savestates;
using PreciseSavestates.Savestates.Game;

namespace PreciseSavestates;

/// Stable API surface for other mods to drive savestates programmatically,
/// accessed via reflection through the `Interop` field on
/// <see cref="Source.PreciseSavestatesPlugin" />
/// .
[PublicAPI]
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

    /// Creates a savestate and writes it directly to an absolute file path (bypassing the slot/layer store).
    /// <paramref name="filter" /> is a <see cref="SavestateFilter" /> bitmask; pass a negative value for the default.
    public bool CreateSavestateToFile(string path, int filter = -1) {
        return module.CreateSavestateToFile(path, filter < 0 ? null : (SavestateFilter)filter);
    }

    /// Loads a savestate directly from an absolute file path (bypassing the slot/layer store).
    public Task<bool> LoadSavestateFromFile(string path) {
        return module.LoadSavestateFromFile(path);
    }

    public float? LastLoadedGameTime => SavestateModule.LastLoadedGameTime;
    public int? LastLoadedFrameCount => SavestateModule.LastLoadedFrameCount;
    public UnityEngine.Random.State? LastLoadedRandomState => SavestateModule.LastLoadedRandomState;

    /// When set, a load restores only the scene + pre-Start save data (PlayerData/SceneData) and holds the rest of
    /// the snapshot as pending; the driver then applies it via <see cref="ApplyPendingSnapshot" /> at a controlled
    /// point. Lets a TAS land the restore at a player-loop phase symmetric with the capture (no resume dead frame).
    public bool DeferSnapshotRestore {
        get => SavestateLogic.DeferSnapshotRestore;
        set => SavestateLogic.DeferSnapshotRestore = value;
    }

    /// Whether a deferred load has finished its scene phase and is waiting for the component/FSM/RNG/clock restore.
    public bool SnapshotPending => SavestateLogic.PendingSnapshot != null;

    /// Applies the held snapshot (component/FSM/RNG/clock). No-op if none is pending.
    public void ApplyPendingSnapshot() => SavestateLogic.ApplyPendingSnapshot();

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
