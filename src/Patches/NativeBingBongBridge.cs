using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride.Patches;

/// <summary>Reflection-only bridge to PEAK's native Bing Bong response and subtitle systems. Mirrors the technique used by BingBongVoiceLineAPI: write into LocalizedText.mainTable and rewrite Action_AskBingBong.responses so PEAK plays our clips with native UI.</summary>
internal static class NativeBingBongBridge
{
    internal const string OverrideSubtitleId = "BBVO_OVERRIDE_SUBTITLE";

    private static bool _resolved = false;
    private static bool _resolveOk = false;

    private static Type? _localizedTextType;
    private static FieldInfo? _mainTableField;
    private static FieldInfo? _languageCountField;
    private static MethodInfo? _tryInitTablesMethod;

    private static Type? _askBingBongType;
    private static Type? _bingBongResponseType;
    private static FieldInfo? _responsesField;
    private static FieldInfo? _responseSubtitleIdField;
    private static FieldInfo? _responseSfxField;
    private static FieldInfo? _responseMouthCurveField;
    private static FieldInfo? _responseMouthCurveTimeField;

    private static Type? _sfxInstanceType;
    private static FieldInfo? _sfxClipsField;

    private static readonly Dictionary<int, string> AssignedIdByClip = new Dictionary<int, string>();

