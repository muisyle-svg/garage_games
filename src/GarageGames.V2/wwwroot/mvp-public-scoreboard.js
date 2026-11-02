// Leaderboards tab: one line saying whether the event guide website has the latest results.
// The app uploads them itself (PublicScoreboardPublisher); this only shows its status.
(() => {
  "use strict";

  function statusText(status, formatTime) {
    if (!status || !status.state) return "";
    const when = status.lastPublishedAt ? ` Last sent ${formatTime(status.lastPublishedAt)}.` : "";
    switch (status.state) {
      case "published": return `Public scoreboard: ${status.message}${when}`;
      case "waiting": return `Public scoreboard: ${status.message}${when}`;
      case "error": return `Public scoreboard: couldn't update the website. ${status.message}${when}`;
      default: return `Public scoreboard is off. ${status.message}`;
    }
  }

  const api = Object.freeze({ statusText });
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window === "undefined" || typeof document === "undefined") return;

  const node = document.getElementById("public-scoreboard-status");
  if (!node) return;
  const formatTime = (value) => {
    const date = new Date(value);
    return Number.isFinite(date.getTime())
      ? new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(date)
      : "";
  };
  let last = "";

  async function refresh() {
    try {
      const response = await fetch("/api/public-scoreboard", { cache: "no-store" });
      if (!response.ok) return;
      const status = await response.json();
      const signature = JSON.stringify(status);
      if (signature === last) return;
      last = signature;
      node.dataset.state = status.state || "";
      node.replaceChildren(document.createTextNode(statusText(status, formatTime)));
      if (status.siteUrl && status.state !== "off") {
        const link = document.createElement("a");
        link.href = status.siteUrl;
        link.target = "_blank";
        link.rel = "noopener";
        link.textContent = "Open the public scoreboard";
        node.append(" ", link);
      }
      node.hidden = false;
    } catch {
      // The scorekeeper's own status line reports a lost connection to the app.
    }
  }

  refresh();
  setInterval(refresh, 10000);
})();
