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
RealTimeTranslater Unity Adapter 0.3.1 loaded; tight text bounds and faster text polling enabled.
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
      "objectName": "DialogueBody",
      "hierarchy": "Canvas/DialoguePanel/DialogueBody",
      "selectableName": "",
      "isSelectable": false,
      "isButton": false,
      "isChoiceLike": false,
      "isSpeakerLike": false,
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

- The adapter scans active `TextMeshProUGUI` and legacy `UnityEngine.UI.Text` objects about every 120 ms.
- For TextMeshProUGUI, version 0.3.0 publishes the actual rendered text bounds (`textBounds`) when available instead of the whole RectTransform. Replace mode therefore masks only the glyph area plus a very small margin; RectTransform bounds remain as a fallback.
- Transparent text hidden by `Graphic.color.a` or parent `CanvasGroup.alpha` is rejected before publishing.
- Each region also carries its Unity object name and hierarchy path, plus real Unity `Selectable`/`Button` ancestry. The adapter marks choice-like and speaker-like objects so the desktop app can distinguish dialogue text from SKIP/AUTO controls and nameplates.
- The desktop UI exposes a **Translate dialogue / choices only** checkbox for Unity Adapter mode. When checked, dialogue/choice filtering is used in both Subtitle and Replace modes. When unchecked, all meaningful visible Unity UI text is translated (up to the safety cap).
- Dialogue-only mode is intentionally conservative: it rejects tooltip/stat blocks, short name-only strings, and common menu/status noise, then ranks likely dialogue/choice sentences.
- Unity Adapter translation uses source language `auto` so mixed Japanese/English UI can be handled without tying it to the OCR language selector.
- World-space text and unusual custom renderers may have imperfect rectangles.
- Complex masks/clipping can still leave some visually hidden objects in the feed.
- Games that do not use Unity UI/TextMeshPro still require OCR or a game-specific adapter.
- The adapter project is intentionally not part of the main CI solution because it compiles against DLLs from the user's installed game. The desktop receiver and fallback pipeline are built by normal CI.


## Translation quality and subtitle behavior

For Ollama, the desktop app keeps the model warm for 30 minutes, uses a short two-line localization context, a low-temperature localization prompt, and a bounded output budget to reduce latency while keeping Korean dialogue natural.

Subtitle mode uses a compact centered panel instead of a wide opaque bar. Its width is capped, the background is lighter, text has a small shadow, and font size scales with the captured game window.

Dialogue-only filtering also rejects short HUD abbreviations such as `Cond.`, `AP`, `HP`, and similar labels unless the Unity hierarchy explicitly identifies them as dialogue/choice content.

The status line reports the most recent Unity translation latency in milliseconds after a new line is translated.


## Recommended local translation model

For reliable English/Japanese -> Korean translation, the current recommended Ollama model is `translategemma:4b`. RealTime Translater detects TranslateGemma model names and uses the prompt format intended for that dedicated translation family.

Install it with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup-recommended-translation-model.ps1
```

General chat models such as Qwen remain supported. For those models, the app uses structured output first, validates the Korean result, falls back to a strict plain-text retry when needed, and rejects English/Chinese leakage, repeated garbage, role labels, and malformed output.

Ollama HTTP/server failures no longer terminate the capture pipeline. The current line is left unobscured, the status reports the error, and the translator retries after a short cooldown.


## Replace mode bounds

Replace mode is intentionally conservative about screen coverage. For TextMeshPro UI, the Unity adapter reports the tight rendered-text rectangle rather than the full dialogue or profile panel. The desktop overlay adds only a few pixels of padding, shrinks Korean text to fit before expanding the mask, and limits any height growth for long paragraphs.

Because this requires adapter-side geometry data, upgrading from adapter 0.2.x to 0.3.x requires rebuilding and reinstalling the BepInEx adapter with `scripts/build-unity-adapter.ps1`, then restarting the game.


## Reliability improvements

Current realtime behavior is designed to avoid the most common intermittent-miss cases:

- Ollama is warmed before the capture pipeline starts.
- Transient Ollama HTTP/network failures are retried internally with short backoff.
- TranslateGemma gets a second stricter translation attempt if its first output fails Korean quality validation.
- Unity text changes are sampled faster and held briefly for stability before translation, reducing typewriter/partial-line translations.
- Proven dialogue/prose Unity text objects are learned during the session so later very short lines from the same object are not dropped by conservative heuristics.
- If a translation fails, the same Unity line is retried quickly with increasing backoff instead of terminating the pipeline.
- If the game advances while a translation is still running, the stale result is discarded instead of being flashed over the newer line.
- A short Unity adapter disconnect uses a reconnect grace period before OCR fallback, avoiding noisy fallback during momentary IPC interruptions.
- The default desktop capture cadence is 8 FPS; OCR itself is still gated by frame-change/stability logic.
