using System;
using HarmonyLib;
using UnityEngine;
namespace BingBongVoiceOverride.Patches;

// Catches Action_AskBingBong on any Object.Instantiate call and rewrites its responses.
[HarmonyPatch(typeof(UnityEngine.Object))]
internal static class AskBingBongInstantiateDiscoveryPatch
{
    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object) })]
    [HarmonyPostfix]
    private static void InstantiatePostfix(UnityEngine.Object __result)
    {
        if (!Plugin.UseNativeBingBongAPI.Value) return;
        AskBingBongDiscoveryHelper.TryHandle(__result);
    }
}

// Catches Action_AskBingBong added via GameObject.AddComponent and rewrites its responses.
[HarmonyPatch(typeof(GameObject))]
internal static class AskBingBongAddComponentDiscoveryPatch
{
    [HarmonyPatch(nameof(GameObject.AddComponent), new Type[] { typeof(Type) })]
    [HarmonyPostfix]
    private static void AddComponentPostfix(GameObject __instance, ref Component __result)
    {
        if (!Plugin.UseNativeBingBongAPI.Value) return;
        AskBingBongDiscoveryHelper.TryHandle((UnityEngine.Object)__result ?? __instance);
    }
}

// Shared discovery helper used by both AskBingBong patches.
internal static class AskBingBongDiscoveryHelper
{
    // Locates an Action_AskBingBong component on the given object and rewrites its response array.
    // created (UnityEngine.Object): freshly instantiated or composed Unity object
    // returns: void
    internal static void TryHandle(UnityEngine.Object created)
    {
        if (created == null) return;
        if (!NativeBingBongBridge.TryResolve()) return;

        try
        {
            GameObject? go = created as GameObject;
            if (go == null && created is Component comp) go = comp.gameObject;
            if (go == null) return;

            Type askType = AccessTools.TypeByName("Action_AskBingBong");
            if (askType == null) return;

            Component? ask = go.GetComponent(askType);
            if (ask == null) ask = go.GetComponentInChildren(askType, true);
            if (ask == null) return;

            if (NativeBingBongBridge.RewriteResponses(ask))
                Plugin.Log.LogInfo($"[BBVO] Rewrote Action_AskBingBong responses on '{go.name}'.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("[BBVO] AskBingBongDiscovery exception: " + ex.Message);
        }
    }
}

