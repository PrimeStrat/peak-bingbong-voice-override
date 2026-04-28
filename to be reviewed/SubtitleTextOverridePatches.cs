using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
namespace BingBongVoiceOverride.Patches;

internal static class SubtitleTextOverridePatches {
    private static readonly List<object> TrackedTargets = [];
    private static readonly HashSet<int> StyleCapturedIds = [];
    private static readonly Dictionary<int, bool> SubtitleTargetCache = [];
    private static readonly string[] SubtitleHierarchyKeywords = ["subtitle", "caption", "sing"];
    private static readonly string[] SubtitleHierarchyExcludeKeywords = ["button", "btn", "label", "header", "title", "tooltip", "icon", "name", "score", "time", "hp", "health", "stamina", "chat", "version"];
    private static bool _internalWrite = false;

    private static float _discoverUntil = 0f;
    private static float _nextForcePushAt = 0f;
    private const float ForcePushInterval = 0.1f;

    internal static bool StyleCaptured = false;
    internal static Font? CapturedFont = null;
    internal static int CapturedFontSize = 28;
    internal static FontStyle CapturedFontStyle = FontStyle.Bold;
    internal static Color CapturedColor = Color.white;
    internal static Color CapturedOutlineColor = Color.black;
    internal static float CapturedOutlineWidth = 2f;
    internal static TextAnchor CapturedAnchor = TextAnchor.MiddleCenter;
    internal static Rect CapturedScreenRect = new Rect(0f, 0f, 0f, 0f);

    internal static void Apply(Harmony harmony) {
        TryPatchTextSetter(harmony, "UnityEngine.UI.Text");
        TryPatchTextSetter(harmony, "TMPro.TMP_Text");
        TryPatchTextSetter(harmony, "TMPro.TextMeshProUGUI");
        TryPatchTextSetter(harmony, "TMPro.TextMeshPro");
        TryPatchTmpSetTextString(harmony, "TMPro.TMP_Text");
        TryPatchTmpSetTextString(harmony, "TMPro.TextMeshProUGUI");
        TryPatchTmpSetTextString(harmony, "TMPro.TextMeshPro");
    }

    private static void TryPatchTextSetter(Harmony harmony, string typeName) {
        Type t = AccessTools.TypeByName(typeName);
        if (t == null)
            return;

        MethodInfo setter = AccessTools.PropertySetter(t, "text");
        if (setter == null)
            return;

        harmony.Patch(
            setter,
            prefix: new HarmonyMethod(typeof(SubtitleTextOverridePatches), nameof(TextSetterPrefix))
        );
    }

