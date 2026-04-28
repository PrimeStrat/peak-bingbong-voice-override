using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BingBongVoiceOverride.Handlers;

internal static class SubtitleTextOverrideHandler {
    private static readonly List<object> TrackedTargets = new List<object>();
    private static readonly HashSet<int> StyleCapturedIds = new HashSet<int>();
    private static readonly Dictionary<int, bool> SubtitleTargetCache = new Dictionary<int, bool>();
    private static readonly string[] SubtitleHierarchyKeywords = ["subtitle", "caption", "sing"];
    private static readonly string[] SubtitleHierarchyExcludeKeywords = ["button", "btn", "label", "header", "title", "tooltip", "icon", "name", "score", "time", "hp", "health", "stamina", "chat", "version"];
    private static bool _internalWrite;
    private static float _discoverUntil;
    private static float _nextForcePushAt;
    private const float ForcePushInterval = 0.1f;

    internal static bool StyleCaptured { get; private set; }
    internal static Font? CapturedFont { get; private set; }
    internal static int CapturedFontSize { get; private set; } = 28;
    internal static FontStyle CapturedFontStyle { get; private set; } = FontStyle.Bold;
    internal static Color CapturedColor { get; private set; } = Color.white;
    internal static Color CapturedOutlineColor { get; private set; } = Color.black;
    internal static float CapturedOutlineWidth { get; private set; } = 2f;
    internal static TextAnchor CapturedAnchor { get; private set; } = TextAnchor.MiddleCenter;
    internal static Rect CapturedScreenRect { get; private set; } = new Rect(0f, 0f, 0f, 0f);

    internal static void BeginDiscoveryWindow(float durationSeconds) {
        float end = Time.unscaledTime + Mathf.Max(0.25f, durationSeconds);
        if (end > _discoverUntil) {
            _discoverUntil = end;
        }
    }

    internal static void TickForceActiveSubtitle() {
        if (Plugin.MenuVisible) return;

        bool timed = Plugin.IsTimedSubtitleActive;
        bool hasActive = !string.IsNullOrWhiteSpace(Plugin.ActiveSubtitle) && Time.unscaledTime <= Plugin.ActiveSubtitleUntil;
        if (!timed && !hasActive) return;

        float now = Time.unscaledTime;
        if (now < _nextForcePushAt) return;
        _nextForcePushAt = now + ForcePushInterval;

        string desired = timed ? (Plugin.ActiveSubtitle ?? string.Empty) : (hasActive ? Plugin.ActiveSubtitle : string.Empty);
        for (int i = TrackedTargets.Count - 1; i >= 0; i--) {
            object target = TrackedTargets[i];
            if (target == null) {
                TrackedTargets.RemoveAt(i);
                continue;
            }

            PropertyInfo? textProperty = target.GetType().GetProperty("text");
            if (textProperty == null || !textProperty.CanWrite) {
                TrackedTargets.RemoveAt(i);
                continue;
            }

            try {
                string current = textProperty.GetValue(target) as string ?? string.Empty;
                if (current.Equals(desired, StringComparison.Ordinal)) continue;

                _internalWrite = true;
                textProperty.SetValue(target, desired, null);
            }
            catch {
                TrackedTargets.RemoveAt(i);
            }
            finally {
                _internalWrite = false;
            }
        }
    }

    internal static void HandleTextSetter(object instance, ref string value) {
        if (!ShouldOverride(instance)) return;
        value = GetDesiredSubtitleText();
    }

    internal static void HandleSetTextString(object instance, ref string value) {
        if (!ShouldOverride(instance)) return;
        value = GetDesiredSubtitleText();
    }

    private static bool ShouldOverride(object instance) {
        if (_internalWrite || Plugin.MenuVisible) return false;

        bool timed = Plugin.IsTimedSubtitleActive;
        bool hasActive = !string.IsNullOrWhiteSpace(Plugin.ActiveSubtitle) && Time.unscaledTime <= Plugin.ActiveSubtitleUntil;
        if (!timed && !hasActive) return false;
        if (!IsCandidateSubtitleTarget(instance)) return false;

        TrackTarget(instance);
        CaptureStyleFromOnce(instance);
        return true;
    }

    private static string GetDesiredSubtitleText() {
        bool timed = Plugin.IsTimedSubtitleActive;
        bool hasActive = !string.IsNullOrWhiteSpace(Plugin.ActiveSubtitle) && Time.unscaledTime <= Plugin.ActiveSubtitleUntil;
        return timed ? (Plugin.ActiveSubtitle ?? string.Empty) : (hasActive ? Plugin.ActiveSubtitle : string.Empty);
    }

    private static bool IsCandidateSubtitleTarget(object target) {
        return IsBingBongSubtitleTarget(target);
    }

