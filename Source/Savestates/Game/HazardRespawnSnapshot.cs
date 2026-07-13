using Newtonsoft.Json;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

public class HazardRespawnSnapshot {
    [JsonProperty(Required = Required.Always)]
    public Vector3 Location;

    [JsonProperty(Required = Required.Always)]
    public HazardRespawnMarker.FacingDirection Facing;

    public static HazardRespawnSnapshot Of(PlayerData pd) => new() {
        Location = pd.hazardRespawnLocation,
        Facing = pd.hazardRespawnFacing,
    };

    public void Restore() {
        var pd = PlayerData.instance;
        pd.hazardRespawnLocation = Location;
        pd.hazardRespawnFacing = Facing;
    }
}
