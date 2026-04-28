using System;
using System.Reflection;
using System.Reflection.Emit;

namespace BingBongVoiceOverride.Patches;

// NOTE: We should revise this file further but it's workable, a lot of optional statements.

internal static class PhotonNet {
    private static Type? _photonNetworkType;
    private static Type? PhotonNetworkType => _photonNetworkType ??= AccessTools.TypeByName("Photon.Pun.PhotonNetwork")
        ?? AccessTools.TypeByName("PhotonNetwork");

    private static Type? _eventDataType;
    private static Type? EventDataType => _eventDataType ??= AccessTools.TypeByName("ExitGames.Client.Photon.EventData")
        ?? AccessTools.TypeByName("EventData");

    private static Type? _raiseEventOptionsType;
    private static Type? RaiseEventOptionsType => _raiseEventOptionsType ??= AccessTools.TypeByName("Photon.Realtime.RaiseEventOptions")
        ?? AccessTools.TypeByName("ExitGames.Client.Photon.RaiseEventOptions")
        ?? AccessTools.TypeByName("RaiseEventOptions");

    private static Type? _sendOptionsType;
    private static Type? SendOptionsType => _sendOptionsType ??= AccessTools.TypeByName("ExitGames.Client.Photon.SendOptions")
        ?? AccessTools.TypeByName("SendOptions");

    private static Type? _receiverGroupType;
    private static Type? ReceiverGroupType => _receiverGroupType ??= AccessTools.TypeByName("Photon.Realtime.ReceiverGroup")
        ?? AccessTools.TypeByName("ExitGames.Client.Photon.Lite.ReceiverGroup")
        ?? AccessTools.TypeByName("ReceiverGroup");

    private static MethodInfo? _raiseEventMethod;
    private static MemberInfo? _eventCodeMember;
    private static MemberInfo? _eventCustomDataMember;
    private static MemberInfo? _eventSenderMember;
    private static bool _isSubscribed;
    private static Delegate? _eventDelegate;
    private static object? _subscribedClient;
    private static EventInfo? _subscribedEvent;
    private static Action<byte, object?, int>? _eventHandler;
    private static bool _hasLoggedAvailability;

    internal static bool IsAvailable {
        get {
            bool isAvailable = PhotonNetworkType != null && EventDataType != null && RaiseEventOptionsType != null && SendOptionsType != null;
            if (!_hasLoggedAvailability) {
                _hasLoggedAvailability = true;
                Plugin.Log.LogInfo($"[PhotonNet] Reflection bindings: PN={PhotonNetworkType?.FullName ?? "null"} EvData={EventDataType?.FullName ?? "null"} RaiseOpts={RaiseEventOptionsType?.FullName ?? "null"} SendOpts={SendOptionsType?.FullName ?? "null"} Receivers={ReceiverGroupType?.FullName ?? "null"}");
            }
            return isAvailable;
        }
    }

    internal static int LocalActorNumber {
        get {
            try {
                object? localPlayer = AccessTools.Property(PhotonNetworkType!, "LocalPlayer")?.GetValue(null);
                if (localPlayer == null) return -1;
                return (int)(AccessTools.Property(localPlayer.GetType(), "ActorNumber")?.GetValue(localPlayer) ?? -1);
            }
            catch {
                return -1;
            }
        }
    }

    internal static int MasterActorNumber {
        get {
            try {
                object? masterClient = AccessTools.Property(PhotonNetworkType!, "MasterClient")?.GetValue(null);
                if (masterClient == null) return -1;
                return (int)(AccessTools.Property(masterClient.GetType(), "ActorNumber")?.GetValue(masterClient) ?? -1);
            }
            catch {
                return -1;
            }
        }
    }

    internal static string GetNickNameForActor(int actorNumber) {
        try {
            object? room = AccessTools.Property(PhotonNetworkType!, "CurrentRoom")?.GetValue(null);
            if (room == null) return string.Empty;

            object? players = AccessTools.Property(room.GetType(), "Players")?.GetValue(room);
            if (players is not System.Collections.IDictionary dictionary) return string.Empty;

            foreach (object? value in dictionary.Values) {
                if (value == null) continue;
                int currentActor = (int)(AccessTools.Property(value.GetType(), "ActorNumber")?.GetValue(value) ?? -1);
                if (currentActor == actorNumber) {
                    return AccessTools.Property(value.GetType(), "NickName")?.GetValue(value) as string ?? string.Empty;
                }
            }
        }
        catch {
        }

        return string.Empty;
    }

