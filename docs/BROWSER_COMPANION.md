# Browser Companion

The Browser Companion gives RealTime Translater structured browser text before OCR is needed.

## Supported paths

- YouTube: reads the currently rendered caption segments directly from the player DOM and coalesces them into one low-latency translation unit.
- YouTube without rendered captions: intentionally yields to OCR today instead of translating page chrome; an audio/ASR source can occupy this fallback slot later.
- Generic websites: reads visible semantic text from dialogs, live regions, article/main text, headings, buttons, and labels.
- Multiple visible browser windows/tabs are tracked independently and matched to the selected browser window by title.
- If the companion is unavailable, stale, hidden, or does not match the selected target, **Auto (Recommended)** falls back immediately to another source.

No browsing history is uploaded. The extension sends only the current visible page title/URL metadata and visible text regions to `127.0.0.1:47852` on the same PC. The content script never talks to localhost directly: a Manifest V3 service worker owns the loopback request, coalesces superseded per-tab updates, and adds a bridge marker that the desktop receiver validates. Normal web-page origins are rejected by the desktop bridge. The listener is loopback-only, so it does not require a Windows HTTP URL reservation or administrator privileges.

## Install for development

1. Open `chrome://extensions` in Chrome/Edge/Brave (Edge uses `edge://extensions`).
2. Enable **Developer mode**.
3. Choose **Load unpacked**.
4. Select the repository's `browser-extension` folder.
5. Start RealTime Translater and choose **Auto (Recommended)** or **Browser Companion + OCR fallback**.

YouTube captions should then appear in Diagnostics/Status as **YouTube Captions**. Other pages report **Browser DOM**. While YouTube is still growing a caption, the companion marks it partial; after a short quiet period it promotes the exact same caption to final so the translator can persist it without issuing a second model call.

The companion is Manifest V3 and requires no build step. After pulling an update to the repository, open the extensions page and press **Reload** on RealTime Translater Browser Companion so changes to the service worker/content script take effect.
