# Unity / BepInEx text adapter

The Unity adapter is an optional read-only companion plugin for Unity Mono games that already use BepInEx.

It does not modify dialogue, patch methods, write game memory, alter saves, or replace TextMeshPro text. It only enumerates active Unity UI text objects and publishes their current text plus screen rectangle to RealTime Translater through a localhost TCP connection.

## Why use it

Screen OCR remains the universal fallback, but a Unity adapter can provide the exact string that Unity is already displaying.

Data flow:

```
Unity game
  -> active TextMeshProUGUI / UnityEngine.UI.Text
  -> RealTimeTranslater.UnityBepInEx
  -> localhost TCP: 127.0.0.1:47851
  -> RealTime Translater
  -> translation provider
  -> overlay
```

When adapter data is missing or older than two seconds, the desktop app automatically falls back to OCR.

## Claire / Kurea Struggle proof of concept

The inspected game installation already contains:

- BepInEx
- `Assembly-CSharp.dll`
- `Unity.TextMeshPro.dll`
- `UnityEngine.UI.dll`
- existing English/readability/runtime-fix plugins

That makes it a suitable first Unity adapter target.

The existing English translation patch can remain installed. In that case the adapter observes the final visible English strings and RealTime Translater can translate English -> Korean. If the game is switched back to Japanese, use `jpn` or `jpn+eng` as the source/OCR language.

## Build and install

From the RealTimeTranslater repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-unity-adapter.ps1 -GameDir "C:\path\to\Claire_struggle_V1.0.4"
```

The script validates the required game assemblies, builds the adapter against the game's own BepInEx/Unity DLLs, and copies:

```
RealTimeTranslater.UnityBepInEx.dll
```

to:

```
<GameDir>\BepInEx\plugins\RealTimeTranslaterUnityAdapter\
```

No game DLL is copied into this repository or redistributed.

## Test

1. Build/install the adapter.
2. Start RealTime Translater.
3. Select the game window.
4. Select `Unity Adapter + OCR fallback` under **Text source**.
5. Keep **Translation provider** on `Mock` for the first test.
6. Start the game normally.
7. Press **Start** in RealTime Translater.

A successful connection changes the desktop status to something similar to:

```
Running · Windows Graphics Capture · Unity Adapter 12 text region(s)
```

If the status instead says `Unity Adapter waiting, OCR fallback`, check:

```
<GameDir>\BepInEx\LogOutput.log
```

for:

```
RealTimeTranslater Unity Adapter 0.1.0 loaded; read-only text capture enabled.
```

## Protocol

The adapter sends one UTF-8 JSON object per line over a loopback-only TCP connection to `127.0.0.1:47851`.

Example:

```json
{
  "protocol": 1,
  "screenWidth": 1920,
  "screenHeight": 1080,
  "regions": [
    {
      "text": "Hidden Stats",
      "kind": "TMP",
      "x": 120,
      "y": 210,
      "width": 180,
      "height": 40
    }
  ]
}
```

Coordinates use Unity's current render resolution and a top-left origin. The desktop app rescales them to the captured client area.

## Current limitations

- The adapter scans active `TextMeshProUGUI` and legacy `UnityEngine.UI.Text` objects every 200 ms.
- Transparent text hidden by `Graphic.color.a` or parent `CanvasGroup.alpha` is rejected before publishing.
- Each region also carries its Unity object name and hierarchy path so the desktop app can classify dialogue-like text separately from status/UI noise.
- Subtitle mode ranks likely dialogue/speaker/choice text and limits the number of translated regions; Replace mode remains broader.
- Unity Adapter translation uses source language `auto` so mixed Japanese/English UI can be handled without tying it to the OCR language selector.
- World-space text and unusual custom renderers may have imperfect rectangles.
- Complex masks/clipping can still leave some visually hidden objects in the feed.
- Games that do not use Unity UI/TextMeshPro still require OCR or a game-specific adapter.
- The adapter project is intentionally not part of the main CI solution because it compiles against DLLs from the user's installed game. The desktop receiver and fallback pipeline are built by normal CI.
