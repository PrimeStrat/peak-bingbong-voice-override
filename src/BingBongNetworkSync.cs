using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEngine.Networking;

namespace BingBongVoiceOverride;

/// <summary>
/// HTTP-based sound sync server (host) and downloader (client).
///
/// Host side: after clips load, a lightweight HttpListener serves the sound file
/// list and raw bytes on port 28472 so joining clients can pull them.
///
/// Client side: call OnPlayerJoined(hostAddress) from a Harmony patch on the
/// game's player-join event (e.g. the method that fires when the local client
/// finishes connecting to a session). Supply the host's LAN IP and this class
/// downloads any missing .ogg files then triggers a sound refresh automatically.
/// </summary>
internal static class BingBongNetworkSync
{
    /// <summary>Human-readable server/sync state shown in the debug overlay.</summary>
    internal static string StatusText = "idle";

    private static HttpListener? _listener;
    private static Thread? _serverThread;
    private static bool _running = false;
    private static readonly object _syncLock = new object();
    private static readonly HashSet<string> _knownClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> _pendingImportedFileClients =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    private static string _activeHostAddress = string.Empty;
    private static bool _clientAutoSyncRunning = false;

    /// <summary>Starts the HTTP sound-sync server so clients that join can pull .ogg files from this host.</summary>
    /// <returns>void</returns>
    internal static void StartServer()
    {
        if (_running) return;

        _listener = new HttpListener();
        bool bound = TryBind("http://+:28472/bingbong/") || TryBind("http://localhost:28472/bingbong/");

        if (!bound)
        {
            StatusText = "server unavailable";
            Plugin.Log.LogWarning("Could not start sound sync server on port 28472.");
            return;
        }

        _running = true;
        _serverThread = new Thread(ServeLoop) { IsBackground = true, Name = "BingBongSoundServer" };
        _serverThread.Start();
        Plugin.Log.LogInfo($"Sound sync server started. ({StatusText})");
    }

    /// <summary>Stops the HTTP sound-sync server.</summary>
    /// <returns>void</returns>
    internal static void StopServer()
    {
        _running = false;
        _clientAutoSyncRunning = false;
        try { _listener?.Stop(); } catch (Exception) { }
        StatusText = "idle";
    }

    /// <summary>
    /// Call this from a Harmony patch on the game's player-join event.
    /// Downloads any sound/subtitle files the client is missing from the host, then
    /// triggers a sound refresh so the new clips are available immediately.
    /// </summary>
    /// <param name="hostAddress">LAN IP address of the session host.</param>
    /// <returns>void</returns>
    internal static void OnPlayerJoined(string hostAddress)
    {
        if (hostAddress == GetLocalIpAddress() || hostAddress == "127.0.0.1") return;
        _activeHostAddress = hostAddress;
        Plugin.Instance.StartCoroutine(DownloadSoundsFromHost(hostAddress));
        if (!_clientAutoSyncRunning)
        {
            _clientAutoSyncRunning = true;
            Plugin.Instance.StartCoroutine(ClientAutoSyncLoop());
        }
    }

