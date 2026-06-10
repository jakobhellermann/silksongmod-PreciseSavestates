using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace PreciseSavestates.Utils;

public class KeybindManager {
    /**
     * When you hold A + S + F1 and check KeyboardShortcut(F1).IsPressed, it will return false.
     * With this method, other keys like A and S are not checked, so it would be true.
     */
    public static bool CheckShortcutOnly(KeyboardShortcut shortcut) {
        var isDown = Input.GetKeyDown(shortcut.MainKey);
        return shortcut.Modifiers.Aggregate(isDown, (current, modifier) => current && Input.GetKey(modifier));
    }
}
