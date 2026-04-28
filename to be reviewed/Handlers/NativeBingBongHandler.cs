using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride.Handlers;

internal static class NativeBingBongHandler {
    private const string LogPrefix = "[NativeBingBong]";

    private static bool _hasResolved;
    private static bool _isAvailable;
    private static ReflectionBindings? _bindings;
    private static readonly Dictionary<int, string> AssignedSubtitleIdByClip = new Dictionary<int, string>();

    private sealed class ReflectionBindings {
        internal Type LocalizedTextType = null!;
        internal FieldInfo MainTableField = null!;
        internal FieldInfo? LanguageCountField;
        internal MethodInfo? TryInitTablesMethod;
        internal Type AskBingBongType = null!;
        internal Type BingBongResponseType = null!;
        internal FieldInfo ResponsesField = null!;
        internal FieldInfo SubtitleIdField = null!;
        internal FieldInfo SfxField = null!;
        internal FieldInfo? MouthCurveField;
        internal FieldInfo? MouthCurveTimeField;
        internal Type SfxInstanceType = null!;
        internal FieldInfo SfxClipsField = null!;
    }

    internal static bool TryResolve() {
        if (_hasResolved) return _isAvailable;

        _hasResolved = true;
        try {
            _bindings = BuildBindings();
            _isAvailable = true;
            Plugin.Log.LogInfo(LogPrefix + " Resolved native subtitle and response bindings.");
        }
        catch (Exception ex) {
            _bindings = null;
            _isAvailable = false;
            Plugin.Log.LogWarning(LogPrefix + " " + ex.Message);
        }

        return _isAvailable;
    }

    internal static bool WriteSubtitle(string subtitleId, string text) {
        if (!TryResolve() || string.IsNullOrEmpty(subtitleId) || _bindings == null) return false;

        try {
            if (_bindings.TryInitTablesMethod != null) {
                _bindings.TryInitTablesMethod.Invoke(null, null);
            }

            object? tableObject = _bindings.MainTableField.GetValue(null);
            if (tableObject == null) return false;

            int languageCount = GetLanguageCount();
            string normalizedId = subtitleId.ToUpperInvariant();
            string subtitleText = text ?? string.Empty;

            if (tableObject is IDictionary<string, List<string>> typedTable) {
                List<string> entries = new List<string>(languageCount);
                for (int i = 0; i < languageCount; i++) {
                    entries.Add(subtitleText);
                }
                typedTable[normalizedId] = entries;
                return true;
            }

            return WriteSubtitleWithReflection(tableObject, normalizedId, subtitleText, languageCount);
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning(LogPrefix + " WriteSubtitle failed: " + ex.Message);
            return false;
        }
    }

    internal static bool RewriteResponses(object askInstance) {
        if (!TryResolve() || askInstance == null || _bindings == null) return false;

        List<AudioClip> activeClips = Plugin.GetActiveClips();
        if (activeClips.Count == 0) return false;

        try {
            Array responses = Array.CreateInstance(_bindings.BingBongResponseType, activeClips.Count);
            AssignedSubtitleIdByClip.Clear();

            for (int i = 0; i < activeClips.Count; i++) {
                AudioClip clip = activeClips[i];
                string subtitleId = "BBVO_" + clip.name.ToUpperInvariant();
                AssignedSubtitleIdByClip[clip.GetInstanceID()] = subtitleId;

                string subtitle = Plugin.SubtitleOverrides.TryGetValue(clip.name, out string savedSubtitle)
                    && !string.IsNullOrWhiteSpace(savedSubtitle)
                    ? savedSubtitle
                    : string.Empty;

                WriteSubtitle(subtitleId, subtitle);
                responses.SetValue(BuildResponse(clip, subtitleId), i);
            }

            _bindings.ResponsesField.SetValue(askInstance, responses);
            return true;
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning(LogPrefix + " RewriteResponses failed: " + ex.Message);
            return false;
        }
    }

    internal static string GetAssignedSubtitleId(AudioClip clip) {
        if (clip == null) return string.Empty;
        AssignedSubtitleIdByClip.TryGetValue(clip.GetInstanceID(), out string subtitleId);
        return subtitleId ?? string.Empty;
    }

    internal static int RewriteAllInScene() {
        if (!TryResolve() || _bindings == null) return 0;

        try {
            int rewrittenCount = 0;
            UnityEngine.Object[] instances = UnityEngine.Object.FindObjectsOfType(_bindings.AskBingBongType);
            for (int i = 0; i < instances.Length; i++) {
                if (RewriteResponses(instances[i])) {
                    rewrittenCount++;
                }
            }
            return rewrittenCount;
        }
        catch (Exception ex) {
            Plugin.Log.LogWarning(LogPrefix + " RewriteAllInScene failed: " + ex.Message);
            return 0;
        }
    }

