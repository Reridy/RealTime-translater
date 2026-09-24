"use strict";

const endpoint = "http://127.0.0.1:47852/v1/push";
const bridgeHeader = "browser-companion-v1";
const inFlightByTab = new Map();

chrome.runtime.onMessage.addListener(
  (message, sender, sendResponse) => {
    if (
      !message ||
      message.type !== "rtt-browser-snapshot" ||
      !message.payload
    ) {
      return false;
    }

    const tabId =
      sender.tab && Number.isInteger(sender.tab.id)
        ? sender.tab.id
        : -1;

    const previous = inFlightByTab.get(tabId);
    if (previous) {
      previous.abort();
    }

    const controller = new AbortController();
    inFlightByTab.set(tabId, controller);

    const payload = {
      ...message.payload,
      extensionPageId:
        tabId >= 0
          ? String(tabId)
          : ""
    };

    void fetch(endpoint, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-RealTime-Translater": bridgeHeader
      },
      body: JSON.stringify(payload),
      cache: "no-store",
      signal: controller.signal
    })
      .then(() => {
        sendResponse({ ok: true });
      })
      .catch(() => {
        // Desktop app may not be running, or a newer snapshot superseded this
        // one. Both are normal realtime states.
        sendResponse({ ok: false });
      })
      .finally(() => {
        if (inFlightByTab.get(tabId) === controller) {
          inFlightByTab.delete(tabId);
        }
      });

    // Keep the MV3 service worker/message channel alive until the loopback
    // request settles.
    return true;
  }
);
