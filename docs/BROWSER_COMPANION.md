# Browser Companion

The Browser Companion gives RealTime Translater structured browser text before OCR is needed.

## Supported paths

- YouTube: reads the currently rendered caption segments directly from the player DOM.
- Generic websites: reads visible semantic text from dialogs, live regions, article/main text, headings, buttons, and labels.
- If the companion is unavailable or stale, **Auto (Recommended)** falls back to OCR.

No browsing history is uploaded. The extension sends only the current visible page title/URL metadata and the visible text regions to `127.0.0.1:47852` on the same PC.

## Install for development

1. Open `chrome://extensions` in Chrome/Edge/Brave (Edge uses `edge://extensions`).
2. Enable **Developer mode**.
3. Choose **Load unpacked**.
4. Select the repository's `browser-extension` folder.
5. Start RealTime Translater and choose **Auto (Recommended)** or **Browser Companion + OCR fallback**.

YouTube captions should then appear in Diagnostics/Status as **YouTube Captions**. Other pages report **Browser DOM**.

The companion is Manifest V3 and requires no build step.