    /// <summary>Resolves all reflection handles once and caches success state. Safe to call repeatedly.</summary>
    /// <returns>True when every required type and member is resolved.</returns>
    internal static bool TryResolve()
    {
        if (_resolved) return _resolveOk;
        _resolved = true;

        try
        {
            _localizedTextType = AccessTools.TypeByName("LocalizedText");
            if (_localizedTextType == null) return False("LocalizedText type missing");

            FieldInfo[] allFields = _localizedTextType.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            Plugin.Log.LogInfo("[NativeBingBongBridge] LocalizedText fields: "
                + string.Join(", ", System.Array.ConvertAll(allFields, f => f.Name + ":" + f.FieldType.Name)));

            string[] tableNames = { "MAIN_TABLE", "mainTable", "table", "translationTable", "translations",
                "localizationTable", "_mainTable", "_table", "entries" };
            for (int i = 0; i < tableNames.Length && _mainTableField == null; i++)
                _mainTableField = AccessTools.Field(_localizedTextType, tableNames[i]);
            if (_mainTableField == null)
            {
                for (int i = 0; i < allFields.Length && _mainTableField == null; i++)
                {
                    FieldInfo f = allFields[i];
                    if (!f.FieldType.IsGenericType) continue;
                    if (f.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>)) continue;
                    if (f.FieldType.GetGenericArguments()[0] == typeof(string))
                    {
                        _mainTableField = f;
                        Plugin.Log.LogInfo("[NativeBingBongBridge] mainTable resolved via scan: " + f.Name);
                    }
                }
            }

            string[] countNames = { "LANGUAGE_COUNT", "languageCount", "LanguageCount", "numLanguages", "NUM_LANGUAGES" };
            for (int i = 0; i < countNames.Length && _languageCountField == null; i++)
                _languageCountField = AccessTools.Field(_localizedTextType, countNames[i]);

            _tryInitTablesMethod = AccessTools.Method(_localizedTextType, "TryInitTables");
            if (_mainTableField == null) return False("LocalizedText table field not found");

            _askBingBongType = AccessTools.TypeByName("Action_AskBingBong");
            if (_askBingBongType == null) return False("Action_AskBingBong type missing");
            _bingBongResponseType = _askBingBongType.GetNestedType("BingBongResponse", BindingFlags.Public | BindingFlags.NonPublic);
            if (_bingBongResponseType == null) return False("BingBongResponse nested type missing");

            _responsesField = AccessTools.Field(_askBingBongType, "responses");
            if (_responsesField == null) return False("responses field missing");

            _responseSubtitleIdField = AccessTools.Field(_bingBongResponseType, "subtitleID");
            _responseSfxField = AccessTools.Field(_bingBongResponseType, "sfx");
            _responseMouthCurveField = AccessTools.Field(_bingBongResponseType, "mouthCurve");
            _responseMouthCurveTimeField = AccessTools.Field(_bingBongResponseType, "mouthCurveTime");
            if (_responseSubtitleIdField == null || _responseSfxField == null) return False("BingBongResponse fields missing");

            _sfxInstanceType = AccessTools.TypeByName("SFX_Instance");
            if (_sfxInstanceType == null) return False("SFX_Instance type missing");
            _sfxClipsField = AccessTools.Field(_sfxInstanceType, "clips");
            if (_sfxClipsField == null) return False("SFX_Instance.clips field missing");

            _resolveOk = true;
            Plugin.Log.LogInfo("[NativeBingBongBridge] Resolved native subtitle and response bindings.");
            return true;
        }
        catch (Exception ex)
        {
            return False("resolve exception: " + ex.Message);
        }
    }

    /// <summary>Writes the given text into LocalizedText.mainTable[id] across every language slot so the native UI displays it on the next lookup.</summary>
    /// <param name="id">Subtitle id used by a BingBongResponse (uppercased before storage).</param>
    /// <param name="text">Text to display.</param>
    /// <returns>True when the table was updated.</returns>
    internal static bool WriteSubtitle(string id, string text)
    {
        if (!TryResolve()) return false;
        if (string.IsNullOrEmpty(id)) return false;

        try
        {
            if (_tryInitTablesMethod != null)
                _tryInitTablesMethod.Invoke(null, null);

            object? langCountObj = _languageCountField != null ? _languageCountField.GetValue(null) : null;
            int langCount = langCountObj is int ic ? ic : 1;
            if (langCount <= 0) langCount = 1;

            object tableObj = _mainTableField!.GetValue(null);
            if (tableObj is IDictionary<string, List<string>> dict)
            {
                List<string> entries = new List<string>(langCount);
                for (int i = 0; i < langCount; i++) entries.Add(text ?? string.Empty);
                dict[id.ToUpperInvariant()] = entries;
                return true;
            }

            return WriteSubtitleViaReflection(tableObj, id, text, langCount);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("[NativeBingBongBridge] WriteSubtitle failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Replaces the responses array on the given Action_AskBingBong instance with one entry per loaded plugin clip, each mapped to our control subtitle id so a follow-up WriteSubtitle drives the native UI.</summary>
    /// <param name="askInstance">An Action_AskBingBong instance discovered in the scene.</param>
    /// <returns>True when the responses array was rewritten.</returns>
    internal static bool RewriteResponses(object askInstance)
    {
        if (!TryResolve() || askInstance == null) return false;

        List<AudioClip> active = Plugin.GetActiveClips();
        if (active.Count == 0) return false;

        try
        {
            Array arr = Array.CreateInstance(_bingBongResponseType!, active.Count);
            AssignedIdByClip.Clear();
            for (int i = 0; i < active.Count; i++)
            {
                AudioClip clip = active[i];
                string id = "BBVO_" + clip.name.ToUpperInvariant();
                AssignedIdByClip[clip.GetInstanceID()] = id;

                string subText;
                if (Plugin.SubtitleOverrides.TryGetValue(clip.name, out subText) && !string.IsNullOrWhiteSpace(subText))
                    WriteSubtitle(id, subText);
                else
                    WriteSubtitle(id, string.Empty);

                object response = BuildResponse(clip, id);
                arr.SetValue(response, i);
            }

            _responsesField!.SetValue(askInstance, arr);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("[NativeBingBongBridge] RewriteResponses failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Returns the subtitle id assigned to the given clip during the most recent RewriteResponses pass, or empty when not assigned.</summary>
    /// <param name="clip">Clip whose subtitle id to look up.</param>
    /// <returns>Assigned id or empty string.</returns>
    internal static string GetAssignedSubtitleId(AudioClip clip)
    {
        if (clip == null) return string.Empty;
        AssignedIdByClip.TryGetValue(clip.GetInstanceID(), out string id);
        return id ?? string.Empty;
    }

    /// <summary>Scans every loaded Action_AskBingBong instance and rewrites its responses array. Useful after clip reloads.</summary>
    /// <returns>Number of instances rewritten.</returns>
    internal static int RewriteAllInScene()
    {
        if (!TryResolve()) return 0;
        int count = 0;
        try
        {
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_askBingBongType!);
            for (int i = 0; i < all.Length; i++)
            {
                if (RewriteResponses(all[i])) count++;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("[NativeBingBongBridge] RewriteAllInScene failed: " + ex.Message);
        }
        return count;
    }

    private static object BuildResponse(AudioClip clip, string subtitleId)
    {
        ScriptableObject sfx = ScriptableObject.CreateInstance(_sfxInstanceType!) as ScriptableObject;
        AudioClip[] clipsArr = new AudioClip[] { clip };
        _sfxClipsField!.SetValue(sfx, clipsArr);

        object response = Activator.CreateInstance(_bingBongResponseType!);
        _responseSubtitleIdField!.SetValue(response, subtitleId);
        _responseSfxField!.SetValue(response, sfx);
        if (_responseMouthCurveField != null)
            _responseMouthCurveField.SetValue(response, new AnimationCurve());
        if (_responseMouthCurveTimeField != null)
            _responseMouthCurveTimeField.SetValue(response, 1f);
        return response;
    }

    private static bool WriteSubtitleViaReflection(object tableObj, string id, string text, int langCount)
    {
        if (tableObj == null) return false;
        Type tType = tableObj.GetType();
        PropertyInfo indexer = tType.GetProperty("Item");
        if (indexer == null) return false;

        Type valueType = indexer.PropertyType;
        if (!typeof(System.Collections.IList).IsAssignableFrom(valueType)) return false;

        System.Collections.IList list = (System.Collections.IList)Activator.CreateInstance(valueType);
        for (int i = 0; i < langCount; i++) list.Add(text ?? string.Empty);
        indexer.SetValue(tableObj, list, new object[] { id.ToUpperInvariant() });
        return true;
    }

    private static bool False(string reason)
    {
        Plugin.Log.LogWarning("[NativeBingBongBridge] resolve failed: " + reason);
        _resolveOk = false;
        return false;
    }
}
