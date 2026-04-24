using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
namespace BingBongVoiceOverride;

// Photon-based sound sync. The master client (host) serves audio files; non-master clients
// pull missing files. All transport rides on Photon RaiseEvent so it works through Photon's
// relay just like normal game traffic - no LAN, no HTTP server, no firewall holes required.
internal static class BingBongNetworkSync
{
    private const byte EV_REQ_LIST = 173;
    private const byte EV_FILE_LIST = 174;
    private const byte EV_REQ_FILE = 175;
    private const byte EV_FILE_CHUNK = 176;
    private const byte EV_CONFIRM_HAVE = 177;
    private const byte EV_SIGNAL = 178;
    private const byte EV_STRING_BATCH = 181;

    private const byte SIG_REFRESH = 1;
    private const byte SIG_STOP = 2;
    private const byte SIG_PLAY = 3;
    private const byte SIG_PAUSE = 4;
    private const byte SIG_RESUME = 5;
    private const byte SIG_FORCE_NEXT = 6;
    private const byte SIG_SUBTITLE = 7;
    private const byte SIG_CLIP_ENABLED = 8;
    private const byte SIG_PERMISSIONS = 9;

    private const byte STRING_BATCH_FILE_LIST = 1;
    private const byte STRING_BATCH_CONFIRM_HAVE = 2;

    // Leave room for Photon serialization overhead instead of targeting the theoretical max payload size.
    private const int CHUNK_SIZE = 12 * 1024;
    private const int MAX_STRING_BATCH_BYTES = 12 * 1024;
    private const int CHUNKS_PER_SEND_SLICE = 3;
    private const float CLIENT_TRANSFER_STALL_TIMEOUT_SECONDS = 60f;

    // Human-readable transport state shown in menus and overlay.
    internal static string StatusText = "idle";

    // True when the local player is the master client and acting as the sound host.
    internal static bool IsHosting => _running;

    // True when the local player is in a room as a non-master client and has been linked to the host.
    internal static bool IsConnectedAsClient => !_running && _hostJoined;

    // True when the host allows clients to upload new sounds, mirrored from host status broadcasts.
    internal static bool HostAllowsClientImports = false;

    // Reserved for menu code that previously displayed the host IP. Always empty under Photon transport.
    internal static string ActiveHostAddress => _hostJoined ? "photon" : string.Empty;

    // True after the client has received any event from the host this session.
    internal static bool HasReachedHost => _hasReachedHost;

    // True when the most recent client sync attempt failed and is awaiting retry.
    internal static bool ClientSyncFailed => _syncFailed;

    // True while a client sync is downloading or the host is waiting for a client to confirm files.
    internal static bool IsSyncBusy =>
        _clientSyncRunning ||
        (IsHosting && _lobbyPlayerNames.Count > 0 && GetUnsyncedPlayerNames().Count > 0);

    private static bool _running = false;
    private static bool _hostJoined = false;
    private static bool _clientSyncRunning = false;
    private static bool _syncFailed = false;
    private static bool _hasReachedHost = false;
    internal static bool ClientAllowPlayback = false;
    internal static bool ClientAllowSubtitleEdit = false;
    internal static bool ClientAllowSelectionEdit = false;
    internal static bool ClientAllowSettingsChange = false;
    internal static bool ClientAllowMenu = true;

    private static readonly object _syncLock = new();

    // Lobby (host-side) tracking by Photon NickName.
    private static readonly HashSet<string> _lobbyPlayerNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> _playerSyncedFileNames =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-actor download tracking (host-side).
    private static readonly HashSet<int> _knownActors = new();
    private static readonly Dictionary<int, string> _actorNames = new();
    private static readonly Dictionary<int, HashSet<string>> _actorDownloadedFiles = new();
    private static readonly Dictionary<string, HashSet<string>> _pendingImportedFileClients =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, IncomingChunkWriter> _incomingChunkWriters = new();
    private static readonly Queue<PendingChunkSend> _outboundChunkQueue = new();

    // Client-side file list received from the host.
    private static List<(string name, long size)>? _hostFileList = null;
    // Files the client is still waiting to download (set populated when the file list arrives).
    private static readonly HashSet<string> _clientPendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string[][]> _stringBatchBuffers = new();
    private static bool _sendPumpRunning = false;
    private static float _lastClientTransferProgressAt = 0f;

    private sealed class PendingChunkSend
    {
        internal string FileName = string.Empty;
        internal string FilePath = string.Empty;
        internal int TargetActor = -1;
        internal long Length = 0L;
        internal int TotalChunks = 0;
        internal int NextChunkIndex = 0;
        internal bool IsCompleted = false;
        internal FileStream? Stream;

        internal void Dispose()
        {
            Stream?.Dispose();
            Stream = null;
        }
    }

    private sealed class IncomingChunkWriter
    {
        internal string TempPath = string.Empty;
        internal int TotalChunks = 0;
        internal int ReceivedChunks = 0;
        internal bool[] Received = Array.Empty<bool>();
        internal FileStream Stream = null!;

        internal void Dispose()
        {
            Stream?.Dispose();
        }
    }

