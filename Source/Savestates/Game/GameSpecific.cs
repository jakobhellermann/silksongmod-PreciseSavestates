using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Newtonsoft.Json;
using TMProOld;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Events;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace PreciseSavestates.Savestates.Game;

public static class GameSpecific {
    public static IReadOnlyList<JsonConverter> ExtraConverters { get; } = [new Tk2dAnimatorConverter()];

    public static CustomizableContractResolver Resolver => new() {
        ContainerTypesToIgnore = [
            typeof(MonoBehaviour),
            typeof(Component),
            typeof(Object),
        ],
        FieldTypesToIgnore = [
            // ignored
            typeof(Camera),
            typeof(Coroutine), // native IntPtr wrapper. TODO: load a dummy coroutine for != null checks?
            typeof(GameObject),
            typeof(UnityEventBase),
            typeof(Action),
            typeof(Delegate),
            typeof(PositionConstraint),
            typeof(TextMeshProUGUI),
            typeof(TMP_Text),
            typeof(Sprite),
            typeof(Tilemap),
            typeof(LineRenderer),
            typeof(Color),
            typeof(ParticleSystem),
            typeof(AnimationCurve),
            typeof(AnimationClip),
            typeof(Rect),
            // todo
            typeof(Transform), // maybe
            typeof(RenderTexture),
            typeof(Texture2D),
            typeof(Texture3D),
            typeof(SpriteRenderer), // maybe
            typeof(LayerMask), // maybe
            typeof(Collider2D), // maybe
            typeof(ScriptableObject),
            // handled via dedicated snapshot, ignore fsm and backreferences
            typeof(Fsm),
            typeof(FsmState),
            typeof(NamedVariable),
            typeof(FsmEvent),
            typeof(FsmStateAction),
            // cached reflection metadata 
            typeof(MemberInfo),
            typeof(ParameterInfo),
        ],
        ExactFieldTypesToIgnore = [typeof(Component)],
        FieldAllowlist = new Dictionary<Type, string[]> {
            { typeof(Transform), ["localPosition", "localRotation", "localScale"] },
            { typeof(Rigidbody2D), ["position", "linearVelocity", "gravityScale", "bodyType"] },
            { typeof(MeshRenderer), [] },
            { typeof(Renderer), ["enabled"] },
            { typeof(Behaviour), ["enabled"] },
        },
        PropertyConverters = new Dictionary<Type, JsonConverter> {
            { typeof(tk2dSpriteAnimator), new Tk2dAnimatorConverter() },
        },
        FieldDenylist = new Dictionary<Type, string[]> {
            {
                typeof(FsmStateAction),
                // definitions that never change at runtime
                ["name", "enabled", "isOpen", "autoName", "blocksFinish", "fsmComponent", "Enabled", "Name"]
            },
            {
                typeof(HeroAnimationController),
                // pd/cState are references to HeroController; audioclips
                ["pd", "cState", "wakeUpGround1", "wakeUpGround2", "wakeUpGroundCloakless", "backflipSpin"]
            },
            { typeof(HeroController), [
                // restored separately
                "playerData",
                // unserializable collections, static
                "configs", "specialConfigs"]
            },
            { typeof(CameraController), ["instantLockedArea"] }, // transient? TODO
            // CallStaticMethod caches its resolved MethodInfo/Type/ParameterInfo (unresolvable) keyed on cachedClassName/cachedMethodName
            // Skipping it recomputes the cache.
            { typeof(CallStaticMethod), ["cachedClassName", "cachedMethodName", "parameters"] },
            // --- explicit denylisted "skip unserializable type" warnings ---
            { typeof(RunEffects), ["runTypes"] },
            { typeof(InputHandler), ["MappableControllerActions", "MappableKeyboardActions"] },
            { typeof(HeroVibrationController), ["audioClipVibrations", "emissions"] },
            { typeof(SpriteFlash), ["repeatingFlashes"] },
            { typeof(GameManager), ["skippables"] },
            {
                typeof(DamageEnemies),
                [
                    // static config
                    "spikeSlashReactions",
                    // scratch
                    "hitsResponded", "tempHitsResponded", "damagePrevented", "currentDamageBuffer", "processingDamageBuffer"
                ]
            },
            { typeof(tk2dTileMap), ["layers", "tilePrefabsList"] },
            { typeof(HealthManager), ["itemDropGroups", "_itemDrops"] },
            { typeof(EnemyDeathEffects), ["altCorpses", "deathSounds"] },
            { typeof(EventRelayResponder), ["responses"] },
            { typeof(CallMethodProper), ["parameters"] },
            { typeof(BattleScene), ["initialisables"] }, // recomputed from scene tree, read-only
            { typeof(InteractableBase), ["blockers"] }, // live registry , re-registered on scene reload
            // registry of enabled IRecoilMultiplier components; only registrant is BlackThreadState on OnEnable
            { typeof(Recoil), ["recoilMultipliers"] },
            { typeof(BlackThreadState), ["stateReceivers"] }, // rebuilt from scene via GetComponentsInChildren
        },
    };
}
