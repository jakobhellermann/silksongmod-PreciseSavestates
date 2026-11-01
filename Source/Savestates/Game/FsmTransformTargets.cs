using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace PreciseSavestates.Savestates.Game;

/// The object whose transform an FSM action moves, rotates or scales, for the action types that do.
internal static class FsmTransformTargets {
    // From the decomp: actions assigning position/rotation/scale, Translate/Rotate or iTween, minus spawners
    private static readonly HashSet<Type> OwnerDefaultActions = [
        typeof(AlignToDirection), typeof(AnimatePositionBy), typeof(AnimatePositionTo), typeof(AnimatePositionToV2),
        typeof(AnimateRotationTo), typeof(AnimateRotationToV2), typeof(AnimateScaleTo), typeof(AnimateXPositionTo),
        typeof(AnimateYPositionTo), typeof(AnimateZPositionTo), typeof(ChaseObjectGround), typeof(ClampPosition),
        typeof(ClampRotation), typeof(ConstrainMovement), typeof(EasePosX), typeof(FaceAngle), typeof(FaceAngleV2),
        typeof(FaceAngleV3), typeof(FaceDirection), typeof(FaceDirectionV2), typeof(FaceObjectV2),
        typeof(FaceObjectV3), typeof(FaceObjectV4), typeof(FaceObjectV5), typeof(FlipScale), typeof(FlipScaleDelay),
        typeof(FlipScaleOnExit), typeof(JumpTranslateTo), typeof(LerpObjectPosition), typeof(LookAt2d),
        typeof(LookAt2dGameObject), typeof(LookAt2dGameObjectSmooth), typeof(LookAtDirection), typeof(MatchScaleSign),
        typeof(MatchScaleSignV2), typeof(MoveObject), typeof(MoveTowards), typeof(ObjectJitter),
        typeof(ObjectJitterLocal), typeof(ObjectJitterOnRender), typeof(RandomlyFlipScale),
        typeof(RandomlyFlipYScale), typeof(Rotate), typeof(RotateTo), typeof(ScaleTo), typeof(SetPosition),
        typeof(SetPosition2D), typeof(SetPositionTemp), typeof(SetPositionToObject), typeof(SetPositionToObject2D),
        typeof(SetPositionToObject2DLateUpdate), typeof(SetPositionToObject2DV2), typeof(SetPositionToObjectDelay),
        typeof(SetRandomRotation), typeof(SetRotation), typeof(SetRotation2DLerp), typeof(SetRotationDelay),
        typeof(SetRotationTemp), typeof(SetRotationTimer), typeof(SetScale), typeof(SetScaleDelay),
        typeof(SetScaleTemp), typeof(ShakePosition), typeof(ShakePositionV2), typeof(SimpleLook),
        typeof(SimpleTiltByVelocityX), typeof(SineMovement), typeof(SmoothFollowAction), typeof(SmoothFollowTarget2D),
        typeof(SmoothLookAt), typeof(SmoothLookAt2d), typeof(SmoothLookAtDirection), typeof(TiltBySpeed),
        typeof(TiltBySpeedY), typeof(Translate), typeof(TranslateContinuous), typeof(TranslateRandom),
        typeof(TranslateV2), typeof(TranslateVelocity2dConditional), typeof(TweenPosition), typeof(TweenPunch),
        typeof(TweenRotation), typeof(TweenScale), typeof(WalkLeftRight), typeof(iTweenFsmAction),
        typeof(iTweenLookFrom), typeof(iTweenLookTo), typeof(iTweenLookUpdate), typeof(iTweenMoveAdd),
        typeof(iTweenMoveBy), typeof(iTweenMoveFrom), typeof(iTweenMoveTo), typeof(iTweenMoveUpdate),
        typeof(iTweenPunchPosition), typeof(iTweenPunchRotation), typeof(iTweenPunchScale), typeof(iTweenRotateAdd),
        typeof(iTweenRotateBy), typeof(iTweenRotateFrom), typeof(iTweenRotateTo), typeof(iTweenRotateUpdate),
        typeof(iTweenScaleAdd), typeof(iTweenScaleBy), typeof(iTweenScaleFrom), typeof(iTweenScaleTo),
        typeof(iTweenScaleUpdate), typeof(iTweenShakePosition), typeof(iTweenShakeRotation), typeof(iTweenShakeScale),
    ];

    private static readonly Dictionary<Type, Func<FsmStateAction, GameObject?>> GameObjectActions = new() {
        [typeof(FaceObject)] = action => ((FaceObject)action).objectA.Value,
        [typeof(SimpleTiltOtherByVelocityX)] = action => ((SimpleTiltOtherByVelocityX)action).objectToTilt.Value,
        [typeof(TiltBySpeedV2)] = action => ((TiltBySpeedV2)action).gameObjectToTilt.Value,
    };

    private static readonly Dictionary<Type, FieldInfo?> ownerDefaultFields = new();

    public static GameObject? Of(FsmStateAction action) {
        var type = action.GetType();
        if (GameObjectActions.TryGetValue(type, out var target)) {
            return target(action);
        }
        if (!OwnerDefaultActions.Contains(type)) {
            return null;
        }

        if (!ownerDefaultFields.TryGetValue(type, out var field)) {
            field = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(FsmOwnerDefault));
            ownerDefaultFields[type] = field;
        }

        return field?.GetValue(action) is FsmOwnerDefault owner ? action.Fsm.GetOwnerDefaultTarget(owner) : null;
    }
}
