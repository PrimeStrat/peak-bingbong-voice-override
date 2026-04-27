# Changelog

## 1.0.2
- Fixed dependency listing to use the PEAK-specific BepInExPack.

## 1.0.1
- Fixed clients in rooms where the host lacks the mod being permanently menu-blocked.
- Fixed a double-broadcast cascade that caused clients to mistakenly start their own sync servers on clip reload.
- Added 150ms TTL cache on active clip list to avoid redundant allocations each frame.
- Added sync-busy pause: audio is paused while a file transfer is in progress and resumes automatically.
- Added on-screen sync status toast in the menu HUD.
- Added Music Player and several other QoL additions.
- Network sync join/leave state now uses actual connection flags instead of a one-shot guard, making the design more robust across rapid room transitions.
- Axis blocking during menu open now targets only "Mouse X" and "Mouse Y" explicitly instead of fuzzy name matching.
- Removed dead code and reduced comment noise throughout patch files.

## 1.0.0
- Initial release.
- Automatically loads `.wav` and `.ogg` files from `BepInEx/plugins/PrimeStrat-BingBongVoiceOverride/sounds/`.
- Patches `AudioSource.Play` and `AudioSource.PlayOneShot` on Bing Bong game objects to play random custom clips.
- Config options: master enable toggle and volume multiplier.
- Subtitles are now driven through PEAK's native Bing Bong UI by mirroring the BingBongVoiceLineAPI technique: rewrite `Action_AskBingBong.responses[]` with our clips and write subtitle text into `LocalizedText.mainTable`.
- Audio plays through PEAK's own Bing Bong AudioSource by default, so subtitles, mouth animation and volume falloff match vanilla.
- Added mirror-on-drop: if the player drops Bing Bong mid-line, playback resumes on the plugin's detached source so the clip still finishes.
- Added config `Subtitles.UseNativeBingBongAPI` (default true). Set to false to fall back to the previous AudioSource hijack and Harmony text-setter patches.