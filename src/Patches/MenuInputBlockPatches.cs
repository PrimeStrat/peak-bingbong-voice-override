using HarmonyLib;
using UnityEngine;
namespace BingBongVoiceOverride.Patches;

// Blocks mouse-look axes while the unified menu is open so gameplay camera movement pauses like the Escape menu.
[HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetAxis), new[] { typeof(string) })]
internal static class BlockMouseAxisPatch
{
    // Overrides Mouse X/Mouse Y axis values to zero while the menu is visible.
    // axisName (string): axis name requested by the game
    // __result (float): axis value to return when overridden
    // returns: bool - false to skip original Input.GetAxis for blocked mouse axes; true otherwise
    [HarmonyPrefix]
    private static bool Prefix(string axisName, ref float __result)
    {
        if (!Plugin.MenuVisible)
            return true;

        if (axisName == null)
            return true;

        if (!IsBlockedLookAxis(axisName))
            return true;

        __result = 0f;
        return false;
    }

    private static bool IsBlockedLookAxis(string axisName)
    {
        string n = axisName.Trim().ToLowerInvariant();
        if (n.Equals("mouse x") || n.Equals("mouse y")) return true;
        if (n.Contains("look")) return true;
        if (n.Contains("camera")) return true;
        if (n.Contains("rightstick")) return true;
        if (n.Contains("right stick")) return true;
        if (n.Contains("aim")) return true;
        return false;
    }
}

// Blocks raw mouse-look axes while the unified menu is open so camera movement stays frozen.
[HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetAxisRaw), new[] { typeof(string) })]
internal static class BlockMouseAxisRawPatch
{
    // Overrides Mouse X/Mouse Y raw axis values to zero while the menu is visible.
    // axisName (string): raw axis name requested by the game
    // __result (float): raw axis value to return when overridden
    // returns: bool - false to skip original Input.GetAxisRaw for blocked mouse axes; true otherwise
    [HarmonyPrefix]
    private static bool Prefix(string axisName, ref float __result)
    {
        if (!Plugin.MenuVisible)
            return true;

        if (axisName == null)
            return true;

        if (!BlockMouseAxisPatch_IsBlockedLookAxis(axisName))
            return true;

        __result = 0f;
        return false;
    }

    private static bool BlockMouseAxisPatch_IsBlockedLookAxis(string axisName)
    {
        string n = axisName.Trim().ToLowerInvariant();
        if (n.Equals("mouse x") || n.Equals("mouse y")) return true;
        if (n.Contains("look")) return true;
        if (n.Contains("camera")) return true;
        if (n.Contains("rightstick")) return true;
        if (n.Contains("right stick")) return true;
        if (n.Contains("aim")) return true;
        return false;
    }
}
