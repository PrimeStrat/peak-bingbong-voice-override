# Changelog

## 1.0.0
- Initial release.
- Automatically loads `.wav` and `.ogg` files from `BepInEx/plugins/PrimeStrat-BingBongVoiceOverride/sounds/`.
- Patches `AudioSource.Play` and `AudioSource.PlayOneShot` on Bing Bong game objects to play random custom clips.
- Config options: master enable toggle and volume multiplier.
- Subtitles are now driven through PEAK's native Bing Bong UI by mirroring the BingBongVoiceLineAPI technique: rewrite `Action_AskBingBong.responses[]` with our clips and write subtitle text into `LocalizedText.mainTable`.
- Audio plays through PEAK's own Bing Bong AudioSource by default, so subtitles, mouth animation and volume falloff match vanilla.
- Added mirror-on-drop: if the player drops Bing Bong mid-line, playback resumes on the plugin's detached source so the clip still finishes.
- Added config `Subtitles.UseNativeBingBongAPI` (default true). Set to false to fall back to the previous AudioSource hijack and Harmony text-setter patches.
- Timed sing-along subtitles still update per cue, but the native UI may not refresh mid-clip on every PEAK build; enable `ShowSubtitleOverlay` if cues stop animating.
