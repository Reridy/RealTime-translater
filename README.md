# RealTime Translater

A Windows-first real-time game screen translation overlay.

The MVP captures a selected game window, skips unchanged frames, runs OCR, stabilizes noisy OCR output, translates only new text, and renders the Korean result over the original screen position with a click-through overlay.

## MVP status

Implemented on the feature/mvp-realtime-translation branch:

- Select any visible Windows desktop game/window.
- Capture the client area with GDI CopyFromScreen.
- Ignore mostly unchanged frames using a lightweight frame-difference detector.
- OCR text lines with Tesseract 5.
- Stabilize OCR over multiple frames before translating.
- Cache translations to avoid repeat network/model calls.
- Translate through Ollama, LibreTranslate, or a built-in mock provider.
- Preserve recent dialogue context for translation prompts.
- Render translated text in the OCR bounding box using a transparent, click-through, topmost WPF overlay.
- Exclude the overlay from Windows screen capture where WDA_EXCLUDEFROMCAPTURE is supported.
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

- GDI screen capture requires the target window to be visible and can be affected by occlusion.
- Exclusive fullscreen games are not supported reliably yet.
- Tesseract OCR quality depends heavily on game font/background and installed traineddata.
- Replace mode currently uses a semi-transparent background rectangle instead of reconstructing the underlying game texture.
- Anti-cheat protected games may behave differently. The app does not inject into the game process or read game memory.
- OCR is full-window in this MVP; configurable regions and automatic text detection are planned.

See docs/ARCHITECTURE.md and docs/ROADMAP.md for the design and next implementation phases.