    private static void TryPatchTmpSetTextString(Harmony harmony, string typeName) {
        Type t = AccessTools.TypeByName(typeName);
        if (t == null)
            return;

        MethodInfo[] methods;
                 {
            methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        }
        catch (Exception) {
            return;
        }

        for (int i = 0; i < methods.Length; i++) {
            MethodInfo m = methods[i];
            if (m.Name != "SetText")
                continue;

            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == 0)
                continue;
            if (ps[0].ParameterType != typeof(string))
                continue;

                         {
                harmony.Patch(
                    m,
                    prefix: new HarmonyMethod(typeof(SubtitleTextOverridePatches), nameof(SetTextStringPrefix))
                );
            }
            catch (Exception) {
            }
        }
    }

    internal static void BeginDiscoveryWindow(float durationSeconds) {
        float now = Time.unscaledTime;
        float end = now + Mathf.Max(0.25f, durationSeconds);
        if (end > _discoverUntil)
            _discoverUntil = end;
    }

    internal static void TickForceActiveSubtitle() {
        if (Plugin.MenuVisible)
            return;

        bool timed = Plugin.IsTimedSubtitleActive;
        bool hasActive = !string.IsNullOrWhiteSpace(Plugin.ActiveSubtitle) && Time.unscaledTime <= Plugin.ActiveSubtitleUntil;
        if (!timed && !hasActive)
            return;

        float now = Time.unscaledTime;
        if (now < _nextForcePushAt)
            return;
        _nextForcePushAt = now + ForcePushInterval;

        string desired = timed ? (Plugin.ActiveSubtitle ?? string.Empty) : (hasActive ? Plugin.ActiveSubtitle : string.Empty);

        for (int i = TrackedTargets.Count - 1; i >= 0; i--) {
            object target = TrackedTargets[i];
            if (target == null) {
                TrackedTargets.RemoveAt(i);
                continue;
            }

            PropertyInfo p = target.GetType().GetProperty("text");
            if (p == null || !p.CanWrite) {
                TrackedTargets.RemoveAt(i);
                continue;
            }

                         {
                string current = p.GetValue(target) as string ?? string.Empty;
                if (current.Equals(desired, StringComparison.Ordinal))
                    continue;

                _internalWrite = true;
                p.SetValue(target, desired, null);
                _internalWrite = false;
            }
            catch (Exception) {
                _internalWrite = false;
                TrackedTargets.RemoveAt(i);
            }
        }
    }

    private static void TextSetterPrefix(object __instance, ref string value) {
        if (_internalWrite)
            return;
        if (Plugin.MenuVisible)
            return;

        bool timed = Plugin.IsTimedSubtitleActive;
        bool hasActive = !string.IsNullOrWhiteSpace(Plugin.ActiveSubtitle) && UnityEngine.Time.unscaledTime <= Plugin.ActiveSubtitleUntil;
        if (!timed && !hasActive)
            return;

        if (!IsCandidateSubtitleTarget(__instance))
            return;

        TrackTarget(__instance);
        CaptureStyleFromOnce(__instance);

        value = timed ? (Plugin.ActiveSubtitle ?? string.Empty) : (hasActive ? Plugin.ActiveSubtitle : string.Empty);
    }

    private static void SetTextStringPrefix(object __instance, ref string __0) {
        if (_internalWrite)
            return;
        if (Plugin.MenuVisible)
            return;

        bool timed = Plugin.IsTimedSubtitleActive;
        bool hasActive = !string.IsNullOrWhiteSpace(Plugin.ActiveSubtitle) && UnityEngine.Time.unscaledTime <= Plugin.ActiveSubtitleUntil;
        if (!timed && !hasActive)
            return;

        if (!IsCandidateSubtitleTarget(__instance))
            return;

        TrackTarget(__instance);
        CaptureStyleFromOnce(__instance);

        __0 = timed ? (Plugin.ActiveSubtitle ?? string.Empty) : (hasActive ? Plugin.ActiveSubtitle : string.Empty);
    }

    private static bool IsCandidateSubtitleTarget(object target) {
        return IsBingBongSubtitleTarget(target);
    }

    private static bool IsBingBongSubtitleTarget(object target) {
        Component? c = target as Component;
        if (c == null)
            return false;

        int id = c.GetInstanceID();
        if (SubtitleTargetCache.TryGetValue(id, out bool cached))
            return cached;

        bool match = false;
                 {
            UnityEngine.Transform t = c.transform;
            while (t != null && !match) {
                string name = t.gameObject.name;
                bool excluded = false;
                for (int j = 0; j < SubtitleHierarchyExcludeKeywords.Length; j++) {
                    if (name.IndexOf(SubtitleHierarchyExcludeKeywords[j], StringComparison.OrdinalIgnoreCase) >= 0) {
                        excluded = true;
                        break;
                    }
                }
                if (excluded) {
                    t = t.parent;
                    continue;
                }

                for (int i = 0; i < SubtitleHierarchyKeywords.Length; i++) {
                    if (name.IndexOf(SubtitleHierarchyKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0) {
                        match = true;
                        break;
                    }
                }
                t = t.parent;
            }
        }
        catch (Exception) {
        }

        SubtitleTargetCache[id] = match;
        return match;
    }

    private static void CaptureStyleFromOnce(object target) {
        Component? c = target as Component;
        if (c == null)
            return;
        int id = c.GetInstanceID();
        if (StyleCapturedIds.Contains(id))
            return;
        StyleCapturedIds.Add(id);
        CaptureStyleFrom(target);
    }

    private static void TrackTarget(object target) {
        if (target == null)
            return;
        if (TrackedTargets.Contains(target))
            return;
        TrackedTargets.Add(target);
    }

    private static void CaptureStyleFrom(object target) {
        Component? c = target as Component;
        if (c == null)
            return;

                 {
            Type t = target.GetType();

            PropertyInfo colorProp = t.GetProperty("color");
            if (colorProp != null && colorProp.PropertyType == typeof(Color)) {
                object cv = colorProp.GetValue(target);
                if (cv is Color col)
                    CapturedColor = col;
            }

            PropertyInfo fontSizeProp = t.GetProperty("fontSize");
            if (fontSizeProp != null) {
                object fs = fontSizeProp.GetValue(target);
                if (fs is int isize && isize > 0)
                    CapturedFontSize = isize;
                else if (fs is float fsize && fsize > 0f)
                    CapturedFontSize = Mathf.Max(8, Mathf.RoundToInt(fsize));
            }

            PropertyInfo fontStyleProp = t.GetProperty("fontStyle");
            if (fontStyleProp != null) {
                object fsv = fontStyleProp.GetValue(target);
                if (fsv is FontStyle fst)
                    CapturedFontStyle = fst;
            }

            PropertyInfo alignProp = t.GetProperty("alignment");
            if (alignProp != null) {
                object av = alignProp.GetValue(target);
                if (av is TextAnchor ta)
                    CapturedAnchor = ta;
                else if (av != null)
                    CapturedAnchor = MapTmpAlignment(av.ToString());
            }

            PropertyInfo fontProp = t.GetProperty("font");
            if (fontProp != null) {
                object fv = fontProp.GetValue(target);
                if (fv is Font f) {
                    CapturedFont = f;
                }
                else if (fv != null) {
                    PropertyInfo srcProp = fv.GetType().GetProperty("sourceFontFile");
                    if (srcProp != null) {
                        object src = srcProp.GetValue(fv);
                        if (src is Font sf)
                            CapturedFont = sf;
                    }
                }
            }

            PropertyInfo outlineColorProp = t.GetProperty("outlineColor");
            if (outlineColorProp != null && outlineColorProp.PropertyType == typeof(Color)) {
                object oc = outlineColorProp.GetValue(target);
                if (oc is Color outline)
                    CapturedOutlineColor = outline;
            }

            PropertyInfo outlineWidthProp = t.GetProperty("outlineWidth");
            if (outlineWidthProp != null) {
                object ow = outlineWidthProp.GetValue(target);
                if (ow is float f && f > 0f)
                    CapturedOutlineWidth = Mathf.Clamp(f * 8f, 1f, 4f);
                else if (ow is int i && i > 0)
                    CapturedOutlineWidth = Mathf.Clamp(i, 1f, 4f);
            }

            RectTransform? rt = c.transform as RectTransform;
            Canvas? canvas = c.GetComponentInParent<Canvas>();
            if (rt != null && canvas != null)
                CapturedScreenRect = ComputeScreenRect(rt, canvas);

            StyleCaptured = true;
        }
        catch (Exception) {
        }
    }

    private static TextAnchor MapTmpAlignment(string name) {
        if (string.IsNullOrEmpty(name)) return TextAnchor.MiddleCenter;
        string n = name.ToLowerInvariant();
        if (n.Contains("topleft")) return TextAnchor.UpperLeft;
        if (n.Contains("topright")) return TextAnchor.UpperRight;
        if (n.Contains("top")) return TextAnchor.UpperCenter;
        if (n.Contains("bottomleft")) return TextAnchor.LowerLeft;
        if (n.Contains("bottomright")) return TextAnchor.LowerRight;
        if (n.Contains("bottom")) return TextAnchor.LowerCenter;
        if (n.Contains("left")) return TextAnchor.MiddleLeft;
        if (n.Contains("right")) return TextAnchor.MiddleRight;
        return TextAnchor.MiddleCenter;
    }

    private static Rect ComputeScreenRect(RectTransform rt, Canvas canvas) {
        Vector3[] corners = new Vector3[4];
        rt.GetWorldCorners(corners);

        Camera? cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;

        Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

        float xMin = Mathf.Min(bl.x, tr.x);
        float xMax = Mathf.Max(bl.x, tr.x);
        float yMin = Mathf.Min(bl.y, tr.y);
        float yMax = Mathf.Max(bl.y, tr.y);

        float guiYTop = Screen.height - yMax;
        return new Rect(xMin, guiYTop, xMax - xMin, yMax - yMin);
    }
}
