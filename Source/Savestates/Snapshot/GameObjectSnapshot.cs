using JetBrains.Annotations;
using PreciseSavestates.Savestates.Game;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Source.Savestates.Snapshot;

[PublicAPI]
public class GameObjectSnapshot {
    public required string Path;
    public int Layer;
    public bool Active;
    // set for objects that only exist at runtime
    public SpawnSource? Source;
    // set for objects the game moved in the hierarchy at runtime
    public ReparentSource? Reparent;

    public static GameObjectSnapshot Of(GameObject go, SpawnSource? source = null, ReparentSource? reparent = null) => new() {
        Path = ObjectUtils.ObjectPath(go),
        Layer = go.layer,
        Active = go.activeSelf,
        Source = source,
        Reparent = reparent,
    };

    public bool Restore() {
        var go = ObjectUtils.LookupPath(Path);
        if (!go && Source?.Spawn() is { } spawned) {
            Log.Info($"- Respawned {Path}");
            go = spawned;
        }
        if (!go && Reparent?.Apply() is { } reparented) {
            Log.Info($"- Reparented {Path}");
            go = reparented;
        }
        if (!go) {
            Log.Error($"Savestate stored GameObject state on {Path}, which does not exist at load time");
            return false;
        }

        go!.layer = Layer;
        if (go.activeSelf != Active) {
            go.SetActive(Active);
        }

        return true;
    }
}
