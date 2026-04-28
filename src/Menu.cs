using System;
using System.IO;
using UnityEngine;

namespace BingBongVoiceOverride;

internal class Menu : MonoBehaviour
{
    private const float windowWidth = 600f;
    private const float windowHeight = 400f;
    private const int tabSettings = 0;
    private const int tabSounds = 1;

    private Rect windowRect = new(100f, 100f, windowWidth, windowHeight);
    private Vector2 clipsScroll = Vector2.zero;
    private Vector2 settingsScroll = Vector2.zero;
    private int activeTab = tabSounds;

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

        DrawTabSection();
        GUILayout.Space(10f);
        if (activeTab == tabSettings)
        {
            DrawSettingsSection();
        }
        else
        {
            DrawStatusSection();
            GUILayout.Space(8f);
            DrawSoundActionsRow();
            GUILayout.Space(8f);
            DrawClipsSection();
        }
        GUILayout.Space(10f);
        DrawButtonsSection();

        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    private void DrawTabSection()
    {
        GUILayout.Label("Debug Menu", GUI.skin.box);
        activeTab = GUILayout.Toolbar(activeTab, new[] { "Settings", "Sounds" });
    }

    private static void DrawSoundActionsRow()
    {
        GUILayout.BeginHorizontal();
        DrawOpenSoundsFolderButton();
        GUILayout.Space(8f);
        DrawRefreshSoundsButton();
        GUILayout.EndHorizontal();

        if (Plugin.IsReloadingSounds)
        {
            GUILayout.Label("Reloading sounds...", GUILayout.Width(200f));
        }
    }

    private static void DrawOpenSoundsFolderButton()
    {
        if (!GUILayout.Button("Open Sounds Folder", GUILayout.Width(180f)))
        {
            return;
        }

        string folder = Plugin.SoundsFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Plugin.Log.LogWarning("[Menu] Sounds folder is not available yet.");
            return;
        }

        string normalizedPath = folder.Replace('\\', '/');
        Application.OpenURL("file:///" + normalizedPath);
    }

    private static void DrawRefreshSoundsButton()
    {
        if (Plugin.IsReloadingSounds)
        {
            GUI.enabled = false;
        }

        bool clicked = GUILayout.Button("Refresh Sounds", GUILayout.Width(140f));
        GUI.enabled = true;

        if (!clicked)
        {
            return;
        }

        Plugin.RequestReloadSounds();
    }

    private void DrawStatusSection()
    {
        GUILayout.Label("Status", GUI.skin.box);
        GUILayout.Label($"Clips Loaded: {Plugin.CustomClips.Count}");
        GUILayout.Label($"Audio Playing: {Plugin.IsBingBongAudioActive}");
        AudioSource? pluginAudioSource = Plugin.PluginAudioSource;
        if (pluginAudioSource != null && pluginAudioSource.isPlaying && pluginAudioSource.clip != null)
        {
            GUILayout.Label($"Current Clip: {pluginAudioSource.clip.name}");
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

        Texture2D dark = MakeTex(new Color(0.10f, 0.10f, 0.13f, 0.97f));
        Texture2D mid = MakeTex(new Color(0.17f, 0.17f, 0.22f, 1f));
        Texture2D button = MakeTex(new Color(0.22f, 0.22f, 0.30f, 1f));
        Texture2D buttonHover = MakeTex(new Color(0.30f, 0.30f, 0.42f, 1f));
        Texture2D header = MakeTex(new Color(0.14f, 0.14f, 0.19f, 1f));

        texDark = dark;
        texMid = mid;
        texButton = button;
        texButtonHover = buttonHover;
        texHeader = header;

        skin = GUI.skin != null ? Instantiate(GUI.skin) : ScriptableObject.CreateInstance<GUISkin>();
        skin.window.normal.background = dark;
        skin.window.normal.textColor = Color.white;
        skin.box.normal.background = header;
        skin.box.normal.textColor = Color.white;
        skin.button.normal.background = button;
        skin.button.normal.textColor = Color.white;
        skin.button.hover.background = buttonHover;
        skin.button.active.background = mid;
        skin.toggle.normal.background = button;
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