    // Subscribes to Photon events and marks this client as the sound host.
    // returns: void
    internal static void StartServer()
    {
        if (_running) return;
        if (!Patches.PhotonNet.IsAvailable)
        {
            StatusText = "photon unavailable";
            Plugin.Log.LogWarning("[Sync] Photon runtime not found; sync disabled.");
            return;
        }
        _running = true;
        Patches.PhotonNet.Subscribe(OnPhotonEvent);
        StatusText = "hosting (photon)";
        Plugin.Log.LogInfo("[Sync] Photon sync host active.");
    }

    // Tears down host state and unsubscribes from Photon events.
    // returns: void
    internal static void StopServer()
    {
        _running = false;
        _hostJoined = false;
        _hasReachedHost = false;
        StatusText = "idle";
        Patches.PhotonNet.Unsubscribe();
        DisposeIncomingChunkWriters();
        ClearOutboundChunkQueue();
        lock (_syncLock)
        {
            _lobbyPlayerNames.Clear();
            _playerSyncedFileNames.Clear();
            _knownActors.Clear();
            _actorNames.Clear();
            _actorDownloadedFiles.Clear();
            _pendingImportedFileClients.Clear();
            _stringBatchBuffers.Clear();
            _clientPendingFiles.Clear();
            _hostFileList = null;
            _sendPumpRunning = false;
            _lastClientTransferProgressAt = 0f;
        }
    }

