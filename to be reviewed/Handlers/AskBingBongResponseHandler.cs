using System;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride.Handlers;

internal static class AskBingBongResponseHandler {
    internal static void HandleCreatedObject(UnityEngine.Object created) {
        if (created == null) {
            return;
        }

        if (!NativeBingBongHandler.TryResolve()) {
            return;
        }
         
        GameObject? root = created as GameObject;
        if (root == null && created is Component component) {
            root = component.gameObject;
        }

        if (root == null) return;

        Type askType = AccessTools.TypeByName("Action_AskBingBong");
        if (askType == null) return;

        Component? ask = root.GetComponent(askType);
        ask ??= root.GetComponentInChildren(askType, true);

        if (ask != null && NativeBingBongHandler.RewriteResponses(ask)) {
            Plugin.Log.LogInfo($"[BBVO] Rewrote Action_AskBingBong responses on '{root.name}'.");
        }
    }
}
