using System;
using System.Collections.Generic;
using HarmonyLib;

namespace BingBongVoiceOverride.Handlers;

internal static class NetworkSyncPatchHandler {
    internal static bool LocalPlayerCreatedLobby { get; private set; }
    internal static bool IsInRoom { get; private set; }

    internal static void HandlePlayerEnteredRoom(object player) {
        try {
            string playerName = PhotonBridge.GetNickName(player);
            bool isHost = LocalPlayerCreatedLobby;
            Plugin.Log.LogInfo($"[NetworkSync] Player entered room: '{playerName}'. LocalCreatedLobby={isHost}");
            if (!isHost) return;

            if (!BingBongNetworkSync.IsHosting) {
                BingBongNetworkSync.StartServer();
            }
            BingBongNetworkSync.OnPhotonPlayerJoined(playerName);
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning($"[NetworkSync] OnPlayerEnteredRoom error: {ex.Message}");
        }
    }

    internal static void HandlePlayerLeftRoom(object player) {
        try {
            string playerName = PhotonBridge.GetNickName(player);
            Plugin.Log.LogInfo($"[NetworkSync] Player left room: '{playerName}'.");
            BingBongNetworkSync.OnPhotonPlayerLeft(playerName);
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning($"[NetworkSync] OnPlayerLeftRoom error: {ex.Message}");
        }
    }

    internal static void HandleJoinedRoom() {
        IsInRoom = true;

        try {
            if (LocalPlayerCreatedLobby) {
                if (BingBongNetworkSync.IsHosting) return;

                Plugin.Log.LogInfo("[NetworkSync] Local player joined room as host.");
                BingBongNetworkSync.StartServer();
                List<string> otherPlayers = PhotonBridge.GetOtherPlayerNames();
                for (int i = 0; i < otherPlayers.Count; i++) {
                    BingBongNetworkSync.OnPhotonPlayerJoined(otherPlayers[i]);
                }
                if (otherPlayers.Count > 0) {
                    BingBongNetworkSync.BroadcastRefresh();
                }
                return;
            }

            if (BingBongNetworkSync.IsConnectedAsClient) return;

            Plugin.Log.LogInfo("[NetworkSync] Local player joined room as client.");
            BingBongNetworkSync.OnPlayerJoined(string.Empty);
            Menu.joinToastUntil = UnityEngine.Time.unscaledTime + 8f;
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning($"[NetworkSync] OnJoinedRoom error: {ex.Message}");
        }
    }

    internal static void HandleLeftRoom() {
        LocalPlayerCreatedLobby = false;
        IsInRoom = false;
        Plugin.Log.LogInfo("[NetworkSync] Left room; cleared LocalPlayerCreatedLobby.");
        BingBongNetworkSync.StopServer();
    }

    internal static void HandlePlayClicked() {
        LocalPlayerCreatedLobby = true;
        Plugin.Log.LogInfo("[NetworkSync] PlayClicked detected; this player will host the sync server.");
    }

    internal static string GetLocalPhotonNickName() {
        return PhotonBridge.GetLocalNickName();
    }

    private static class PhotonBridge {
        private static Type? _photonNetworkType;

        private static Type? PhotonNetworkType => _photonNetworkType ??= AccessTools.TypeByName("Photon.Pun.PhotonNetwork");

        private static object? GetStaticPropertyValue(string propertyName) {
            Type? photonNetworkType = PhotonNetworkType;
            if (photonNetworkType == null) return null;
            return AccessTools.Property(photonNetworkType, propertyName)?.GetValue(null);
        }

        internal static string GetLocalNickName() {
            object? localPlayer = GetStaticPropertyValue("LocalPlayer");
            if (localPlayer == null) return string.Empty;
            return AccessTools.Property(localPlayer.GetType(), "NickName")?.GetValue(localPlayer) as string ?? string.Empty;
        }

        internal static string GetNickName(object? player) {
            if (player == null) return string.Empty;
            return AccessTools.Property(player.GetType(), "NickName")?.GetValue(player) as string ?? string.Empty;
        }

        internal static List<string> GetOtherPlayerNames() {
            List<string> names = new List<string>();
            object? room = GetStaticPropertyValue("CurrentRoom");
            if (room == null) return names;

            object? players = AccessTools.Property(room.GetType(), "Players")?.GetValue(room);
            if (players is not System.Collections.IDictionary dictionary) return names;

            object? localPlayer = GetStaticPropertyValue("LocalPlayer");
            int localActorNumber = localPlayer != null
                ? (int)(AccessTools.Property(localPlayer.GetType(), "ActorNumber")?.GetValue(localPlayer) ?? -1)
                : -1;

            foreach (object? value in dictionary.Values) {
                if (value == null) continue;

                int actorNumber = (int)(AccessTools.Property(value.GetType(), "ActorNumber")?.GetValue(value) ?? -1);
                if (localActorNumber >= 0 && actorNumber == localActorNumber) continue;

                string? playerName = AccessTools.Property(value.GetType(), "NickName")?.GetValue(value) as string;
                if (!string.IsNullOrWhiteSpace(playerName)) {
                    names.Add(playerName);
                }
            }

            return names;
        }
    }
}