    internal static void Subscribe(Action<byte, object?, int> handler) {
        _eventHandler = handler;
        if (_isSubscribed) return;

        try {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo? networkingClientField = PhotonNetworkType!.GetField("NetworkingClient", flags);
            PropertyInfo? networkingClientProperty = PhotonNetworkType.GetProperty("NetworkingClient", flags);
            object? networkingClient = networkingClientField?.GetValue(null) ?? networkingClientProperty?.GetValue(null);
            if (networkingClient == null) {
                Plugin.Log.LogWarning($"[PhotonNet] NetworkingClient unavailable; cannot subscribe. (field={networkingClientField != null}, prop={networkingClientProperty != null})");
                return;
            }

            EventInfo? eventReceived = networkingClient.GetType().GetEvent("EventReceived");
            if (eventReceived?.EventHandlerType == null) {
                Plugin.Log.LogWarning("[PhotonNet] EventReceived event not found on NetworkingClient.");
                return;
            }

            _eventCodeMember ??= FindMember(EventDataType!, "Code");
            _eventCustomDataMember ??= FindMember(EventDataType!, "CustomData");
            _eventSenderMember ??= FindMember(EventDataType!, "Sender");
            if (_eventCodeMember == null || _eventCustomDataMember == null || _eventSenderMember == null) {
                Plugin.Log.LogWarning($"[PhotonNet] EventData layout unexpected: Code={_eventCodeMember != null} Custom={_eventCustomDataMember != null} Sender={_eventSenderMember != null}.");
            }

            ParameterInfo[] parameters = eventReceived.EventHandlerType.GetMethod("Invoke")!.GetParameters();
            DynamicMethod trampoline = new DynamicMethod("BBVOPhotonEventTrampoline", typeof(void), [parameters[0].ParameterType], typeof(PhotonNet).Module, true);
            ILGenerator il = trampoline.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(PhotonNet).GetMethod(nameof(DispatchEvent), BindingFlags.NonPublic | BindingFlags.Static)!);
            il.Emit(OpCodes.Ret);

            _eventDelegate = trampoline.CreateDelegate(eventReceived.EventHandlerType);
            eventReceived.AddEventHandler(networkingClient, _eventDelegate);
            _subscribedClient = networkingClient;
            _subscribedEvent = eventReceived;
            _isSubscribed = true;
            Plugin.Log.LogInfo("[PhotonNet] Subscribed to Photon EventReceived.");
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning($"[PhotonNet] Subscribe failed: {ex.Message}");
        }
    }

    internal static void Unsubscribe() {
        if (!_isSubscribed) return;

        try {
            if (_subscribedEvent != null && _subscribedClient != null && _eventDelegate != null) {
                _subscribedEvent.RemoveEventHandler(_subscribedClient, _eventDelegate);
            }
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning($"[PhotonNet] Unsubscribe failed: {ex.Message}");
        }

        _isSubscribed = false;
        _subscribedClient = null;
        _subscribedEvent = null;
        _eventDelegate = null;
        _eventHandler = null;
    }

    internal static void SendToOthers(byte code, object? data) {
        RaiseEvent(code, data, null, true, false);
    }

    internal static void SendToMaster(byte code, object? data) {
        RaiseEvent(code, data, null, true, true);
    }

    internal static void SendToActor(byte code, object? data, int actorNumber) {
        RaiseEvent(code, data, [actorNumber], true, false);
    }

    private static MemberInfo? FindMember(Type type, string name) {
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
        return (MemberInfo?)type.GetField(name, flags) ?? type.GetProperty(name, flags);
    }

    private static object? ReadMember(MemberInfo? member, object target) {
        if (member is FieldInfo field) return field.GetValue(target);
        if (member is PropertyInfo property) return property.GetValue(target);
        return null;
    }

    private static void DispatchEvent(object eventData) {
        if (_eventHandler == null || eventData == null) return;

        try {
            byte code = Convert.ToByte(ReadMember(_eventCodeMember, eventData) ?? (byte)0);
            object? customData = ReadMember(_eventCustomDataMember, eventData);
            int sender = Convert.ToInt32(ReadMember(_eventSenderMember, eventData) ?? -1);
            if (code >= 173 && code <= 180) {
                Plugin.Log.LogInfo($"[PhotonNet] <- event {code} from actor {sender}");
            }
            _eventHandler(code, customData, sender);
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning($"[PhotonNet] Dispatch error: {ex.Message}");
        }
    }

    private static void RaiseEvent(byte code, object? data, int[]? targetActors, bool reliable, bool toMasterOnly) {
        try {
            if (PhotonNetworkType == null || RaiseEventOptionsType == null || SendOptionsType == null) {
                Plugin.Log.LogWarning($"[PhotonNet] RaiseEvent({code}) skipped: PN={PhotonNetworkType != null} Opts={RaiseEventOptionsType != null} Send={SendOptionsType != null}.");
                return;
            }

            if (_raiseEventMethod == null) {
                MethodInfo[] methods = PhotonNetworkType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++) {
                    MethodInfo method = methods[i];
                    if (method.Name != "RaiseEvent") continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 4 && parameters[0].ParameterType == typeof(byte)) {
                        _raiseEventMethod = method;
                        break;
                    }
                }
            }

            if (_raiseEventMethod == null) {
                Plugin.Log.LogWarning("[PhotonNet] PhotonNetwork.RaiseEvent overload not found.");
                return;
            }

            object? options = Activator.CreateInstance(RaiseEventOptionsType);
            if (options == null) return;

            if (targetActors != null) {
                RaiseEventOptionsType.GetField("TargetActors")?.SetValue(options, targetActors);
            }
            else if (toMasterOnly && ReceiverGroupType != null) {
                RaiseEventOptionsType.GetField("Receivers")?.SetValue(options, Enum.ToObject(ReceiverGroupType, (byte)2));
            }
            else if (ReceiverGroupType != null) {
                RaiseEventOptionsType.GetField("Receivers")?.SetValue(options, Enum.ToObject(ReceiverGroupType, (byte)0));
            }

            FieldInfo? sendField = SendOptionsType.GetField(reliable ? "SendReliable" : "SendUnreliable", BindingFlags.Public | BindingFlags.Static);
            object sendOptions = sendField != null ? sendField.GetValue(null)! : Activator.CreateInstance(SendOptionsType)!;
            object? result = _raiseEventMethod.Invoke(null, [code, data, options, sendOptions]);

            if (code >= 173 && code <= 180) {
                string target = targetActors != null ? $"actor {targetActors[0]}" : toMasterOnly ? "master" : "others";
                Plugin.Log.LogInfo($"[PhotonNet] -> event {code} to {target} (returned {result})");
            }
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning($"[PhotonNet] RaiseEvent({code}) failed: {ex.Message}");
        }
    }
}
