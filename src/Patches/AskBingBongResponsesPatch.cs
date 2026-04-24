using System;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride.Patches;

/// <summary>Mirrors BingBongVoiceLineAPI's discovery hooks so we catch every Action_AskBingBong instance the moment it enters the scene and rewrite its responses to point at our clips.</summary>
[HarmonyPatch(typeof(UnityEngine.Object))]
internal static class AskBingBongInstantiateDiscoveryPatch
{
    /// <summary>Postfix on the single-arg Object.Instantiate. Walks the result for Action_AskBingBong and triggers a response rewrite.</summary>
    /// <param name="__result">The newly instantiated Unity object.</param>
    /// <returns>void</returns>
    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object) })]
    [HarmonyPostfix]
    private static void InstantiatePostfix(UnityEngine.Object __result)
    {
        if (!Plugin.ShouldUseNativeSubtitleInfrastructure()) return;
        AskBingBongDiscoveryHelper.TryHandle(__result);
    }
}

/// <summary>Catches Action_AskBingBong components that appear via GameObject.AddComponent so freshly composed Bing Bong objects are also rewritten.</summary>
[HarmonyPatch(typeof(GameObject))]
internal static class AskBingBongAddComponentDiscoveryPatch
{
    /// <summary>Postfix on AddComponent(Type) that rewrites responses when an Action_AskBingBong was added.</summary>
    /// <param name="__instance">GameObject the component was added to.</param>
    /// <param name="__result">The created component.</param>
    /// <returns>void</returns>
    [HarmonyPatch(nameof(GameObject.AddComponent), new Type[] { typeof(Type) })]
    [HarmonyPostfix]
    private static void AddComponentPostfix(GameObject __instance, ref Component __result)
    {
        if (!Plugin.ShouldUseNativeSubtitleInfrastructure()) return;
        AskBingBongDiscoveryHelper.TryHandle((UnityEngine.Object)__result ?? __instance);
    }
}

/// <summary>Shared helpers for the Action_AskBingBong discovery patches.</summary>
internal static class AskBingBongDiscoveryHelper
{
    /// <summary>Locates an Action_AskBingBong component on the given object or its descendants and rewrites its responses array.</summary>
    /// <param name="created">A freshly instantiated or composed Unity object.</param>
    /// <returns>void</returns>
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

