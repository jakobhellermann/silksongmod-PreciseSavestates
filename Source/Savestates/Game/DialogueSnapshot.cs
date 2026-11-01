using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using PreciseSavestates.Utils;

namespace PreciseSavestates.Savestates.Game;

// The progress only exists as locals of the DialogueBox.RunDialogue/PrintText coroutines
public class DialogueSnapshot {
    public int Line;

    private static IEnumerator? running;
    private static DialogueSnapshot? pending;
    private static int? instantPrintConversation;

    public static DialogueSnapshot? Of(DialogueBox box) {
        if (!box || !box.isDialogueRunning || running == null) {
            return null;
        }

        if (running.GetFieldValue<int>("iD") != DialogueBox._conversationID) {
            return null;
        }

        return new DialogueSnapshot {
            Line = running.GetFieldValue<int>("<lineIndex>5__3"),
        };
    }

    // the dialogue restarted during the FSM restore continues here
    public static void RestoreDuring(DialogueSnapshot? snapshot, Action fsmRestore) {
        pending = snapshot;
        try {
            fsmRestore();
        } finally {
            pending = null;
        }
    }

    private static IEnumerator PrintInstantly(DialogueBox box) {
        box.textMesh.maxVisibleCharacters = int.MaxValue;
        box.isPrintingText = false;
        yield break;
    }

    [HarmonyPatch]
    [PublicAPI]
    private static class Patches {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.RunDialogue))]
        private static void RunDialoguePrefix(ref List<DialogueBox.DialogueLine> lines, int iD) {
            if (pending == null) {
                return;
            }

            lines = lines.Skip(pending.Line).ToList();
            instantPrintConversation = iD;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.RunDialogue))]
        private static void RunDialoguePostfix(IEnumerator __result) => running = __result;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.PrintText))]
        private static void PrintTextPostfix(DialogueBox __instance, ref IEnumerator __result) {
            if (instantPrintConversation == DialogueBox._conversationID) {
                instantPrintConversation = null;
                __result = PrintInstantly(__instance);
            }
        }
    }
}
