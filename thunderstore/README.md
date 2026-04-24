# Bing Bong Voice Override

A BepInEx mod for **PEAK** by **PrimeStrat** that lets you replace Bing Bong's SFX with your own audio files. Drop any `.wav` or `.ogg` files into the sounds folder and the mod automatically loads them. Each time Bing Bong would play one of his normal sounds, a random clip from your folder is played instead.

## Adding Custom Audio

1. Locate (or create) the sounds folder:
   ```
   BepInEx/plugins/PrimeStrat-BingBongVoiceOverride/sounds/
   ```
2. Drop any number of `.wav` or `.ogg` audio files into that folder.
3. Launch the game — the mod loads all clips at startup and picks one at random every time Bing Bong would normally make a sound.

> **Tip:** The folder is created automatically on first launch so you just need to add your files.

## Supported Formats

| Format | Notes |
|--------|-------|
| `.wav` | Uncompressed PCM — best quality, larger files. |
| `.ogg` | Ogg Vorbis — compressed, smaller files. |

## Credits

- Author: [PrimeStrat](https://github.com/PrimeStrat)
- Contributor: Ariannasv22
