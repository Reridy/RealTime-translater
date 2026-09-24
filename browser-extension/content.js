(() => {
  "use strict";

  const endpoint = "http://127.0.0.1:47852/v1/push";
  const youtubeHost = /(^|\.)youtube\.com$/i;
  let lastKey = "";
  let lastSentAt = 0;
  let timer = 0;

  function cleanText(value) {
    return String(value || "")
      .replace(/\s+/g, " ")
      .trim();
  }

  function visibleRect(element) {
    if (!(element instanceof Element)) return null;

    const style = getComputedStyle(element);
    if (
      style.display === "none" ||
      style.visibility === "hidden" ||
      Number(style.opacity || "1") < 0.05
    ) {
      return null;
    }

    const rect = element.getBoundingClientRect();
    if (
      rect.width < 4 ||
      rect.height < 4 ||
      rect.bottom <= 0 ||
      rect.right <= 0 ||
      rect.top >= innerHeight ||
      rect.left >= innerWidth
    ) {
      return null;
    }

    return rect;
  }

  function regionFromElement(element, text, role, partial = false) {
    const rect = visibleRect(element);
    if (!rect) return null;

    return {
      text: cleanText(text),
      role,
      x: Math.max(0, rect.left),
      y: Math.max(0, rect.top),
      width: Math.min(innerWidth, rect.right) - Math.max(0, rect.left),
      height: Math.min(innerHeight, rect.bottom) - Math.max(0, rect.top),
      partial
    };
  }

  function youtubeCaptions() {
    if (!youtubeHost.test(location.hostname)) return [];

    const segments = Array.from(
      document.querySelectorAll(".ytp-caption-segment")
    );

    const regions = segments
      .map((segment) =>
        regionFromElement(
          segment,
          segment.textContent,
          "caption",
          true
        )
      )
      .filter((region) => region && region.text.length > 0);

    if (regions.length > 0) return regions;

    const containers = Array.from(
      document.querySelectorAll(".caption-window, .ytp-caption-window-container")
    );

    return containers
      .map((element) =>
        regionFromElement(
          element,
          element.textContent,
          "caption",
          true
        )
      )
      .filter((region) => region && region.text.length > 0)
      .slice(0, 8);
  }

  function genericVisibleText() {
    const selector = [
      "[role='dialog']",
      "[aria-live='polite']",
      "[aria-live='assertive']",
      "article p",
      "main p",
      "main li",
      "h1",
      "h2",
      "h3",
      "button",
      "label"
    ].join(",");

    const candidates = Array.from(
      document.querySelectorAll(selector)
    );

    const seen = new Set();
    const regions = [];

    for (const element of candidates) {
      const text = cleanText(element.innerText || element.textContent);
      if (
        text.length < 4 ||
        text.length > 800 ||
        seen.has(text)
      ) {
        continue;
      }

      const region = regionFromElement(
        element,
        text,
        element.getAttribute("role") || element.tagName.toLowerCase(),
        false
      );

      if (!region) continue;

      const viewportArea = innerWidth * innerHeight;
      const regionArea = region.width * region.height;

      if (
        viewportArea > 0 &&
        regionArea / viewportArea > 0.55
      ) {
        continue;
      }

      seen.add(text);
      regions.push(region);

      if (regions.length >= 48) break;
    }

    return regions;
  }

  function snapshot() {
    const captions = youtubeCaptions();
    const regions =
      captions.length > 0
        ? captions
        : genericVisibleText();

    return {
      protocol: 1,
      kind: captions.length > 0 ? "youtube-captions" : "dom",
      url: location.href,
      title: document.title || "",
      visible: document.visibilityState === "visible",
      timestampUnixMs: Date.now(),
      innerWidth,
      innerHeight,
      outerWidth,
      outerHeight,
      devicePixelRatio: window.devicePixelRatio || 1,
      regions
    };
  }

  async function push(force = false) {
    const data = snapshot();

    const key = JSON.stringify([
      data.kind,
      data.url,
      data.visible,
      data.regions.map((region) => [
        region.text,
        Math.round(region.x),
        Math.round(region.y),
        Math.round(region.width),
        Math.round(region.height)
      ])
    ]);

    const now = Date.now();

    if (
      !force &&
      key === lastKey &&
      now - lastSentAt < 1000
    ) {
      return;
    }

    lastKey = key;
    lastSentAt = now;

    try {
      await fetch(endpoint, {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify(data),
        cache: "no-store"
      });
    } catch {
      // Desktop app may not be running. Keep the companion silent.
    }
  }

  function schedule() {
    clearTimeout(timer);
    timer = setTimeout(() => push(false), 80);
  }

  const observer = new MutationObserver(schedule);
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true,
    characterData: true,
    attributes: true,
    attributeFilter: ["style", "class", "aria-hidden"]
  });

  addEventListener("scroll", schedule, { passive: true });
  addEventListener("resize", schedule, { passive: true });
  document.addEventListener("visibilitychange", () => push(true));

  setInterval(() => push(true), 1000);
  push(true);
})();
