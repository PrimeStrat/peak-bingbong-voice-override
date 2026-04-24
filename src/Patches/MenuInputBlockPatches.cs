using HarmonyLib;

namespace BingBongVoiceOverride.Patches;

/// <summary>Blocks mouse-look axes while the unified menu is open so gameplay camera movement pauses like the Escape menu.</summary>
[HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetAxis), new[] { typeof(string) })]
internal static class BlockMouseAxisPatch
{
    /// <summary>Overrides Mouse X/Mouse Y axis values to zero while the menu is visible.</summary>
    /// <param name="axisName">Axis name requested by the game.</param>
    /// <param name="__result">Axis value to return when overridden.</param>
    /// <returns>False to skip original Input.GetAxis for blocked mouse axes; true otherwise.</returns>
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

/// <summary>Blocks raw mouse-look axes while the unified menu is open so camera movement stays frozen.</summary>
[HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetAxisRaw), new[] { typeof(string) })]
internal static class BlockMouseAxisRawPatch
{
    /// <summary>Overrides Mouse X/Mouse Y raw axis values to zero while the menu is visible.</summary>
    /// <param name="axisName">Raw axis name requested by the game.</param>
    /// <param name="__result">Raw axis value to return when overridden.</param>
    /// <returns>False to skip original Input.GetAxisRaw for blocked mouse axes; true otherwise.</returns>
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
