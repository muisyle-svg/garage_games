(() => {
  "use strict";

  const MAX_EVENT_BASE_POINTS = 1_000_000;
  const MAX_SCORING_POINTS = 1_000_000;
  const MAX_SCORING_SECONDS = 86_400;
  const SCORE_FIELDS = Object.freeze([
    "basePoints",
    "minimumPoints",
    "decayPoints",
    "decayEverySeconds",
    "graceSeconds"
  ]);

  function integerDefault(value, fallback, { minimum = 0, maximum = Number.MAX_SAFE_INTEGER } = {}) {
    if (value === null || value === undefined || value === "") return fallback;
    const number = Number(value);
    return Number.isInteger(number) && number >= minimum && number <= maximum ? number : fallback;
  }

  function scoringDefaults(source, payload, inheritedDefaults) {
    const provided = source.scoring || payload?.scoring || inheritedDefaults || source.edition?.scoring || {};
    return {
      basePoints: integerDefault(provided.basePoints, 50, { maximum: MAX_EVENT_BASE_POINTS }),
      minimumPoints: integerDefault(provided.minimumPoints, 25, { maximum: MAX_EVENT_BASE_POINTS }),
      decayPoints: integerDefault(provided.decayPoints, 5, { maximum: MAX_SCORING_POINTS }),
      decayEverySeconds: integerDefault(provided.decayEverySeconds, 5, { minimum: 1, maximum: MAX_SCORING_SECONDS }),
      graceSeconds: 0
    };
  }

  function hardwareId(value) {
    if (typeof value !== "string") return null;
    const input = value.trim();
    const compact = /^[0-9a-f]{12}$/i.test(input)
      ? input
      : /^(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}$/i.test(input)
        ? input.replace(/[:-]/g, "")
        : "";
    return compact ? compact.toUpperCase() : null;
  }

  function uniqueEventId(events) {
    const used = new Set((events || []).map((event) => String(event.eventId || "").toLowerCase()));
    let index = 1;
    let candidate = "";
    do {
      candidate = `event-${String(index).padStart(2, "0")}`;
      index += 1;
    } while (used.has(candidate.toLowerCase()));
    return candidate;
  }

  function uniquePlaceholder(eventId, usedDeviceIds = new Set()) {
    const safeId = String(eventId || "event").trim().replace(/[^a-z0-9_-]/gi, "-") || "event";
    const used = new Set(Array.from(usedDeviceIds, (id) => String(id).toLowerCase()));
    let candidate = `unassigned-${safeId}`;
    let suffix = 2;
    while (used.has(candidate.toLowerCase())) {
      candidate = `unassigned-${safeId}-${suffix}`;
      suffix += 1;
    }
    return candidate;
  }

  function normalizeSetup(payload, inheritedDefaults = null) {
    const source = payload?.setup || payload?.result || payload || {};
    const sourceEvents = Array.isArray(source.events) ? source.events : [];
    const defaults = scoringDefaults(source, payload, inheritedDefaults);
    const usedEventIds = new Set();
    const sourceWithIds = sourceEvents.map((event, index) => {
      let eventId = String(event?.eventId || "").trim();
      if (!eventId || usedEventIds.has(eventId.toLowerCase())) {
        eventId = uniqueEventId(Array.from(usedEventIds, (id) => ({ eventId: id })));
      }
      usedEventIds.add(eventId.toLowerCase());
      return { event: event || {}, eventId, index };
    });

    const reservedPlaceholders = new Set();
    sourceWithIds.forEach(({ event }) => {
      const deviceId = String(event.deviceId || "").trim();
      if (deviceId && !hardwareId(deviceId)) reservedPlaceholders.add(deviceId.toLowerCase());
    });
    const usedDeviceIds = new Set();
    const events = sourceWithIds.map(({ event, eventId, index }) => {
      const sourceDeviceId = String(event.deviceId || "").trim();
      const assignedMac = hardwareId(sourceDeviceId);
      let placeholderDeviceId = "";
      if (!assignedMac && sourceDeviceId && !usedDeviceIds.has(sourceDeviceId.toLowerCase())) {
        placeholderDeviceId = sourceDeviceId;
      } else {
        placeholderDeviceId = uniquePlaceholder(eventId, new Set([...reservedPlaceholders, ...usedDeviceIds]));
      }
      usedDeviceIds.add((assignedMac || placeholderDeviceId).toLowerCase());
      usedDeviceIds.add(placeholderDeviceId.toLowerCase());
      const hasExplicitBase = event.basePoints !== null && event.basePoints !== undefined;
      const basePoints = hasExplicitBase ? event.basePoints : defaults.basePoints;
      const inherited = {};
      const values = {};
      SCORE_FIELDS.forEach((field) => {
        const explicit = event[field] !== null && event[field] !== undefined;
        inherited[field] = !explicit;
        if (explicit) values[field] = event[field];
      });
      values.basePoints = basePoints;
      values.minimumPoints = event.minimumPoints !== null && event.minimumPoints !== undefined
        ? event.minimumPoints
        : hasExplicitBase ? Math.ceil(Number(basePoints) / 2) : defaults.minimumPoints;
      values.decayPoints = event.decayPoints ?? defaults.decayPoints;
      values.decayEverySeconds = event.decayEverySeconds ?? defaults.decayEverySeconds;
      values.graceSeconds = event.graceSeconds ?? defaults.graceSeconds;
      return {
        eventId,
        name: String(event.name || `Event ${index + 1}`),
        type: String(event.type || "standard"),
        prompt: String(event.prompt || ""),
        answer: String(event.answer || ""),
        requiredSuccesses: integerDefault(event.requiredSuccesses, 1, { minimum: 1, maximum: MAX_KEYPAD_REQUIRED_SUCCESSES }),
        ...values,
        ...Object.fromEntries(SCORE_FIELDS.map((field) => [`${field}Inherited`, inherited[field]])),
        assignmentValue: assignedMac || "",
        unassignedDeviceId: placeholderDeviceId
      };
    });

    return {
      editionId: String(source.editionId || ""),
      name: String(source.name || ""),
      scoringDefaults: defaults,
      bonusGame: normalizeBonusGame(source.bonusGame),
      // Where keypad events get their messages and codes (the keypad answer file).
      keypadMessages: {
        count: integerDefault(source.keypadMessageCount, 0),
        source: String(source.keypadMessageSource || ""),
        error: String(source.keypadMessageError || "")
      },
      events
    };
  }

  // The bonus speed round's settings, edited in seconds (the app stores milliseconds).
  const BONUS_DEFAULTS = Object.freeze({
    name: "Bonus round", enabled: true, pointsPerPress: 5, initialWindowMs: 10_000, stepMs: 1_000, stepEveryMs: 10_000, minimumWindowMs: 2_000
  });
  const MAX_BONUS_NAME_LENGTH = 60;

  function normalizeBonusGame(source) {
    const value = { ...BONUS_DEFAULTS, ...(source && typeof source === "object" ? source : {}) };
    const seconds = (ms) => String(Math.round(Number(ms) / 100) / 10);
    return {
      name: String(value.name || BONUS_DEFAULTS.name),
      enabled: value.enabled !== false,
      pointsPerPress: String(value.pointsPerPress),
      initialSeconds: seconds(value.initialWindowMs),
      stepSeconds: seconds(value.stepMs),
      stepEverySeconds: seconds(value.stepEveryMs),
      minimumSeconds: seconds(value.minimumWindowMs)
    };
  }

  function buildBonusGamePayload(bonus) {
    const source = bonus || normalizeBonusGame(null);
    const secondsToMs = (text, label, minimum, maximum) => {
      const number = Number(String(text ?? "").trim());
      if (String(text ?? "").trim() === "" || !Number.isFinite(number) || number < minimum || number > maximum) {
        throw new Error(`Bonus round: ${label} must be a number from ${minimum} to ${maximum} seconds.`);
      }
      return Math.round(number * 1000);
    };
    if (!validInteger(source.pointsPerPress, 0, 100_000)) {
      throw new Error("Bonus round: points per press must be a whole number from 0 to 100,000.");
    }
    // A blank name falls back to the default.
    const name = String(source.name ?? "").trim() || BONUS_DEFAULTS.name;
    if (name.length > MAX_BONUS_NAME_LENGTH) {
      throw new Error(`Bonus round: keep its name to ${MAX_BONUS_NAME_LENGTH} characters or fewer.`);
    }
    const payload = {
      name,
      enabled: source.enabled !== false,
      pointsPerPress: Number(source.pointsPerPress),
      initialWindowMs: secondsToMs(source.initialSeconds, "starting seconds per press", 0.5, 60),
      stepMs: secondsToMs(source.stepSeconds, "the drop", 0, 60),
      stepEveryMs: secondsToMs(source.stepEverySeconds, "how often it drops", 1, 600),
      minimumWindowMs: secondsToMs(source.minimumSeconds, "minimum seconds per press", 0.5, 60)
    };
    if (payload.minimumWindowMs > payload.initialWindowMs) {
      throw new Error("Bonus round: the minimum seconds per press can't be longer than the starting seconds.");
    }
    return payload;
  }

  function addEvent(draft) {
    const eventId = uniqueEventId(draft?.events || []);
    const usedDeviceIds = new Set();
    (draft?.events || []).forEach((event) => {
      usedDeviceIds.add(String(event.unassignedDeviceId || "").toLowerCase());
      const assigned = hardwareId(event.assignmentValue);
      if (assigned) usedDeviceIds.add(assigned.toLowerCase());
    });
    const placeholder = uniquePlaceholder(eventId, usedDeviceIds);
    const defaults = draft?.scoringDefaults || scoringDefaults({}, null, null);
    const event = {
      eventId,
      name: `Event ${(draft?.events || []).length + 1}`,
      type: "standard",
      prompt: "",
      answer: "",
      requiredSuccesses: 1,
      basePoints: defaults.basePoints,
      basePointsInherited: true,
      minimumPoints: defaults.minimumPoints,
      minimumPointsInherited: true,
      decayPoints: defaults.decayPoints,
      decayPointsInherited: true,
      decayEverySeconds: defaults.decayEverySeconds,
      decayEverySecondsInherited: true,
      graceSeconds: 0,
      graceSecondsInherited: true,
      assignmentValue: "",
      unassignedDeviceId: placeholder
    };
    draft.events.push(event);
    return event;
  }

  function updateEventScoring(event, field, value) {
    if (!event || !SCORE_FIELDS.includes(field)) return false;
    event[field] = value;
    event[`${field}Inherited`] = false;
    if (field === "basePoints" && event.minimumPointsInherited) {
      const number = value === "" ? NaN : Number(value);
      event.minimumPoints = Number.isInteger(number) && number >= 0
        ? Math.ceil(number / 2)
        : "";
    }
    return true;
  }

  function buildSetupPayload(draft) {
    const editionId = String(draft?.editionId || "").trim();
    const name = String(draft?.name || "").trim();
    if (!editionId) throw new Error("The setup response is missing its edition ID. Reload setup before saving.");
    if (!name) throw new Error("Enter an edition name.");
    if (!draft.events?.length) throw new Error("Add at least one event before saving setup.");

    const eventIds = new Set();
    const deviceIds = new Set();
    const payloadEvents = draft.events.map((event, index) => {
      const eventId = String(event.eventId || "").trim();
      const eventName = String(event.name || "").trim();
      if (!eventId || eventIds.has(eventId.toLowerCase())) throw new Error("Each event needs a unique event ID.");
      if (!eventName) throw new Error(`Enter a name for event ${index + 1}.`);
      const basePoints = Number(event.basePoints);
      const minimumPoints = Number(event.minimumPoints);
      const decayPoints = Number(event.decayPoints);
      const decayEverySeconds = Number(event.decayEverySeconds);
      const graceSeconds = Number(event.graceSeconds);
      if (!validInteger(event.basePoints, 0, MAX_EVENT_BASE_POINTS)) {
        throw new Error(`${eventName || `Event ${index + 1}`}: starting points must be a whole number from 0 to 1,000,000.`);
      }
      if (!validInteger(event.minimumPoints, 0, basePoints)) {
        throw new Error(`${eventName}: minimum points must be a whole number from 0 to the starting points (${basePoints}).`);
      }
      if (!validInteger(event.decayPoints, 0, MAX_SCORING_POINTS)) {
        throw new Error(`${eventName}: points lost each step must be a whole number from 0 to ${MAX_SCORING_POINTS.toLocaleString("en-US")}.`);
      }
      if (!validInteger(event.decayEverySeconds, 1, MAX_SCORING_SECONDS)) {
        throw new Error(`${eventName}: seconds per step must be a whole number from 1 to ${MAX_SCORING_SECONDS.toLocaleString("en-US")}.`);
      }
      if (!validInteger(event.graceSeconds, 0, MAX_SCORING_SECONDS)) {
        throw new Error(`${eventName}: initial grace seconds must be a whole number from 0 to ${MAX_SCORING_SECONDS.toLocaleString("en-US")}.`);
      }
      eventIds.add(eventId.toLowerCase());

      const rawAssignment = String(event.assignmentValue || "").trim();
      const assignedMac = rawAssignment ? hardwareId(rawAssignment) : null;
      if (rawAssignment && !assignedMac) throw new Error(`${eventName}: enter a 12-hex MAC address or leave it unassigned.`);
      let deviceId = assignedMac || String(event.unassignedDeviceId || "").trim();
      if (!deviceId) deviceId = uniquePlaceholder(eventId, deviceIds);
      if (deviceIds.has(deviceId.toLowerCase())) {
        if (assignedMac) throw new Error(`${eventName}: this hardware MAC is already assigned to another event.`);
        deviceId = uniquePlaceholder(eventId, deviceIds);
      }
      deviceIds.add(deviceId.toLowerCase());
      const type = String(event.type || "standard");
      const prompt = String(event.prompt || "").trim();
      const answer = normalizeKeypadCode(event.answer);
      if (type === "keypad") {
        if (!validInteger(event.requiredSuccesses, 1, MAX_KEYPAD_REQUIRED_SUCCESSES)) {
          throw new Error(`${eventName}: codes to pass must be a whole number from 1 to ${MAX_KEYPAD_REQUIRED_SUCCESSES}.`);
        }
        // A fixed message/code is only a fallback kept from older setups; the keypad
        // answer file normally supplies them.
        if (prompt.length > MAX_KEYPAD_PROMPT_LENGTH) throw new Error(`${eventName}: keep the TV message to ${MAX_KEYPAD_PROMPT_LENGTH} characters or fewer.`);
        if (answer && !KEYPAD_CODE_PATTERN.test(answer)) {
          throw new Error(`${eventName}: the keypad code must be 1 to 12 keys using 0-9, A-D, or #; players press * to enter it.`);
        }
      }
      return {
        eventId,
        name: eventName,
        deviceId,
        type,
        basePoints: event.basePointsInherited ? null : basePoints,
        minimumPoints: event.minimumPointsInherited ? null : minimumPoints,
        decayPoints: event.decayPointsInherited ? null : decayPoints,
        decayEverySeconds: event.decayEverySecondsInherited ? null : decayEverySeconds,
        graceSeconds: event.graceSecondsInherited ? null : graceSeconds,
        ...(type === "keypad" ? {
          requiredSuccesses: Number(event.requiredSuccesses),
          ...(prompt ? { prompt } : {}),
          ...(answer ? { answer } : {})
        } : {})
      };
    });

    return { editionId, name, events: payloadEvents, bonusGame: buildBonusGamePayload(draft.bonusGame) };
  }

  function scanStatus(value) {
    const status = String(value || "").trim().replace(/[\s_-]/g, "").toLowerCase();
    if (status === "responding" || status === "responded") return "Responding";
    if (status === "notresponding" || status === "noresponse" || status === "unresponsive") return "NotResponding";
    if (status === "notscanned" || status === "unscanned") return "NotScanned";
    if (status === "detected" || status === "seen") return "Detected";
    return "Unknown";
  }

  function firstBoolean(sources, keys) {
    for (const source of sources) {
      for (const key of keys) {
        if (typeof source?.[key] === "boolean") return source[key];
      }
    }
    return false;
  }

  function normalizeScanResponse(payload) {
    const root = payload?.scan || payload?.scanResult || payload?.result || payload?.data || payload || {};
    const connected = firstBoolean([root, root.master], ["connected", "masterConnected", "isConnected"]);
    const completed = firstBoolean([root], ["completed", "scanCompleted", "complete", "finished"]);
    const rawDevices = root.devices || root.results || root.deviceStatuses || [];
    const devices = (Array.isArray(rawDevices) ? rawDevices : []).map((device) => ({
      eventId: String(device?.eventId || ""),
      name: String(device?.name || ""),
      deviceId: hardwareId(String(device?.deviceId || device?.macAddress || device?.address || device?.id || "")) || "",
      status: scanStatus(device?.status || device?.readiness || device?.result)
    }));
    const rawDetected = root.detectedDeviceIds || root.detectedDevices || root.foundDeviceIds || [];
    const detectedDeviceIds = [];
    const seen = new Set();
    (Array.isArray(rawDetected) ? rawDetected : typeof rawDetected === "string" ? [rawDetected] : []).forEach((entry) => {
      const mac = hardwareId(typeof entry === "string" ? entry : entry?.deviceId || entry?.macAddress || entry?.address || entry?.id);
      if (mac && !seen.has(mac)) {
        seen.add(mac);
        detectedDeviceIds.push(mac);
      }
    });
    if (root.detectedDeviceIds === undefined && root.detectedDevices === undefined && root.foundDeviceIds === undefined) {
      devices.filter((device) => device.status === "Responding" || device.status === "Detected").forEach((device) => {
        if (device.deviceId && !seen.has(device.deviceId)) {
          seen.add(device.deviceId);
          detectedDeviceIds.push(device.deviceId);
        }
      });
    }
    return { connected, completed, devices, detectedDeviceIds };
  }

  function readiness(event, scan, fresh) {
    const mac = hardwareId(event?.assignmentValue || event?.deviceId || "");
    if (!mac) return { key: "unassigned", label: "Unassigned" };
    if (!fresh || !scan?.connected || !scan?.completed) return { key: "unverified", label: "Unverified" };

    const result = (scan.devices || []).find((device) => device.deviceId === mac ||
      (device.eventId === event.eventId && (!device.deviceId || device.deviceId === mac)));
    if (result?.status === "Responding") return { key: "responding", label: "Responding" };
    if (result?.status === "NotResponding") return { key: "not-responding", label: "Not responding" };
    if (result?.status === "NotScanned") return { key: "not-scanned", label: "Not scanned" };
    if ((scan.detectedDeviceIds || []).includes(mac)) return { key: "detected", label: "Detected · response unreported" };
    return { key: "not-seen", label: "Not seen in scan" };
  }

  function readinessFromSnapshot(event, snapshot, masterConnected) {
    const mac = hardwareId(event?.assignmentValue || event?.deviceId || "");
    if (!mac) return { key: "unassigned", label: "Unassigned" };
    if (masterConnected !== true) return { key: "unverified", label: "Unverified" };

    const device = (snapshot?.devices || []).find((candidate) =>
      (candidate.eventId && candidate.eventId === event.eventId) ||
      hardwareId(candidate.deviceId || "") === mac);
    if (!device) return null;

    const availability = String(device.availability || "").trim().toLowerCase();
    if (availability === "online" && device.lastSeenAt) return { key: "responding", label: "Responding" };
    if (availability === "offline" || availability === "error") return { key: "not-responding", label: "Not responding" };
    return { key: "unverified", label: "Unverified" };
  }

  function currentReadiness(event, options = {}) {
    if (!hardwareId(event?.assignmentValue || event?.deviceId || "")) return { key: "unassigned", label: "Unassigned" };
    if (options.dirty || options.scanLoading || options.masterUnavailable || options.masterConnected === false) {
      return { key: "unverified", label: "Unverified" };
    }
    if (options.scanFresh && options.scanIsNewer) return readiness(event, options.scan, true);
    if (options.masterConnected === true) {
      const snapshotStatus = readinessFromSnapshot(event, options.snapshot, true);
      if (snapshotStatus) return snapshotStatus;
    }
    return options.scanFresh
      ? readiness(event, options.scan, true)
      : { key: "unverified", label: "Unverified" };
  }

  function scanSummary(scan, fresh) {
    if (!scan) return "Physical availability is unverified until a fresh scan completes.";
    if (!scan.connected) return "Master disconnected; physical availability is unverified.";
    if (!scan.completed) return "Scan did not complete; physical availability is unverified.";
    if (!fresh) return "Setup has changed since the scan. Save and scan again; physical availability is unverified.";
    const responding = scan.devices.filter((device) => device.status === "Responding").length;
    return `Fresh scan completed · ${responding} ${responding === 1 ? "device" : "devices"} responding.`;
  }

  function validInteger(value, minimum, maximum) {
    const text = String(value ?? "").trim();
    const number = Number(text);
    return text !== "" && Number.isInteger(number) && number >= minimum && number <= maximum;
  }

  // Codes are typed on a 4x4 keypad (0-9, A-D, #) and submitted with '*', so a code
  // written as "D5*" is stored as "D5".
  const KEYPAD_CODE_PATTERN = /^[0-9A-D#]{1,12}$/;
  const MAX_KEYPAD_PROMPT_LENGTH = 120;
  const MAX_KEYPAD_REQUIRED_SUCCESSES = 20;

  function normalizeKeypadCode(value) {
    return String(value ?? "").replace(/\s+/g, "").toUpperCase().replace(/\*+$/, "");
  }

  const api = Object.freeze({
    hardwareId,
    normalizeKeypadCode,
    normalizeBonusGame,
    buildBonusGamePayload,
    normalizeSetup,
    addEvent,
    updateEventScoring,
    buildSetupPayload,
    normalizeScanResponse,
    readiness,
    readinessFromSnapshot,
    currentReadiness,
    scanSummary
  });
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesScorekeeperSetup = api;
})();
