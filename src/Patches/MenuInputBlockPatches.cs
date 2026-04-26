using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
namespace BingBongVoiceOverride.Patches;

// Shared axis name list. Unity's standard mouse-look axes are "Mouse X" and "Mouse Y".
internal static class MenuAxisBlock
{
    private static readonly HashSet<string> BlockedAxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mouse X",
        "Mouse Y",
    };

    internal static bool IsBlocked(string? axisName) =>
        axisName != null && BlockedAxes.Contains(axisName.Trim());
}

// Blocks camera axes while the unified menu is open so gameplay camera movement pauses.
[HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetAxis), new[] { typeof(string) })]
internal static class BlockMouseAxisPatch
{
    [HarmonyPrefix]
    private static bool Prefix(string axisName, ref float __result)
    {
        if (!Plugin.MenuVisible || !MenuAxisBlock.IsBlocked(axisName)) return true;
        __result = 0f;
        return false;
    }
}

[HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetAxisRaw), new[] { typeof(string) })]
internal static class BlockMouseAxisRawPatch
{
    [HarmonyPrefix]
    private static bool Prefix(string axisName, ref float __result)
    {
        if (!Plugin.MenuVisible || !MenuAxisBlock.IsBlocked(axisName)) return true;
        __result = 0f;
        return false;
    }
}
