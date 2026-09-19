# Architecture

## Product goal

RealTime Translater is a Windows desktop overlay that makes an untranslated game feel closer to a localized build without modifying or injecting into the game process.

The target experience is:

game window -> detect changed text -> OCR -> stabilize -> context-aware translation -> draw Korean in the same screen location

The MVP deliberately stays outside the game process. This reduces compatibility and anti-cheat risk compared with memory hooks or DLL injection, while still working with many visual novels, RPGs, strategy games, and menu-heavy games.

## Component map

### 1. Window discovery

WindowFinder enumerates visible top-level Windows and filters out this app. The selected HWND is the stable target identity for capture.

### 2. Capture

WindowCaptureService captures the client area using Graphics.CopyFromScreen.

Why GDI first:

- very small implementation surface
- easy to debug
- no game-process access
- enough for a functional MVP

Known tradeoff: the window must remain visible and exclusive fullscreen is unreliable.

The capture layer is intentionally isolated so it can be replaced with Windows Graphics Capture later without changing OCR, translation, or overlay logic.

### 3. Frame change detector

OCR is expensive. FrameChangeDetector downsamples the capture to 32x18 grayscale samples and compares the current sample with the previous one.

If the normalized average difference is below the configured threshold, the pipeline skips OCR.

After a changed frame is seen, the pipeline intentionally forces enough additional OCR passes to satisfy stabilization. This prevents a common bug where the first changed frame is OCRed once but an identical second frame is skipped before the stabilizer can confirm it.

### 4. OCR

TesseractOcrService uses Tesseract 5 and returns line-level TextRegion objects containing recognized text, pixel bounding box, and confidence.

The default language set is jpn+eng.

Tesseract is the MVP backend because it is local, deterministic, widely available, and returns bounding boxes. The OCR boundary is kept small so a future backend can use PaddleOCR, Windows OCR, or a GPU text detector.

### 5. OCR stabilization

FrameTextStabilizer waits until the normalized recognized text repeats across a configurable number of OCR frames.

This addresses transient OCR errors such as a small glyph changing between consecutive reads. Whitespace and minor bounding-box movement do not reset stabilization because the stable key is based on normalized text, not exact coordinates.

Future versions should use per-region temporal matching and edit-distance tolerance rather than whole-frame exact text equality.

### 6. Translation

TranslationCoordinator provides a provider abstraction, translation cache, short recent-dialogue context, and conversion from TextRegion to TranslatedRegion.

Providers currently implemented:

- Mock: verifies OCR and overlay layout with no external service
- Ollama: local LLM translation via the Ollama chat API
- LibreTranslate: simple HTTP translation backend

The provider interface makes DeepL, Google Cloud Translation, Azure Translator, OpenAI-compatible endpoints, or custom local models straightforward additions.

### 7. Overlay

OverlayWindow is a borderless transparent WPF window.

It is always on top, excluded from the taskbar, click-through, non-activating, and marked WDA_EXCLUDEFROMCAPTURE when supported.

Replace mode draws a dark translucent rectangle over each OCR line and places Korean text inside the original bounding box.

Subtitle mode combines current translations into a bottom-center subtitle panel.

The overlay uses the target window DPI to map capture pixels to WPF device-independent units.

### 8. Feedback-loop prevention

An overlay can accidentally be captured by a screen-grab implementation, causing OCR to read its own Korean translation.

The MVP calls SetWindowDisplayAffinity with WDA_EXCLUDEFROMCAPTURE on the overlay window. On supported Windows versions this keeps the overlay out of capture APIs.

If a future capture backend does not respect this flag, the capture layer should explicitly exclude the overlay surface or temporarily hide it during capture.

## Data flow

1. User selects a game window.
2. CaptureFrame contains a Bitmap, absolute client-area screen bounds, and DPI scale.
3. FrameChangeDetector decides whether OCR is needed.
4. TesseractOcrService returns TextRegion lines.
5. FrameTextStabilizer waits for stable text.
6. TranslationCoordinator checks cache, supplies context, and calls a provider.
7. OverlayWindow renders TranslatedRegion values.
8. The loop repeats at the configured capture FPS.

## Threading

The pipeline runs on a background task. Capture, OCR, stabilization, and translation remain off the WPF UI thread. Only overlay rendering and status text updates are dispatched to the WPF dispatcher.

## Security and privacy

The application does not inject code into the game, inspect game memory, install kernel drivers, or send screenshots to the translation provider.

Only recognized text is sent to a network translation provider. With Ollama, translation can remain fully local.

## Product differentiation

The long-term direction is not merely showing a translation panel. The goal is to behave like a localization layer:

- preserve original text positions
- understand dialogue context
- reuse game-specific glossary entries
- match font sizing and visual layout
- avoid retranslating unchanged text
- eventually reconstruct or inpaint the original text background

That is the main product distinction to protect as the project evolves.
