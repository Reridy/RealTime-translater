# RealTime Translater

A Windows-first real-time game screen translation overlay.

The MVP captures a selected game window, skips unchanged frames, runs OCR, stabilizes noisy OCR output, translates only new text, and renders the Korean result over the original screen position with a click-through overlay.

## MVP status

Implemented on the feature/mvp-realtime-translation branch:

- Select any visible Windows desktop game/window.
- Capture the selected window with Windows Graphics Capture, with automatic GDI fallback if WGC cannot initialize.
- Ignore mostly unchanged frames using a lightweight frame-difference detector.
- OCR text lines with Tesseract 5.
- Optional read-only Unity/BepInEx text adapter that can feed exact visible TMP_Text / Unity UI strings to the translation pipeline, with automatic OCR fallback.
- Stabilize OCR over multiple frames before translating.
- Cache translations to avoid repeat network/model calls, including a persistent per-user cache across restarts.
- Translate through Ollama, LibreTranslate, or a built-in mock provider.
- Preserve recent dialogue context for translation prompts.
- Render translated text using a transparent, click-through, topmost WPF overlay. Replace mode now samples the local game background, erases only the original glyph rectangle, and lays Korean out inside the game's original Unity text container when adapter metadata is available.
- Optional screenshot-visible overlay mode. With WGC active, the app captures the target window directly so its own overlay is not fed back into OCR.
- Exclude the overlay from Windows screen capture by default using WDA_EXCLUDEFROMCAPTURE.
- CI build/test workflow for Windows.

## Quick start

Requirements:

- Windows 10 2004+ or Windows 11
- .NET 8 SDK
- Windowed or borderless-windowed game mode recommended

1. Download OCR language data:

    powershell -ExecutionPolicy Bypass -File scripts/download-tessdata.ps1

2. Build:

    dotnet restore
    dotnet build RealTimeTranslater.sln

3. Run:

    dotnet run --project src/RealTimeTranslater.App

4. Pick the game window, choose an OCR language and translation provider, then press Start.

For real translation without a cloud API key, run a local Ollama server and select Ollama in the app. The model name and endpoint are editable in the UI.

## Current limitations

This is an MVP, not yet a universal game translator.

- Windows Graphics Capture is the primary backend. If it is unavailable, the app falls back to GDI CopyFromScreen; that fallback requires the target window to stay visible and can be affected by occlusion.
- Minimized target windows are not captured.
- Exclusive fullscreen games are not supported reliably yet.
- Tesseract OCR quality depends heavily on game font/background and installed traineddata.
- Replace mode uses local background-color reconstruction rather than full image inpainting. It works especially well on dialogue boxes and flat UI backgrounds; highly textured text backgrounds can still reveal a small patched area.
- Anti-cheat protected games may behave differently. The app does not inject into the game process or read game memory.
- OCR is full-window in this MVP; configurable regions and automatic text detection are planned.

For the Unity/BepInEx proof-of-concept adapter, see docs/UNITY_ADAPTER.md.

See docs/ARCHITECTURE.md and docs/ROADMAP.md for the design and next implementation phases.


## Release packaging

A reproducible Windows x64 package can be created with:

    powershell -ExecutionPolicy Bypass -File scripts/publish-windows.ps1

The script produces a self-contained .NET 8 folder and ZIP under `dist/`, includes English/Japanese Tesseract data, and copies first-run/setup guidance. End-user settings and the persistent translation cache are stored under `%LOCALAPPDATA%\RealTimeTranslater` so the installation directory can remain read-only.

For the lowest-latency local translation path, the current recommended Ollama model is `translategemma:4b`. Multiple visible regions are batched, the model is kept warm, completed sentences skip unnecessary stabilization delay, and repeated lines are served from the persistent cache.