    /// <summary>Registers a newly imported file and starts waiting for all known clients to fetch it.</summary>
    /// <param name="fileName">Imported file name, including extension.</param>
    /// <returns>void</returns>
    internal static void RegisterImportedFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        lock (_syncLock)
        {
            HashSet<string> waiting = new HashSet<string>(_knownClients, StringComparer.OrdinalIgnoreCase);
            _pendingImportedFileClients[fileName] = waiting;
        }
    }

    /// <summary>Returns true when all known clients have acknowledged download of the imported file.</summary>
    /// <param name="fileName">Imported file name, including extension.</param>
    /// <returns>bool</returns>
    internal static bool IsImportedFileSynced(string fileName)
    {
        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (!_pendingImportedFileClients.TryGetValue(fileName, out waiting))
                return true;
            if (waiting.Count == 0)
            {
                _pendingImportedFileClients.Remove(fileName);
                return true;
            }
            return false;
        }
    }

    /// <summary>Returns count of clients still waiting to download the imported file.</summary>
    /// <param name="fileName">Imported file name, including extension.</param>
    /// <returns>Count of pending client downloads.</returns>
    internal static int GetPendingClientCount(string fileName)
    {
        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (!_pendingImportedFileClients.TryGetValue(fileName, out waiting))
                return 0;
            return waiting.Count;
        }
    }

    private static bool TryBind(string prefix)
    {
        try
        {
            _listener!.Prefixes.Clear();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            StatusText = prefix.Contains("+") ? "hosting *:28472" : "hosting localhost:28472";
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Bind failed for {prefix}: {ex.Message}");
            return false;
        }
    }

    private static IEnumerator DownloadSoundsFromHost(string hostAddress)
    {
        StatusText = $"syncing from {hostAddress}";
        Plugin.Log.LogInfo($"Fetching sound list from host {hostAddress}...");

        UnityWebRequest listReq = UnityWebRequest.Get($"http://{hostAddress}:28472/bingbong/list");
        yield return listReq.SendWebRequest();

        if (listReq.result != UnityWebRequest.Result.Success)
        {
            StatusText = "sync failed";
            Plugin.Log.LogWarning($"Could not reach host sound server: {listReq.error}");
            listReq.Dispose();
            yield break;
        }

        string[] fileNames = listReq.downloadHandler.text.Split(
            new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        listReq.Dispose();

        int downloaded = 0;
        long maxBytes = Math.Max(64L, Plugin.MaxSyncFileSizeKb.Value) * 1024L;
        foreach (string fileName in fileNames)
        {
            string entry = fileName.Trim();
            if (string.IsNullOrEmpty(entry)) continue;

            string name;
            long sizeBytes = -1L;
            int sep = entry.IndexOf('|');
            if (sep > 0)
            {
                name = entry.Substring(0, sep).Trim();
                long.TryParse(entry.Substring(sep + 1).Trim(), out sizeBytes);
            }
            else
            {
                name = entry;
            }
            if (string.IsNullOrEmpty(name)) continue;

            string ext = Path.GetExtension(name).ToLowerInvariant();
            bool isAudio = ext == ".ogg" || ext == ".wav";
            if (isAudio && sizeBytes > 0L && sizeBytes > maxBytes)
            {
                Plugin.Log.LogInfo($"  Skipping '{name}' ({sizeBytes / 1024L} KB > {Plugin.MaxSyncFileSizeKb.Value} KB).");
                continue;
            }

            string destPath = Path.Combine(Plugin.SoundsFolder, name);
            if (File.Exists(destPath)) continue;

            UnityWebRequest fileReq = UnityWebRequest.Get(
                $"http://{hostAddress}:28472/bingbong/file/{Uri.EscapeDataString(name)}");
            yield return fileReq.SendWebRequest();

            if (fileReq.result != UnityWebRequest.Result.Success)
            {
                Plugin.Log.LogWarning($"Failed to download '{name}': {fileReq.error}");
                fileReq.Dispose();
                continue;
            }

            File.WriteAllBytes(destPath, fileReq.downloadHandler.data);
            Plugin.Log.LogInfo($"  Synced: {name}");
            downloaded++;
            fileReq.Dispose();
        }

        StatusText = downloaded > 0 ? $"synced {downloaded} file(s)" : "in sync";

        if (downloaded > 0)
            Plugin.Instance.StartRefresh();
    }

    private static IEnumerator ClientAutoSyncLoop()
    {
        while (_clientAutoSyncRunning)
        {
            if (!string.IsNullOrWhiteSpace(_activeHostAddress) && Plugin.Instance != null)
                yield return Plugin.Instance.StartCoroutine(DownloadSoundsFromHost(_activeHostAddress));
            yield return new UnityEngine.WaitForSecondsRealtime(2f);
        }
    }

    private static void ServeLoop()
    {
        while (_running && _listener != null && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception)
            {
                break;
            }
            ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url.AbsolutePath;
            string requesterIp = context.Request.RemoteEndPoint?.Address.ToString() ?? string.Empty;
            TrackClient(requesterIp);
            if (path.EndsWith("/list", StringComparison.OrdinalIgnoreCase))
                ServeFileList(context);
            else if (path.Contains("/file/"))
                ServeFile(context, Path.GetFileName(Uri.UnescapeDataString(path)), requesterIp);
            else
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Sound server request error: {ex.Message}");
        }
    }

    private static void ServeFileList(HttpListenerContext context)
    {
        long maxBytes = Math.Max(64L, Plugin.MaxSyncFileSizeKb.Value) * 1024L;
        string[] oggFiles = Directory.GetFiles(Plugin.SoundsFolder, "*.ogg", SearchOption.TopDirectoryOnly);
        string[] wavFiles = Directory.GetFiles(Plugin.SoundsFolder, "*.wav", SearchOption.TopDirectoryOnly);
        string[] jsonFiles = Directory.GetFiles(Plugin.SoundsFolder, "*.json", SearchOption.TopDirectoryOnly);
        System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();
        foreach (string f in oggFiles)
        {
            long size = new FileInfo(f).Length;
            if (size > maxBytes) continue;
            lines.Add($"{Path.GetFileName(f)}|{size}");
        }
        foreach (string f in wavFiles)
        {
            long size = new FileInfo(f).Length;
            if (size > maxBytes) continue;
            lines.Add($"{Path.GetFileName(f)}|{size}");
        }
        foreach (string f in jsonFiles)
        {
            long size = new FileInfo(f).Length;
            lines.Add($"{Path.GetFileName(f)}|{size}");
        }
        string body = string.Join("\n", lines.ToArray());
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static void ServeFile(HttpListenerContext context, string fileName, string requesterIp)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        string filePath = Path.GetFullPath(Path.Combine(Plugin.SoundsFolder, fileName));
        string soundsRoot = Path.GetFullPath(Plugin.SoundsFolder);

        if (!filePath.StartsWith(soundsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        long maxBytes = Math.Max(64L, Plugin.MaxSyncFileSizeKb.Value) * 1024L;
        long fileSize = new FileInfo(filePath).Length;
        if ((ext == ".ogg" || ext == ".wav") && fileSize > maxBytes)
        {
            context.Response.StatusCode = 413;
            context.Response.Close();
            return;
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        AcknowledgeDownloadedByClient(fileName, requesterIp);
        if (ext == ".json")
            context.Response.ContentType = "application/json";
        else if (ext == ".wav")
            context.Response.ContentType = "audio/wav";
        else
            context.Response.ContentType = "audio/ogg";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static void TrackClient(string requesterIp)
    {
        if (string.IsNullOrWhiteSpace(requesterIp) || requesterIp == "127.0.0.1" || requesterIp == "::1")
            return;
        if (requesterIp == GetLocalIpAddress())
            return;

        lock (_syncLock)
        {
            _knownClients.Add(requesterIp);
        }
    }

    private static void AcknowledgeDownloadedByClient(string fileName, string requesterIp)
    {
        if (string.IsNullOrWhiteSpace(requesterIp))
            return;

        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (_pendingImportedFileClients.TryGetValue(fileName, out waiting))
                waiting.Remove(requesterIp);
        }
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (IPAddress address in host.AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return address.ToString();
            }
        }
        catch (Exception) { }
        return "127.0.0.1";
    }
}