    private static ReflectionBindings BuildBindings() {
        Type localizedTextType = AccessTools.TypeByName("LocalizedText")
            ?? throw new InvalidOperationException("LocalizedText type missing.");
        FieldInfo mainTableField = FindMainTableField(localizedTextType)
            ?? throw new InvalidOperationException("LocalizedText table field not found.");

        Type askBingBongType = AccessTools.TypeByName("Action_AskBingBong")
            ?? throw new InvalidOperationException("Action_AskBingBong type missing.");
        Type bingBongResponseType = askBingBongType.GetNestedType("BingBongResponse", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BingBongResponse nested type missing.");

        FieldInfo responsesField = AccessTools.Field(askBingBongType, "responses")
            ?? throw new InvalidOperationException("responses field missing.");
        FieldInfo subtitleIdField = AccessTools.Field(bingBongResponseType, "subtitleID")
            ?? throw new InvalidOperationException("subtitleID field missing.");
        FieldInfo sfxField = AccessTools.Field(bingBongResponseType, "sfx")
            ?? throw new InvalidOperationException("sfx field missing.");

        Type sfxInstanceType = AccessTools.TypeByName("SFX_Instance")
            ?? throw new InvalidOperationException("SFX_Instance type missing.");
        FieldInfo sfxClipsField = AccessTools.Field(sfxInstanceType, "clips")
            ?? throw new InvalidOperationException("SFX_Instance.clips field missing.");

        return new ReflectionBindings {
            LocalizedTextType = localizedTextType,
            MainTableField = mainTableField,
            LanguageCountField = AccessTools.Field(localizedTextType, "languageCount"),
            TryInitTablesMethod = AccessTools.Method(localizedTextType, "TryInitTables"),
            AskBingBongType = askBingBongType,
            BingBongResponseType = bingBongResponseType,
            ResponsesField = responsesField,
            SubtitleIdField = subtitleIdField,
            SfxField = sfxField,
            MouthCurveField = AccessTools.Field(bingBongResponseType, "mouthCurve"),
            MouthCurveTimeField = AccessTools.Field(bingBongResponseType, "mouthCurveTime"),
            SfxInstanceType = sfxInstanceType,
            SfxClipsField = sfxClipsField,
        };
    }

    private static FieldInfo? FindMainTableField(Type localizedTextType) {
        FieldInfo? mainTableField = AccessTools.Field(localizedTextType, "mainTable");
        if (mainTableField != null) return mainTableField;

        FieldInfo[] fields = localizedTextType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        for (int i = 0; i < fields.Length; i++) {
            FieldInfo field = fields[i];
            if (!field.FieldType.IsGenericType) continue;
            if (field.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>)) continue;
            if (field.FieldType.GetGenericArguments()[0] == typeof(string)) return field;
        }

        return null;
    }

    private static int GetLanguageCount() {
        if (_bindings?.LanguageCountField?.GetValue(null) is int languageCount && languageCount > 0) {
            return languageCount;
        }

        return 1;
    }

    private static object BuildResponse(AudioClip clip, string subtitleId) {
        ScriptableObject sfxInstance = (ScriptableObject)ScriptableObject.CreateInstance(_bindings!.SfxInstanceType);
        _bindings.SfxClipsField.SetValue(sfxInstance, new AudioClip[] { clip });

        object response = Activator.CreateInstance(_bindings.BingBongResponseType)!;
        _bindings.SubtitleIdField.SetValue(response, subtitleId);
        _bindings.SfxField.SetValue(response, sfxInstance);
        if (_bindings.MouthCurveField != null) {
            _bindings.MouthCurveField.SetValue(response, new AnimationCurve());
        }
        if (_bindings.MouthCurveTimeField != null) {
            _bindings.MouthCurveTimeField.SetValue(response, 1f);
        }
        return response;
    }

    private static bool WriteSubtitleWithReflection(object tableObject, string subtitleId, string text, int languageCount) {
        PropertyInfo? indexer = tableObject.GetType().GetProperty("Item");
        if (indexer == null) return false;
        if (!typeof(IList).IsAssignableFrom(indexer.PropertyType)) return false;

        IList values = (IList)Activator.CreateInstance(indexer.PropertyType)!;
        for (int i = 0; i < languageCount; i++) {
            values.Add(text);
        }

        indexer.SetValue(tableObject, values, new object[] { subtitleId });
        return true;
    }
}
