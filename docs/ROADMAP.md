# Roadmap

## Phase 0 - MVP foundation

Implemented in the first feature branch:

- WPF Windows app
- visible-window selection
- client-area capture
- frame-change detection
- Tesseract Japanese/English OCR
- OCR temporal stabilization
- translation cache
- short dialogue context
- Mock, Ollama, and LibreTranslate providers
- click-through overlay
- Replace and Subtitle modes
- per-window DPI scaling
- unit tests for stabilization and translation caching
- Windows CI

Success criterion:

A user can open a Japanese visual novel in windowed mode, choose its window, and see stable translated text drawn over recognized text regions.

## Phase 1 - Make visual novels good

Priority:

1. Region-of-interest editor so users can draw dialogue, name, and menu OCR zones.
2. Per-region OCR settings and preprocessing.
3. Game profiles saved by executable/window signature.
4. Glossary support for names, places, skills, and UI terminology.
5. Better line grouping and speaker-name detection.
6. Translation batching so a dialogue box is translated as one semantic unit.
7. OCR confidence/debug overlay.
8. Hotkeys for start/stop, freeze, retranslate, and hide overlay.

This phase should make the tool genuinely comfortable for visual novels before broadening to every game genre.

## Phase 2 - Better capture and OCR

Replace GDI capture behind the existing capture boundary with Windows Graphics Capture.

Goals:

- capture occluded windows
- lower latency
- support borderless fullscreen more reliably
- reduce CPU copies
- optionally use GPU textures end-to-end

OCR upgrades:

- PaddleOCR backend
- text-detection model separate from text recognition
- Japanese vertical text support
- stylized font preprocessing
- adaptive thresholding, upscale, and sharpen pipelines
- per-region backend selection

## Phase 3 - Real localization overlay

This is the main differentiation phase.

Replace mode upgrades:

- estimate original foreground/background colors
- remove source glyphs rather than covering the whole line with a rectangle
- background reconstruction or inpainting
- automatic Korean font-size fitting
- outline and shadow matching
- text alignment detection
- multiline reflow inside the original UI box
- UI-safe clipping

Hybrid mode:

- short UI strings replaced in-place
- long dialogue shown as localized dialogue or subtitle
- names and menu labels kept aligned with their original controls

## Phase 4 - Translation quality

- persistent per-game glossary
- translation memory
- character-specific speaking style
- scene and dialogue context memory
- local LLM provider presets
- OpenAI-compatible provider
- DeepL, Azure, and Google provider plugins
- batch translation
- automatic language detection
- optional user correction that feeds translation memory

## Phase 5 - Performance

Targets:

- capture latency below one frame at 60 Hz
- text-change detection below 2 ms on common 1080p scenes
- no OCR work on unchanged dialogue
- asynchronous provider calls with cancellation and debouncing
- translation cache persisted on disk
- bounded memory
- overlay updates without flicker

## Phase 6 - Distribution

- self-contained x64 release
- first-run OCR data installer
- settings and profile UI
- signed binaries
- crash logging with opt-in diagnostics
- portable mode
- update checker
- release packaging via GitHub Actions

## Non-goals for now

- DLL injection
- game-memory hooks
- kernel drivers
- bypassing anti-cheat
- exclusive-fullscreen support at any cost

Those techniques are not necessary to validate the product and create unnecessary compatibility and safety risk.
