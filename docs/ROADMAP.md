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

## Universal realtime source layer

Implemented on the current feature branch:

- ✅ Auto Source Resolver with continuous source health/freshness promotion and OCR fallback
- ✅ Manifest V3 Browser Companion over localhost
- ✅ YouTube rendered-caption adapter
- ✅ generic visible DOM adapter
- ✅ automatic Fast / Standard / Quality difficulty routing inside the user-selected translation mode
- ✅ failed Fast translations escalate to larger validation/recovery budgets
- ✅ speculative/typewriter translation with debounce, prefix-growth coalescing, stale-request cancellation, and final-sentence promotion
- ✅ source age, route, cache-hit, provider-call, and translation-latency diagnostics
- ⏳ audio/ASR source adapter
- ⏳ Windows UI Automation/accessibility source adapter
- ⏳ browser-extension store packaging and signing

## Phase 1 - Make visual novels good

Priority:

1. Region-of-interest editor so users can draw dialogue, name, and menu OCR zones.
2. Per-region OCR settings and preprocessing.
3. ✅ Game profiles saved automatically by target process.
4. ✅ Per-game glossary support for names, places, skills, and UI terminology.
5. Better line grouping and speaker-name detection.
6. ✅ Translation batching for multiple visible text regions.
7. OCR confidence/debug overlay.
8. ✅ Global hotkeys for fresh retranslate, start/stop, hide/show overlay, and cycling overlay mode.
9. ✅ Per-game OCR region presets for full-window, bottom-dialogue, and center-focused capture.

This phase should make the tool genuinely comfortable for visual novels before broadening to every game genre.

## Phase 2 - Better capture and OCR

Windows Graphics Capture is now the primary capture backend, with automatic GDI fallback.

Completed:

- direct HWND capture through Windows Graphics Capture
- free-threaded frame pool
- client-area cropping so OCR coordinates stay compatible with the existing overlay
- screenshot-visible overlay mode without self-OCR while WGC is active
- backend status reporting and GDI fallback

Remaining capture goals:

- reduce the current GPU -> SoftwareBitmap -> CPU bitmap copy cost
- support borderless fullscreen more reliably across more games
- keep GPU textures end-to-end for OCR backends that can consume them
- improve resize/recreate behavior and capture diagnostics

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

- ✅ Smart mode replaces precise/short UI strings in-place
- ✅ Smart mode uses compact subtitles for long or poorly constrained text
- ✅ source glyphs are masked before Smart subtitle fallback
- names and menu labels remain aligned with their original controls when Unity layout metadata is available

## Phase 4 - Translation quality

- ✅ persistent per-game glossary
- ✅ persistent translation cache / lightweight translation memory
- ✅ current-speaker context for local LLM prompts
- ✅ short scene/dialogue continuity memory
- ✅ local LLM speed/quality presets (Fast / Balanced / Quality)
- OpenAI-compatible provider
- DeepL, Azure, and Google provider plugins
- ✅ batch translation
- ✅ automatic source-language detection for adapter text
- ✅ user correction editor that feeds per-game translation memory

## Phase 5 - Performance

Targets:

- capture latency below one frame at 60 Hz
- text-change detection below 2 ms on common 1080p scenes
- no OCR work on unchanged dialogue
- ✅ asynchronous provider calls with cancellation, prefix coalescing, and speculative debouncing
- translation cache persisted on disk
- bounded memory
- overlay updates without flicker

## Phase 6 - Distribution

- ✅ self-contained x64 release packaging script
- first-run OCR data installer
- ✅ persistent settings and automatic per-game profiles
- signed binaries
- ✅ privacy-safe local crash/status diagnostics
- portable mode
- update checker
- ✅ manual/tag-triggered GitHub Actions release artifact packaging

## Non-goals for now

- DLL injection
- game-memory hooks
- kernel drivers
- bypassing anti-cheat
- exclusive-fullscreen support at any cost

Those techniques are not necessary to validate the product and create unnecessary compatibility and safety risk.
