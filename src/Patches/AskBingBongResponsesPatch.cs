using System;
using HarmonyLib;
using UnityEngine;
namespace BingBongVoiceOverride.Patches;

// Mirrors BingBongVoiceLineAPI's discovery hooks so we catch every Action_AskBingBong instance the moment it enters the scene and rewrite its responses to point at our clips.
[HarmonyPatch(typeof(UnityEngine.Object))]
internal static class AskBingBongInstantiateDiscoveryPatch
{
    // Postfix on Object.Instantiate; walks the result for Action_AskBingBong and triggers a response rewrite.
    // __result (UnityEngine.Object): the newly instantiated Unity object
    // returns: void
    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object) })]
    [HarmonyPostfix]
    private static void InstantiatePostfix(UnityEngine.Object __result)
    {
        if (!Plugin.UseNativeBingBongAPI.Value) return;
        AskBingBongDiscoveryHelper.TryHandle(__result);
    }
}

// Catches Action_AskBingBong components added via GameObject.AddComponent so freshly composed Bing Bong objects are also rewritten.
[HarmonyPatch(typeof(GameObject))]
internal static class AskBingBongAddComponentDiscoveryPatch
{
    // Postfix on AddComponent(Type); rewrites responses when an Action_AskBingBong was added.
    // __instance (GameObject): the GameObject the component was added to
    // __result (Component): the created component
    // returns: void
    [HarmonyPatch(nameof(GameObject.AddComponent), new Type[] { typeof(Type) })]
    [HarmonyPostfix]
    private static void AddComponentPostfix(GameObject __instance, ref Component __result)
    {
        if (!Plugin.UseNativeBingBongAPI.Value) return;
        AskBingBongDiscoveryHelper.TryHandle((UnityEngine.Object)__result ?? __instance);
    }
}

// Shared helpers for the Action_AskBingBong discovery patches.
internal static class AskBingBongDiscoveryHelper
{
    // Locates an Action_AskBingBong component on the given object or its descendants and rewrites its responses array.
    // created (UnityEngine.Object): a freshly instantiated or composed Unity object
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