    // Tells every connected client to clear timed subtitle state.
    // returns: void
    internal static void BroadcastStop()
    {
        if (!_running) return;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_STOP });
    }

    // Broadcasts a play command for a specific clip to all other players.
    // clipName (string): clip name without extension
    // returns: void
    internal static void BroadcastPlay(string clipName)
    {
        if (!_running) return;
        byte[] nameBytes = Encoding.UTF8.GetBytes(clipName ?? string.Empty);
        byte[] payload = new byte[1 + nameBytes.Length];
        payload[0] = SIG_PLAY;
        Buffer.BlockCopy(nameBytes, 0, payload, 1, nameBytes.Length);
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, payload);
    }

    // Broadcasts a pause command to all other players. returns: void
    internal static void BroadcastPause()
    {
        if (!_running) return;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_PAUSE });
    }

    // Broadcasts a resume command to all other players. returns: void
    internal static void BroadcastResume()
    {
        if (!_running) return;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_RESUME });
    }

    // Broadcasts a force-next clip command to all other players.
    // clipName (string): clip name without extension to set as the forced next pick
    // returns: void
    internal static void BroadcastForceNext(string clipName)
    {
        if (!_running) return;
        byte[] nameBytes = Encoding.UTF8.GetBytes(clipName ?? string.Empty);
        byte[] payload = new byte[1 + nameBytes.Length];
        payload[0] = SIG_FORCE_NEXT;
        Buffer.BlockCopy(nameBytes, 0, payload, 1, nameBytes.Length);
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, payload);
    }

    // Broadcasts a subtitle override change to all other players. Usable by host or a permitted client.
    // clipName (string): clip name without extension
    // subtitle (string): new subtitle text, empty to clear
    // returns: void
    internal static void BroadcastSubtitleUpdate(string clipName, string subtitle)
    {
        if (!Patches.PhotonNet.IsAvailable) return;
        byte[] nameBytes = Encoding.UTF8.GetBytes(clipName ?? string.Empty);
        byte[] subtitleBytes = Encoding.UTF8.GetBytes(subtitle ?? string.Empty);
        byte[] payload = new byte[1 + nameBytes.Length + 1 + subtitleBytes.Length];
        payload[0] = SIG_SUBTITLE;
        Buffer.BlockCopy(nameBytes, 0, payload, 1, nameBytes.Length);
        payload[1 + nameBytes.Length] = 0;
        Buffer.BlockCopy(subtitleBytes, 0, payload, 1 + nameBytes.Length + 1, subtitleBytes.Length);
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, payload);
    }

    // Broadcasts a clip enabled/disabled state change to all other players. Usable by host or a permitted client.
    // clipName (string): clip name without extension
    // enabled (bool): new enabled state
    // returns: void
    internal static void BroadcastClipEnabled(string clipName, bool enabled)
    {
        if (!Patches.PhotonNet.IsAvailable) return;
        byte[] nameBytes = Encoding.UTF8.GetBytes(clipName ?? string.Empty);
        byte[] payload = new byte[2 + nameBytes.Length];
        payload[0] = SIG_CLIP_ENABLED;
        payload[1] = enabled ? (byte)1 : (byte)0;
        Buffer.BlockCopy(nameBytes, 0, payload, 2, nameBytes.Length);
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, payload);
    }

    // Broadcasts the current host permission flags to all connected players. Host only.
    // returns: void
    internal static void BroadcastPermissions()
    {
        if (!_running) return;
        byte perms = 0;
        if (Plugin.AllowClientPlayback.Value) perms |= 1;
        if (Plugin.AllowClientSubtitleEdit.Value) perms |= 2;
        if (Plugin.AllowClientSelectionEdit.Value) perms |= 4;
        if (Plugin.AllowClientSettingsChange.Value) perms |= 8;
        if (Plugin.AllowClientMenu.Value) perms |= 16;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_PERMISSIONS, perms });
    }

    // Tells every connected client to re-pull files and refresh.
    // returns: void
    internal static void BroadcastRefresh()
    {
        if (!_running) return;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_REFRESH });
    }

    // Sends a locally imported file to the host as a chunked Photon upload.
    // fileName (string): file name including extension to upload from the local sounds folder
    // _hostAddressIgnored (string): legacy parameter; transport now uses Photon master client routing
    // returns: IEnumerator
    internal static IEnumerator UploadFileToHost(string fileName, string _hostAddressIgnored)
    {
        string filePath = Path.Combine(Plugin.SoundsFolder, fileName);
        if (!File.Exists(filePath)) yield break;
        PendingChunkSend? transfer = EnqueueFileTransfer(fileName, filePath, Patches.PhotonNet.MasterActorNumber);
        if (transfer == null) yield break;
        while (!transfer.IsCompleted)
            yield return null;
    }

    // Marks the local player as connected to the host and triggers the initial sync. Called
    // from the Harmony OnJoinedRoom postfix when the local player is not the master client.
    // _hostAddressIgnored (string): legacy parameter; ignored under Photon transport
    // returns: void
    internal static void OnPlayerJoined(string _hostAddressIgnored)
    {
        if (!Patches.PhotonNet.IsAvailable)
        {
            StatusText = "photon unavailable";
            return;
        }
        _hostJoined = true;
        _syncFailed = false;
        Patches.PhotonNet.Subscribe(OnPhotonEvent);
        TriggerClientSync();
    }

    // Legacy entry point retained for menu compatibility. Photon transport does not need polling.
    // returns: void
    internal static void StartClientPollLoop() { }

    // Adds a player to the lobby tracking list. Called from NetworkSyncPatches on Photon player-join.
    // playerName (string): Photon NickName of the player who joined
    // returns: void
    internal static void OnPhotonPlayerJoined(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return;
        lock (_syncLock)
            _lobbyPlayerNames.Add(playerName);
        Plugin.Log.LogInfo($"[Sync] Lobby player joined: '{playerName}'");
        BroadcastPermissions();
    }

    // Removes a player from the lobby tracking list. Called from NetworkSyncPatches on Photon player-leave.
    // playerName (string): Photon NickName of the player who left
    // returns: void
    internal static void OnPhotonPlayerLeft(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return;
        lock (_syncLock)
        {
            _lobbyPlayerNames.Remove(playerName);
            _playerSyncedFileNames.Remove(playerName);
        }
        Plugin.Log.LogInfo($"[Sync] Lobby player left: '{playerName}'");
    }

    // Resets failure state and either re-broadcasts to clients (host) or kicks off a fresh sync (client).
    // returns: void
    internal static void AllowResync()
    {
        _syncFailed = false;
        if (IsHosting)
        {
            lock (_syncLock)
                _playerSyncedFileNames.Clear();
            BroadcastRefresh();
        }
        if (IsConnectedAsClient)
            TriggerClientSync();
    }

    // Starts a one-shot client sync coroutine if one is not already running.
    // returns: void
    internal static void TriggerClientSync()
    {
        if (_syncFailed || _clientSyncRunning || !_hostJoined) return;
        _clientSyncRunning = true;
        Plugin.Instance.StartCoroutine(RunClientSync());
    }

    // Returns the names of lobby players who have not yet downloaded all served audio files.
    // returns: List<string>
    internal static List<string> GetUnsyncedPlayerNames()
    {
        int totalFiles = GetServedAudioFileCount();
        lock (_syncLock)
        {
            List<string> result = new();
            foreach (string name in _lobbyPlayerNames)
            {
                HashSet<string>? synced;
                if (!_playerSyncedFileNames.TryGetValue(name, out synced) || synced.Count < totalFiles)
                    result.Add(name);
            }
            return result;
        }
    }

    // Returns true when every known lobby player has downloaded the given clip. Only relevant on the host.
    // clipName (string): clip name without extension
    // returns: bool
    internal static bool IsClipSyncedToAllClients(string clipName)
    {
        if (!_running) return true;
        lock (_syncLock)
        {
            if (_lobbyPlayerNames.Count == 0) return true;
            string? fileName = ResolveAudioFileName(clipName);
            if (fileName == null) return true;
            foreach (string playerName in _lobbyPlayerNames)
            {
                HashSet<string>? synced;
                if (!_playerSyncedFileNames.TryGetValue(playerName, out synced) || !synced.Contains(fileName))
                    return false;
            }
            return true;
        }
    }

    // Registers a newly imported file and waits for all known clients to fetch it.
    // fileName (string): imported file name including extension
    // returns: void
    internal static void RegisterImportedFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        lock (_syncLock)
        {
            HashSet<string> waiting = new(_lobbyPlayerNames, StringComparer.OrdinalIgnoreCase);
            _pendingImportedFileClients[fileName] = waiting;
        }
    }

    // Returns true when all known clients have acknowledged download of the imported file.
    // fileName (string): imported file name including extension
    // returns: bool
    internal static bool IsImportedFileSynced(string fileName)
    {
        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (!_pendingImportedFileClients.TryGetValue(fileName, out waiting)) return true;
            if (waiting.Count == 0)
            {
                _pendingImportedFileClients.Remove(fileName);
                return true;
            }
            return false;
        }
    }

    // Returns count of clients still waiting to download the imported file.
    // fileName (string): imported file name including extension
    // returns: int - count of pending client downloads
    internal static int GetPendingClientCount(string fileName)
    {
        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (!_pendingImportedFileClients.TryGetValue(fileName, out waiting)) return 0;
            return waiting.Count;
        }
    }

    // Returns the count of audio files currently available to serve from this host.
    // returns: int - count of .ogg and .wav files in the sounds folder
    internal static int GetServedAudioFileCount()
    {
        try
        {
            return Directory.GetFiles(Plugin.SoundsFolder, "*.ogg", SearchOption.TopDirectoryOnly).Length
                 + Directory.GetFiles(Plugin.SoundsFolder, "*.wav", SearchOption.TopDirectoryOnly).Length
                 + Directory.GetFiles(Plugin.SoundsFolder, "*.json", SearchOption.TopDirectoryOnly).Length;
        }
        catch (Exception) { return 0; }
    }

    // Returns how many files have been confirmed synced for the given player (by Photon display name).
    // playerName (string): the player's Photon NickName as stored in _lobbyPlayerNames
    // returns: int - number of files recorded as synced for that player
    internal static int GetPlayerSyncedFileCount(string playerName)
    {
        lock (_syncLock)
        {
            return _playerSyncedFileNames.TryGetValue(playerName, out HashSet<string> s) ? s.Count : 0;
        }
    }

    // Returns per-client download counts as a snapshot.
    // returns: List<(string displayName, int count)> - downloaded file count per known client
    internal static List<(string displayName, int count)> GetClientDownloadCounts()
    {
        lock (_syncLock)
        {
            List<(string, int)> result = new(_knownActors.Count);
            foreach (int actor in _knownActors)
            {
                string name = _actorNames.TryGetValue(actor, out string n) && !string.IsNullOrWhiteSpace(n)
                    ? n : $"actor#{actor}";
                int c = _actorDownloadedFiles.TryGetValue(actor, out HashSet<string> f) ? f.Count : 0;
                result.Add((name, c));
            }
            return result;
        }
    }

    // Returns imported files still pending download by at least one client.
    // returns: List<(string fileName, int pending)> - pending import entries
    internal static List<(string fileName, int pending)> GetPendingImports()
    {
        lock (_syncLock)
        {
            List<(string, int)> result = new();
            foreach (KeyValuePair<string, HashSet<string>> kv in _pendingImportedFileClients)
                if (kv.Value.Count > 0) result.Add((kv.Key, kv.Value.Count));
            return result;
        }
    }

    // Returns null when no audio file matches the clip name in the local sounds folder.
    private static string? ResolveAudioFileName(string clipName)
    {
        foreach (string ext in new[] { ".ogg", ".wav" })
        {
            if (File.Exists(Path.Combine(Plugin.SoundsFolder, clipName + ext)))
                return clipName + ext;
        }
        return null;
    }

    // Coroutine that drives a single client sync round-trip.
    private static IEnumerator RunClientSync()
    {
        StatusText = "syncing (photon)";
        _hostFileList = null;
        Patches.PhotonNet.SendToMaster(EV_REQ_LIST, null);

        float waited = 0f;
        while (_hostFileList == null && waited < 15f)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        if (_hostFileList == null)
        {
            _syncFailed = true;
            _clientSyncRunning = false;
            StatusText = "sync failed (no host response)";
            Plugin.Log.LogWarning("[Sync] No file list received from host within 15s.");
            yield break;
        }

        long maxBytes = GetConfiguredMaxSyncBytes();
        List<string> needed = new();
        foreach ((string name, long size) in _hostFileList)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            bool isAudio = ext == ".ogg" || ext == ".wav";
            if (isAudio && size > 0L && size > maxBytes)
            {
                Plugin.Log.LogInfo($"  Skipping '{name}' ({size / 1024L} KB > {Plugin.MaxSyncFileSizeKb.Value} KB).");
                continue;
            }
            bool isJson = ext == ".json";
            if (!isJson && File.Exists(Path.Combine(Plugin.SoundsFolder, name))) continue;
            needed.Add(name);
        }

        lock (_syncLock)
        {
            _clientPendingFiles.Clear();
            foreach (string n in needed) _clientPendingFiles.Add(n);
        }

        for (int i = 0; i < needed.Count; i++)
        {
            string name = needed[i];
            Patches.PhotonNet.SendToMaster(EV_REQ_FILE, name);
            _lastClientTransferProgressAt = Time.unscaledTime;
            while (true)
            {
                bool completed;
                int remaining;
                lock (_syncLock)
                {
                    completed = !_clientPendingFiles.Contains(name);
                    remaining = _clientPendingFiles.Count;
                }
                if (completed) break;
                if (Time.unscaledTime - _lastClientTransferProgressAt >= CLIENT_TRANSFER_STALL_TIMEOUT_SECONDS)
                {
                    Plugin.Log.LogWarning($"[Sync] Download timeout for '{name}' - {remaining} file(s) still pending.");
                    break;
                }
                StatusText = $"downloading ({needed.Count - remaining}/{needed.Count})";
                yield return null;
            }
        }

        // Confirm every file we have on disk that the host advertises.
        List<string> presentFiles = new();
        foreach ((string name, long _) in _hostFileList)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if ((ext == ".ogg" || ext == ".wav" || ext == ".json") && File.Exists(Path.Combine(Plugin.SoundsFolder, name)))
                presentFiles.Add(name);
        }
        if (presentFiles.Count > 0)
            SendStringBatchToMaster(STRING_BATCH_CONFIRM_HAVE, presentFiles);

        string selectionPath = Path.Combine(Plugin.SoundsFolder, "selection.json");
        if (File.Exists(selectionPath))
            ApplySelectionJson(File.ReadAllBytes(selectionPath));

        StatusText = "in sync";
        _clientSyncRunning = false;
        if (!Plugin.IsRefreshPending)
            Plugin.Instance.StartRefresh();
    }

    // Photon event dispatcher. Runs on the Unity main thread.
    private static void OnPhotonEvent(byte code, object? data, int senderActor)
    {
        try
        {
            switch (code)
            {
                case EV_REQ_LIST: HandleReqList(senderActor); break;
                case EV_FILE_LIST: HandleFileList(data); break;
                case EV_REQ_FILE: HandleReqFile(data, senderActor); break;
                case EV_FILE_CHUNK: HandleFileChunk(data, senderActor); break;
                case EV_CONFIRM_HAVE: HandleConfirmHave(data, senderActor); break;
                case EV_SIGNAL: HandleSignal(data); break;
                case EV_STRING_BATCH: HandleStringBatch(data, senderActor); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Sync] Event {code} handler error: {ex.Message}");
        }
    }

    private static void HandleReqList(int senderActor)
    {
        if (!_running) return;
        TrackActor(senderActor);
        long maxBytes = GetConfiguredMaxSyncBytes();
        List<string> entries = new();
        try
        {
            foreach (string f in Directory.GetFiles(Plugin.SoundsFolder, "*.ogg", SearchOption.TopDirectoryOnly))
            {
                long size = new FileInfo(f).Length;
                if (size > maxBytes) continue;
                entries.Add($"{Path.GetFileName(f)}|{size}");
            }
            foreach (string f in Directory.GetFiles(Plugin.SoundsFolder, "*.wav", SearchOption.TopDirectoryOnly))
            {
                long size = new FileInfo(f).Length;
                if (size > maxBytes) continue;
                entries.Add($"{Path.GetFileName(f)}|{size}");
            }
            foreach (string f in Directory.GetFiles(Plugin.SoundsFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                long size = new FileInfo(f).Length;
                entries.Add($"{Path.GetFileName(f)}|{size}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Sync] Building file list failed: {ex.Message}");
        }
        SendStringBatchesToActor(STRING_BATCH_FILE_LIST, entries, senderActor);
    }

    private static void HandleFileList(object? data)
    {
        if (data is not string[] entries) return;
        _hasReachedHost = true;
        List<(string, long)> list = new(entries.Length);
        foreach (string entry in entries)
        {
            string e = entry?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(e)) continue;
            int sep = e.IndexOf('|');
            string name = sep > 0 ? e.Substring(0, sep).Trim() : e;
            long size = -1L;
            if (sep > 0) long.TryParse(e.Substring(sep + 1).Trim(), out size);
            if (!string.IsNullOrEmpty(name)) list.Add((name, size));
        }
        _hostFileList = list;
    }

    private static void HandleReqFile(object? data, int senderActor)
    {
        if (!_running || data is not string fileName) return;
        if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
        string filePath = Path.GetFullPath(Path.Combine(Plugin.SoundsFolder, fileName));
        string root = Path.GetFullPath(Plugin.SoundsFolder);
        if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath)) return;
        long maxBytes = GetConfiguredMaxSyncBytes();
        if (new FileInfo(filePath).Length > maxBytes) return;
        EnqueueFileTransfer(fileName, filePath, senderActor);
    }

    private static void HandleFileChunk(object? data, int senderActor)
    {
        if (data is not object[] arr || arr.Length < 4) return;
        string? fileName = arr[0] as string;
        if (string.IsNullOrEmpty(fileName)) return;
        int idx = Convert.ToInt32(arr[1]);
        int total = Convert.ToInt32(arr[2]);
        byte[]? payload = arr[3] as byte[];
        if (payload == null || idx < 0 || total <= 0 || idx >= total) return;

        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (_running)
        {
            if (Plugin.AllowClientImports == null || !Plugin.AllowClientImports.Value) return;
            if (ext != ".ogg" && ext != ".wav" && ext != ".json") return;
        }

        string destPath = Path.GetFullPath(Path.Combine(Plugin.SoundsFolder, fileName!));
        string root = Path.GetFullPath(Plugin.SoundsFolder);
        if (!destPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;

        string key = $"{senderActor}:{fileName}";
        IncomingChunkWriter? writer = null;
        bool complete = false;
        string tempPath = string.Empty;
        try
        {
            lock (_syncLock)
            {
                if (!_incomingChunkWriters.TryGetValue(key, out writer))
                {
                    writer = CreateIncomingChunkWriter(key, total);
                    if (writer == null) return;
                    _incomingChunkWriters[key] = writer;
                }
                if (writer.TotalChunks != total || writer.Received.Length != total) return;
                if (!writer.Received[idx])
                {
                    writer.Stream.Position = (long)idx * CHUNK_SIZE;
                    writer.Stream.Write(payload, 0, payload.Length);
                    writer.Received[idx] = true;
                    writer.ReceivedChunks++;
                }
                complete = writer.ReceivedChunks == writer.TotalChunks;
                if (complete)
                {
                    tempPath = writer.TempPath;
                    _incomingChunkWriters.Remove(key);
                }
            }
            if (!_running)
                _lastClientTransferProgressAt = Time.unscaledTime;
            if (!complete) return;

            writer!.Dispose();
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tempPath, destPath);
            if (_running)
            {
                Plugin.Log.LogInfo($"[Sync] Received uploaded '{fileName}' from actor {senderActor}.");
                Plugin.Instance?.StartRefresh();
            }
            else
            {
                Plugin.Log.LogInfo($"[Sync] Synced: {fileName}");
                lock (_syncLock) _clientPendingFiles.Remove(fileName);
            }
        }
        catch (Exception ex)
        {
            writer?.Dispose();
            if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                File.Delete(tempPath);
            lock (_syncLock)
                _incomingChunkWriters.Remove(key);
            Plugin.Log.LogWarning($"[Sync] Write failed for '{fileName}': {ex.Message}");
        }
    }

    private static void HandleConfirmHave(object? data, int senderActor)
    {
        if (!_running || data is not string[] names) return;
        TrackActor(senderActor);
        RecordActorDownloads(names, senderActor);
    }

    private static void HandleSignal(object? data)
    {
        if (data is not byte[] bytes || bytes.Length == 0) return;
        switch (bytes[0])
        {
            case SIG_REFRESH:
                _syncFailed = false;
                _clientSyncRunning = false;
                TriggerClientSync();
                break;
            case SIG_STOP:
                Plugin.ClearTimedSubtitles();
                Plugin.ApplyRemoteStop();
                break;
            case SIG_PLAY:
                string playName = bytes.Length > 1 ? Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1) : string.Empty;
                Plugin.ApplyRemotePlay(playName);
                break;
            case SIG_PAUSE:
                Plugin.ApplyRemotePause();
                break;
            case SIG_RESUME:
                Plugin.ApplyRemoteResume();
                break;
            case SIG_FORCE_NEXT:
                string forceName = bytes.Length > 1 ? Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1) : string.Empty;
                Plugin.ApplyRemoteForceNext(forceName);
                break;
            case SIG_SUBTITLE:
                if (bytes.Length >= 2)
                {
                    int sep = Array.IndexOf(bytes, (byte)0, 1);
                    string clipName = sep > 1 ? Encoding.UTF8.GetString(bytes, 1, sep - 1) : string.Empty;
                    string subtitle = sep >= 1 && sep < bytes.Length - 1
                        ? Encoding.UTF8.GetString(bytes, sep + 1, bytes.Length - sep - 1)
                        : string.Empty;
                    if (!string.IsNullOrEmpty(clipName))
                        Plugin.SaveSubtitleOverrideForClip(clipName, subtitle);
                }
                break;
            case SIG_CLIP_ENABLED:
                if (bytes.Length >= 3)
                {
                    bool enabled = bytes[1] != 0;
                    string enabledClipName = Encoding.UTF8.GetString(bytes, 2, bytes.Length - 2);
                    if (!string.IsNullOrEmpty(enabledClipName))
                    {
                        Plugin.EnabledClips[enabledClipName] = enabled;
                        Plugin.SaveSelection();
                    }
                }
                break;
            case SIG_PERMISSIONS:
                if (bytes.Length >= 2)
                {
                    byte perms = bytes[1];
                    ClientAllowPlayback = (perms & 1) != 0;
                    ClientAllowSubtitleEdit = (perms & 2) != 0;
                    ClientAllowSelectionEdit = (perms & 4) != 0;
                    ClientAllowSettingsChange = (perms & 8) != 0;
                    ClientAllowMenu = (perms & 16) != 0;
                    if (!ClientAllowMenu)
                        Plugin.SetMenuVisible(false);
                    Plugin.Log.LogInfo($"[Sync] Host permissions: playback={ClientAllowPlayback} subtitleEdit={ClientAllowSubtitleEdit} selectionEdit={ClientAllowSelectionEdit} settingsChange={ClientAllowSettingsChange} menu={ClientAllowMenu}");
                }
                break;
        }
    }

    // Reassembles multi-event string payloads used for large file manifests and confirmations.
    private static void HandleStringBatch(object? data, int senderActor)
    {
        if (data is not object[] arr || arr.Length < 4) return;
        byte kind = Convert.ToByte(arr[0]);
        int batchIndex = Convert.ToInt32(arr[1]);
        int batchTotal = Convert.ToInt32(arr[2]);
        if (batchIndex < 0 || batchTotal <= 0 || batchIndex >= batchTotal) return;
        if (arr[3] is not string[] items) return;

        string key = $"{senderActor}:{kind}";
        string[][] buffer;
        bool complete;
        lock (_syncLock)
        {
            if (!_stringBatchBuffers.TryGetValue(key, out buffer))
            {
                buffer = new string[batchTotal][];
                _stringBatchBuffers[key] = buffer;
            }
            if (buffer.Length != batchTotal) return;
            buffer[batchIndex] = items;
            complete = AllStringBatchesPresent(buffer);
        }
        if (!complete) return;

        List<string> merged = new();
        lock (_syncLock)
        {
            foreach (string[] batch in buffer)
                merged.AddRange(batch);
            _stringBatchBuffers.Remove(key);
        }

        switch (kind)
        {
            case STRING_BATCH_FILE_LIST:
                HandleFileList(merged.ToArray());
                break;
            case STRING_BATCH_CONFIRM_HAVE:
                if (_running)
                    RecordActorDownloads(merged, senderActor);
                break;
        }
    }

    // Queues a file for paced chunked transfer to the target actor.
    private static PendingChunkSend? EnqueueFileTransfer(string fileName, string filePath, int targetActor)
    {
        if (targetActor < 0 || string.IsNullOrWhiteSpace(fileName) || !File.Exists(filePath)) return null;
        FileInfo info = new(filePath);
        PendingChunkSend transfer = new()
        {
            FileName = fileName,
            FilePath = filePath,
            TargetActor = targetActor,
            Length = info.Length,
            TotalChunks = Math.Max(1, (int)Math.Ceiling(info.Length / (double)CHUNK_SIZE))
        };
        bool startPump = false;
        lock (_syncLock)
        {
            _outboundChunkQueue.Enqueue(transfer);
            if (!_sendPumpRunning)
            {
                _sendPumpRunning = true;
                startPump = true;
            }
        }
        if (startPump && Plugin.Instance != null)
            Plugin.Instance.StartCoroutine(ProcessOutboundChunkQueue());
        return transfer;
    }

    private static IEnumerator ProcessOutboundChunkQueue()
    {
        while (true)
        {
            PendingChunkSend? transfer;
            lock (_syncLock)
            {
                if (_outboundChunkQueue.Count == 0)
                {
                    _sendPumpRunning = false;
                    yield break;
                }
                transfer = _outboundChunkQueue.Dequeue();
            }

            try
            {
                transfer.Stream ??= new FileStream(transfer.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                int sent = 0;
                while (sent < CHUNKS_PER_SEND_SLICE && transfer.NextChunkIndex < transfer.TotalChunks)
                {
                    long chunkOffset = (long)transfer.NextChunkIndex * CHUNK_SIZE;
                    int expectedLength = (int)Math.Min(CHUNK_SIZE, Math.Max(0L, transfer.Length - chunkOffset));
                    byte[] slice = new byte[expectedLength];
                    transfer.Stream.Position = chunkOffset;
                    int read = transfer.Stream.Read(slice, 0, expectedLength);
                    if (read <= 0)
                        throw new EndOfStreamException($"Unexpected EOF while streaming '{transfer.FileName}'.");
                    if (read != slice.Length)
                        Array.Resize(ref slice, read);
                    object[] payload = new object[] { transfer.FileName, transfer.NextChunkIndex, transfer.TotalChunks, slice };
                    Patches.PhotonNet.SendToActor(EV_FILE_CHUNK, payload, transfer.TargetActor);
                    transfer.NextChunkIndex++;
                    sent++;
                }

                if (transfer.NextChunkIndex >= transfer.TotalChunks)
                {
                    transfer.IsCompleted = true;
                    transfer.Dispose();
                }
                else
                {
                    lock (_syncLock)
                        _outboundChunkQueue.Enqueue(transfer);
                }
            }
            catch (Exception ex)
            {
                transfer.IsCompleted = true;
                transfer.Dispose();
                Plugin.Log.LogWarning($"[Sync] Send failed for '{transfer.FileName}': {ex.Message}");
            }

            yield return null;
        }
    }

    private static void SendStringBatchesToActor(byte batchKind, List<string> items, int actorNumber)
    {
        List<string[]> batches = CreateStringBatches(items);
        if (batches.Count == 1)
        {
            if (batchKind == STRING_BATCH_FILE_LIST)
            {
                Patches.PhotonNet.SendToActor(EV_FILE_LIST, batches[0], actorNumber);
                return;
            }
        }
        for (int i = 0; i < batches.Count; i++)
        {
            object[] payload = new object[] { batchKind, i, batches.Count, batches[i] };
            Patches.PhotonNet.SendToActor(EV_STRING_BATCH, payload, actorNumber);
        }
    }

    private static void SendStringBatchToMaster(byte batchKind, List<string> items)
    {
        List<string[]> batches = CreateStringBatches(items);
        if (batches.Count == 1)
        {
            if (batchKind == STRING_BATCH_CONFIRM_HAVE)
            {
                Patches.PhotonNet.SendToMaster(EV_CONFIRM_HAVE, batches[0]);
                return;
            }
        }
        for (int i = 0; i < batches.Count; i++)
        {
            object[] payload = new object[] { batchKind, i, batches.Count, batches[i] };
            Patches.PhotonNet.SendToMaster(EV_STRING_BATCH, payload);
        }
    }

    private static List<string[]> CreateStringBatches(List<string> items)
    {
        List<string[]> batches = new();
        if (items.Count == 0)
        {
            batches.Add(Array.Empty<string>());
            return batches;
        }

        List<string> current = new();
        int currentBytes = 0;
        foreach (string item in items)
        {
            string value = item ?? string.Empty;
            int itemBytes = Encoding.UTF8.GetByteCount(value) + sizeof(short);
            bool wouldOverflow = current.Count > 0 && currentBytes + itemBytes > MAX_STRING_BATCH_BYTES;
            if (wouldOverflow)
            {
                batches.Add(current.ToArray());
                current = new List<string>();
                currentBytes = 0;
            }
            current.Add(value);
            currentBytes += itemBytes;
        }
        if (current.Count > 0)
            batches.Add(current.ToArray());
        return batches;
    }

    private static bool AllStringBatchesPresent(string[][] buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            if (buffer[i] == null) return false;
        return true;
    }

    private static long GetConfiguredMaxSyncBytes()
    {
        int maxKb = Plugin.MaxSyncFileSizeKb?.Value ?? 0;
        if (maxKb <= 0) return long.MaxValue;
        return Math.Max(64L, maxKb) * 1024L;
    }

    private static IncomingChunkWriter? CreateIncomingChunkWriter(string key, int totalChunks)
    {
        try
        {
            string tempRoot = Path.Combine(Plugin.SoundsFolder, ".bbvo-sync-temp");
            Directory.CreateDirectory(tempRoot);
            string safeKey = key.Replace(':', '_').Replace('\\', '_').Replace('/', '_');
            string tempPath = Path.Combine(tempRoot, safeKey + ".part");
            return new IncomingChunkWriter
            {
                TempPath = tempPath,
                TotalChunks = totalChunks,
                Received = new bool[totalChunks],
                Stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)
            };
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Sync] Failed to create temp writer: {ex.Message}");
            return null;
        }
    }

    private static void DisposeIncomingChunkWriters()
    {
        lock (_syncLock)
        {
            foreach (KeyValuePair<string, IncomingChunkWriter> kv in _incomingChunkWriters)
            {
                kv.Value.Dispose();
                if (!string.IsNullOrEmpty(kv.Value.TempPath) && File.Exists(kv.Value.TempPath))
                    File.Delete(kv.Value.TempPath);
            }
            _incomingChunkWriters.Clear();
        }
    }

    private static void ClearOutboundChunkQueue()
    {
        lock (_syncLock)
        {
            while (_outboundChunkQueue.Count > 0)
            {
                PendingChunkSend transfer = _outboundChunkQueue.Dequeue();
                transfer.IsCompleted = true;
                transfer.Dispose();
            }
        }
    }

    private static void RecordActorDownloads(IEnumerable<string> names, int senderActor)
    {
        if (!_running) return;
        TrackActor(senderActor);
        foreach (string name in names)
        {
            if (!string.IsNullOrEmpty(name))
                RecordActorDownload(name, senderActor);
        }
    }

    // Tracks an actor and updates the synced file map for their NickName.
    private static void RecordActorDownload(string fileName, int actor)
    {
        string displayName;
        int fileCount;
        lock (_syncLock)
        {
            HashSet<string> files;
            if (!_actorDownloadedFiles.TryGetValue(actor, out files))
            {
                files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _actorDownloadedFiles[actor] = files;
            }
            files.Add(fileName);
            fileCount = files.Count;
            displayName = _actorNames.TryGetValue(actor, out string n) && !string.IsNullOrWhiteSpace(n)
                ? n : $"actor#{actor}";
            if (!string.IsNullOrEmpty(displayName))
            {
                if (!_playerSyncedFileNames.TryGetValue(displayName, out HashSet<string> namedFiles))
                {
                    namedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _playerSyncedFileNames[displayName] = namedFiles;
                }
                namedFiles.Add(fileName);
            }
            if (_pendingImportedFileClients.TryGetValue(fileName, out HashSet<string> waiting))
            {
                waiting.Remove(displayName);
            }
        }
        Plugin.Log.LogInfo($"[Sync] {displayName} confirmed '{fileName}' (now has {fileCount} file(s) from this host)");
    }

    // Resolves a sender actor's NickName via Photon and stores it. Also auto-registers them in the
    // lobby tracking list so the host still works when PEAK's PlayerConnectionLog hook never fires.
    private static void TrackActor(int actor)
    {
        if (actor < 0) return;
        string name = Patches.PhotonNet.GetNickNameForActor(actor);
        bool added = false;
        lock (_syncLock)
        {
            _knownActors.Add(actor);
            if (!string.IsNullOrWhiteSpace(name))
            {
                _actorNames[actor] = name;
                if (!_lobbyPlayerNames.Contains(name))
                {
                    _lobbyPlayerNames.Add(name);
                    added = true;
                }
            }
        }
        if (added)
            Plugin.Log.LogInfo($"[Sync] Auto-registered actor {actor} as '{name}' (via incoming Photon event).");
    }

    private static void ApplySelectionJson(byte[] bytes)
    {
        try
        {
            string raw = Encoding.UTF8.GetString(bytes);
            System.Text.RegularExpressions.MatchCollection matches =
                System.Text.RegularExpressions.Regex.Matches(
                    raw, "\"(?<k>(?:\\\\.|[^\"])+)\"\\s*:\\s*(?<v>true|false)");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string key = System.Text.RegularExpressions.Regex.Unescape(m.Groups["k"].Value);
                bool value = m.Groups["v"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                Plugin.EnabledClips[key] = value;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Sync] selection.json parse failed: {ex.Message}");
        }
    }
}