    private static bool IsBingBongSubtitleTarget(object target) {
        if (target is not Component component) return false;

        int instanceId = component.GetInstanceID();
        if (SubtitleTargetCache.TryGetValue(instanceId, out bool cached)) return cached;

        bool isMatch = false;
        try {
            Transform? current = component.transform;
            while (current != null && !isMatch) {
                string name = current.gameObject.name;
                if (IsExcludedName(name)) {
                    current = current.parent;
                    continue;
                }

                for (int i = 0; i < SubtitleHierarchyKeywords.Length; i++) {
                    if (name.IndexOf(SubtitleHierarchyKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0) {
                        isMatch = true;
                        break;
                    }
                }

                current = current.parent;
            }
        }
        catch {
        }

        SubtitleTargetCache[instanceId] = isMatch;
        return isMatch;
    }

    private static bool IsExcludedName(string name) {
        for (int i = 0; i < SubtitleHierarchyExcludeKeywords.Length; i++) {
            if (name.IndexOf(SubtitleHierarchyExcludeKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0) {
                return true;
            }
        }

        return false;
    }

    private static void CaptureStyleFromOnce(object target) {
        if (target is not Component component) return;

        int instanceId = component.GetInstanceID();
        if (StyleCapturedIds.Contains(instanceId)) return;

        StyleCapturedIds.Add(instanceId);
        CaptureStyleFrom(target);
    }

    private static void TrackTarget(object target) {
        if (!TrackedTargets.Contains(target)) {
            TrackedTargets.Add(target);
        }
    }

    private static void CaptureStyleFrom(object target) {
        if (target is not Component component) return;

        try {
            Type targetType = target.GetType();
            CopyColor(targetType, target);
            CopyFontSize(targetType, target);
            CopyFontStyle(targetType, target);
            CopyAlignment(targetType, target);
            CopyFont(targetType, target);
            CopyOutline(targetType, target);

            RectTransform? rectTransform = component.transform as RectTransform;
            Canvas? canvas = component.GetComponentInParent<Canvas>();
            if (rectTransform != null && canvas != null) {
                CapturedScreenRect = ComputeScreenRect(rectTransform, canvas);
            }

            StyleCaptured = true;
        }
        catch {
        }
    }

    private static void CopyColor(Type targetType, object target) {
        PropertyInfo? colorProperty = targetType.GetProperty("color");
        if (colorProperty?.PropertyType != typeof(Color)) return;
        if (colorProperty.GetValue(target) is Color color) {
            CapturedColor = color;
        }
    }

    private static void CopyFontSize(Type targetType, object target) {
        PropertyInfo? fontSizeProperty = targetType.GetProperty("fontSize");
        if (fontSizeProperty == null) return;

        object? value = fontSizeProperty.GetValue(target);
        if (value is int fontSize && fontSize > 0) {
            CapturedFontSize = fontSize;
        }
        else if (value is float tmpFontSize && tmpFontSize > 0f) {
            CapturedFontSize = Mathf.Max(8, Mathf.RoundToInt(tmpFontSize));
        }
    }

    private static void CopyFontStyle(Type targetType, object target) {
        PropertyInfo? fontStyleProperty = targetType.GetProperty("fontStyle");
        if (fontStyleProperty?.GetValue(target) is FontStyle fontStyle) {
            CapturedFontStyle = fontStyle;
        }
    }

    private static void CopyAlignment(Type targetType, object target) {
        PropertyInfo? alignmentProperty = targetType.GetProperty("alignment");
        if (alignmentProperty == null) return;

        object? value = alignmentProperty.GetValue(target);
        if (value is TextAnchor anchor) {
            CapturedAnchor = anchor;
        }
        else if (value != null) {
            CapturedAnchor = MapTmpAlignment(value.ToString());
        }
    }

    private static void CopyFont(Type targetType, object target) {
        PropertyInfo? fontProperty = targetType.GetProperty("font");
        if (fontProperty == null) return;

        object? value = fontProperty.GetValue(target);
        if (value is Font font) {
            CapturedFont = font;
            return;
        }

        if (value == null) return;

        PropertyInfo? sourceFontProperty = value.GetType().GetProperty("sourceFontFile");
        if (sourceFontProperty?.GetValue(value) is Font sourceFont) {
            CapturedFont = sourceFont;
        }
    }

    private static void CopyOutline(Type targetType, object target) {
        PropertyInfo? outlineColorProperty = targetType.GetProperty("outlineColor");
        if (outlineColorProperty?.PropertyType == typeof(Color) && outlineColorProperty.GetValue(target) is Color outlineColor) {
            CapturedOutlineColor = outlineColor;
        }

        PropertyInfo? outlineWidthProperty = targetType.GetProperty("outlineWidth");
        if (outlineWidthProperty == null) return;

        object? value = outlineWidthProperty.GetValue(target);
        if (value is float outlineWidth && outlineWidth > 0f) {
            CapturedOutlineWidth = Mathf.Clamp(outlineWidth * 8f, 1f, 4f);
        }
        else if (value is int outlineWidthInt && outlineWidthInt > 0) {
            CapturedOutlineWidth = Mathf.Clamp(outlineWidthInt, 1f, 4f);
        }
    }

    private static TextAnchor MapTmpAlignment(string? name) {
        if (string.IsNullOrEmpty(name)) return TextAnchor.MiddleCenter;

        string normalized = name.ToLowerInvariant();
        if (normalized.Contains("topleft")) return TextAnchor.UpperLeft;
        if (normalized.Contains("topright")) return TextAnchor.UpperRight;
        if (normalized.Contains("top")) return TextAnchor.UpperCenter;
        if (normalized.Contains("bottomleft")) return TextAnchor.LowerLeft;
        if (normalized.Contains("bottomright")) return TextAnchor.LowerRight;
        if (normalized.Contains("bottom")) return TextAnchor.LowerCenter;
        if (normalized.Contains("left")) return TextAnchor.MiddleLeft;
        if (normalized.Contains("right")) return TextAnchor.MiddleRight;
        return TextAnchor.MiddleCenter;
    }

    private static Rect ComputeScreenRect(RectTransform rectTransform, Canvas canvas) {
        Vector3[] corners = new Vector3[4];
        rectTransform.GetWorldCorners(corners);

        Camera? camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        Vector2 topRight = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);

        float xMin = Mathf.Min(bottomLeft.x, topRight.x);
        float xMax = Mathf.Max(bottomLeft.x, topRight.x);
        float yMin = Mathf.Min(bottomLeft.y, topRight.y);
        float yMax = Mathf.Max(bottomLeft.y, topRight.y);

        float guiTop = Screen.height - yMax;
        return new Rect(xMin, guiTop, xMax - xMin, yMax - yMin);
    }
}
