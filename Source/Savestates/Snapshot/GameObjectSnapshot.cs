using JetBrains.Annotations;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Source.Savestates.Snapshot;

[PublicAPI]
public class GameObjectSnapshot {
    public required string Path;
    public int Layer;
    public bool Active;

    public static GameObjectSnapshot Of(GameObject go) => new() {
        Path = ObjectUtils.ObjectPath(go),
        Layer = go.layer,
        Active = go.activeSelf,
    };

    public bool Restore() {
        var go = ObjectUtils.LookupPath(Path);
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
