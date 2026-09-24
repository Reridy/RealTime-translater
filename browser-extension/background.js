"use strict";

const endpoint = "http://127.0.0.1:47852/v1/push";
const bridgeHeader = "browser-companion-v1";

chrome.runtime.onMessage.addListener((message, sender) => {
  if (
    !message ||
    message.type !== "rtt-browser-snapshot" ||
    !message.payload
  ) {
    return;
  }

  const payload = {
    ...message.payload,
    extensionPageId:
      sender.tab && Number.isInteger(sender.tab.id)
        ? String(sender.tab.id)
        : ""
  };

  void fetch(endpoint, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-RealTime-Translater": bridgeHeader
    },
    body: JSON.stringify(payload),
    cache: "no-store"
  }).catch(() => {
    // Desktop app may not be running. The bridge must stay silent.
  });
});
