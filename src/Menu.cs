using System;
using System.Collections.Generic;
using UnityEngine;

namespace BingBongVoiceOverride;

internal class Menu : MonoBehaviour
{
    private const float windowWidth = 600f;
    private const float windowHeight = 400f;

    private Rect windowRect = new Rect(100f, 100f, windowWidth, windowHeight);
    private Vector2 clipsScroll = Vector2.zero;
    private Vector2 settingsScroll = Vector2.zero;

    private GUISkin? skin;
    private Texture2D? texDark;
    private Texture2D? texMid;
    private Texture2D? texButton;
    private Texture2D? texButtonHover;
    private Texture2D? texHeader;

    private void Update()
    {
        if (!Plugin.EnableMod.Value)
        {
            if (Plugin.MenuVisible)
            {
                Plugin.SetMenuVisible(false);
            }
            return;
        }

        if (Input.GetKeyDown(Plugin.MenuToggleKey.Value))
        {
            Plugin.SetMenuVisible(!Plugin.MenuVisible);
        }
    }

    private void OnGUI()
    {
        if (!Plugin.MenuVisible) return;

        GUISkin skinLocal = GetSkin();
        GUI.skin = skinLocal;

        windowRect = GUI.Window(0, windowRect, DrawWindow, "Bing Bong Voice Override");
    }

    private void DrawWindow(int windowID)
    {
        GUILayout.BeginVertical();

        DrawStatusSection();
        GUILayout.Space(10f);
        DrawClipsSection();
        GUILayout.Space(10f);
        DrawSettingsSection();
        GUILayout.Space(10f);
        DrawButtonsSection();

        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    private void DrawStatusSection()
    {
        GUILayout.Label("Status", GUI.skin.box);
        GUILayout.Label($"Clips Loaded: {Plugin.CustomClips.Count}");
        GUILayout.Label($"Audio Playing: {Plugin.IsBingBongAudioActive}");
        if (Plugin.IsBingBongAudioActive && Plugin.PluginAudioSource.clip != null)
        {
            GUILayout.Label($"Current Clip: {Plugin.PluginAudioSource.clip.name}");
        }
    }

    private void DrawClipsSection()
    {
        GUILayout.Label("Loaded Clips", GUI.skin.box);

        clipsScroll = GUILayout.BeginScrollView(clipsScroll, GUILayout.Height(100f));

        if (Plugin.CustomClips.Count == 0)
        {
            GUILayout.Label("No clips loaded. Add .ogg or .wav files to the sounds folder.");
        }
        else
        {
            for (int i = 0; i < Plugin.CustomClips.Count; i++)
            {
                AudioClip clip = Plugin.CustomClips[i];
                string name = clip.name;
                bool enabled = Plugin.EnabledClips.ContainsKey(name) && Plugin.EnabledClips[name];

                GUILayout.BeginHorizontal();
                bool newEnabled = GUILayout.Toggle(enabled, name, GUILayout.Width(300f));
                if (newEnabled != enabled)
                {
                    Plugin.EnabledClips[name] = newEnabled;
                }

                GUILayout.EndHorizontal();
            }
        }

        GUILayout.EndScrollView();
    }

    private void DrawSettingsSection()
    {
        GUILayout.Label("Settings", GUI.skin.box);

        settingsScroll = GUILayout.BeginScrollView(settingsScroll, GUILayout.Height(80f));

        float volume = Plugin.VolumeMultiplier.Value;
        GUILayout.BeginHorizontal();
        GUILayout.Label("Volume:", GUILayout.Width(80f));
        volume = GUILayout.HorizontalSlider(volume, 0f, 2f, GUILayout.Width(150f));
        GUILayout.Label($"{volume:F2}", GUILayout.Width(50f));
        GUILayout.EndHorizontal();

        if (Math.Abs(volume - Plugin.VolumeMultiplier.Value) > 0.01f)
        {
            Plugin.VolumeMultiplier.Value = volume;
        }

        if (GUILayout.Toggle(Plugin.UseNativeBingBongAPI.Value, "Use Native Bing Bong API"))
        {
            if (!Plugin.UseNativeBingBongAPI.Value)
            {
                Plugin.UseNativeBingBongAPI.Value = true;
            }
        }
        else
        {
            if (Plugin.UseNativeBingBongAPI.Value)
            {
                Plugin.UseNativeBingBongAPI.Value = false;
            }
        }

        GUILayout.EndScrollView();
    }

    private void DrawButtonsSection()
    {
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Close", GUILayout.Width(80f)))
        {
            Plugin.SetMenuVisible(false);
        }

        GUILayout.EndHorizontal();
    }

    private GUISkin GetSkin()
    {
        if (skin != null) return skin;

        texDark = MakeTex(new Color(0.10f, 0.10f, 0.13f, 0.97f));
        texMid = MakeTex(new Color(0.17f, 0.17f, 0.22f, 1f));
        texButton = MakeTex(new Color(0.22f, 0.22f, 0.30f, 1f));
        texButtonHover = MakeTex(new Color(0.30f, 0.30f, 0.42f, 1f));
        texHeader = MakeTex(new Color(0.14f, 0.14f, 0.19f, 1f));

        skin = Instantiate(GUI.skin);
        skin.window.normal.background = texDark;
        skin.window.normal.textColor = Color.white;
        skin.box.normal.background = texHeader;
        skin.box.normal.textColor = Color.white;
        skin.button.normal.background = texButton;
        skin.button.normal.textColor = Color.white;
        skin.button.hover.background = texButtonHover;
        skin.button.active.background = texMid;
        skin.toggle.normal.background = texButton;
        skin.toggle.normal.textColor = Color.white;

        return skin;
    }

    private static Texture2D MakeTex(Color c)
    {
        Texture2D t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }
}
