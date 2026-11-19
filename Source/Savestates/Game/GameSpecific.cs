using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Newtonsoft.Json;
using TeamCherry.NestedFadeGroup;
using TMProOld;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Events;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace PreciseSavestates.Savestates.Game;

public static class GameSpecific {
    public static IReadOnlyList<JsonConverter> ExtraConverters { get; } = [new Tk2dAnimatorConverter(), new AnimatorConverter(), new Tk2dClipConverter(), new RaycastHit2DConverter(), new ParticleSystemConverter()];

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
            // runtime-attached by OnEnter, never restorable state
            typeof(PlayMakerUnity2DProxy),
            typeof(iTweenFSMEvents),
            // todo
            typeof(Transform), // maybe
            typeof(RenderTexture),
            typeof(Texture2D),
            typeof(Texture3D),
            typeof(SpriteRenderer), // maybe
            typeof(LayerMask), // maybe
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
            { typeof(Transform), [nameof(Transform.localPosition), nameof(Transform.localRotation), nameof(Transform.localScale)] },
            { typeof(Rigidbody2D), [nameof(Rigidbody2D.position), nameof(Rigidbody2D.linearVelocity), nameof(Rigidbody2D.gravityScale), nameof(Rigidbody2D.bodyType)] },
            { typeof(MeshRenderer), [] },
            { typeof(Renderer), [nameof(Renderer.enabled)] },
            { typeof(Behaviour), [nameof(Behaviour.enabled)] },
            // not previousAlpha, so OnLateUpdate notices the changed alpha and applies it
            { typeof(NestedFadeGroupBase), [nameof(NestedFadeGroupBase.alpha)] },
            { typeof(NestedFadeGroup), [] },
            { typeof(VectorCurveAnimator), [
                "elapsed", "delayLeft", "isAnimating", "initialVector", "currentOffset", "hasCurrentOffsetChanged",
                "nextUpdateTime", "speedMultiplier", "offset", nameof(VectorCurveAnimator.FreezePosition)] },
            // their `Vector` property is the animated transform, captured separately
            { typeof(CurveOffsetAnimation), [] },
            { typeof(CurveRotationAnimation), [] },
            { typeof(CurveScaleAnimation), [] },
            // the targets GenerateTargets derived at tween start, and the progress through them
            { typeof(iTween), ["vector3s", "vector2s", "floats", "colors", "rects", "runningTime", "percentage", "reverse"] },
        },
        PropertyConverters = new Dictionary<Type, JsonConverter>(),
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
                nameof(HeroController.playerData),
                // unserializable collections, static
                nameof(HeroController.configs), nameof(HeroController.specialConfigs)]
            },
            { typeof(CameraController), [nameof(CameraController.instantLockedArea)] }, // transient? TODO
            // cached in OnEnter, re-resolved in restore
            { typeof(CharacterCheckForBump), ["bumpChecker"] },
            // CallStaticMethod caches its resolved MethodInfo/Type/ParameterInfo (unresolvable) keyed on cachedClassName/cachedMethodName
            // Skipping it recomputes the cache.
            { typeof(CallStaticMethod), [nameof(CallStaticMethod.cachedClassName), nameof(CallStaticMethod.cachedMethodName), nameof(CallStaticMethod.parameters)] },
            // --- explicit denylisted "skip unserializable type" warnings ---
            { typeof(RunEffects), [nameof(RunEffects.runTypes)] },
            { typeof(InputHandler), [nameof(InputHandler.MappableControllerActions), nameof(InputHandler.MappableKeyboardActions)] },
            { typeof(HeroVibrationController), [nameof(HeroVibrationController.audioClipVibrations), nameof(HeroVibrationController.emissions)] },
            { typeof(SpriteFlash), [nameof(SpriteFlash.repeatingFlashes)] },
            { typeof(GameManager), [nameof(GameManager.skippables)] },
            {
                typeof(DamageEnemies),
                [
                    // static config
                    nameof(DamageEnemies.spikeSlashReactions),
                    // scratch
                    nameof(DamageEnemies.hitsResponded), nameof(DamageEnemies.tempHitsResponded), nameof(DamageEnemies.damagePrevented), nameof(DamageEnemies.currentDamageBuffer), nameof(DamageEnemies.processingDamageBuffer)
                ]
            },
            { typeof(tk2dTileMap), [nameof(tk2dTileMap.layers), nameof(tk2dTileMap.tilePrefabsList)] },
            { typeof(HealthManager), [nameof(HealthManager.itemDropGroups), nameof(HealthManager._itemDrops)] },
            { typeof(EnemyDeathEffects), [nameof(EnemyDeathEffects.altCorpses), "deathSounds"] },
            { typeof(EventRelayResponder), [nameof(EventRelayResponder.responses)] },
            { typeof(CallMethodProper), [nameof(CallMethodProper.parameters)] },
            { typeof(BattleScene), [nameof(BattleScene.initialisables)] }, // recomputed from scene tree, read-only
            { typeof(InteractableBase), [nameof(InteractableBase.blockers)] }, // live registry , re-registered on scene reload
            // registry of enabled IRecoilMultiplier components; only registrant is BlackThreadState on OnEnable
            { typeof(Recoil), [nameof(Recoil.recoilMultipliers)] },
            { typeof(BlackThreadState), [nameof(BlackThreadState.stateReceivers)] }, // rebuilt from scene via GetComponentsInChildren
        },
    };
}
