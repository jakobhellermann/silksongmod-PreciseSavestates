using Newtonsoft.Json.Linq;
using PreciseSavestates.Savestates.Snapshot;
using PreciseSavestates.Utils;
using UnityEngine;

namespace PreciseSavestates.Source.Savestates.Snapshot;

public class ComponentSnapshot {
    public required string Path;
    public required JToken Data;

    public static ComponentSnapshot Of(Component mb) {
        NormalizeCenterOfMass(mb);
        return new ComponentSnapshot {
            Path = ObjectUtils.ObjectComponentPath(mb),
            Data = SnapshotSerializer.Snapshot(mb),
        };
    }

    // HACK: TAS savestates weren't fully reproducible, presumably because unity internally uses
    // a cached center of mass that is not reachable through reflection.
    // Changing the center of mass to 0 fixes it, and has otherwise no physics impact for FreezeRotation types
    // TODO: move somewhere else?
    public static void NormalizeCenterOfMass(Component c) {
        if (c is Rigidbody2D { bodyType: RigidbodyType2D.Dynamic } rb &&
            rb.constraints.HasFlag(RigidbodyConstraints2D.FreezeRotation) &&
            rb.centerOfMass != Vector2.zero) {
            rb.useAutoMass = false;
            rb.centerOfMass = Vector2.zero;
        }
    }

    public bool Restore() {
        var targetComponent = ObjectUtils.LookupObjectComponentPath(Path);
        if (!targetComponent) {
            Log.Error($"Savestate stored state on {Path}, which does not exist at load time");
            return false;
        }

        NormalizeCenterOfMass(targetComponent);
        SnapshotSerializer.Populate(targetComponent, Data);

        return true;
    }
}
