(() => {
  "use strict";

  function orderScorecardEvents(configuredEvents, eventResults) {
    const resultsById = eventResults instanceof Map
      ? eventResults
      : new Map((eventResults || []).map((result) => [result.eventId, result]));

    return (configuredEvents || []).map((definition, configuredIndex) => ({
      definition,
      result: resultsById.get(definition.eventId) || null,
      configuredIndex
    })).sort((left, right) => {
      const leftCompleted = left.result?.status === "completed";
      const rightCompleted = right.result?.status === "completed";
      if (leftCompleted !== rightCompleted) return leftCompleted ? -1 : 1;

      if (!leftCompleted) return left.configuredIndex - right.configuredIndex;

      const leftFinish = left.result.finishElapsedMs;
      const rightFinish = right.result.finishElapsedMs;
      const leftHasFinish = Number.isFinite(leftFinish) && leftFinish >= 0;
      const rightHasFinish = Number.isFinite(rightFinish) && rightFinish >= 0;
      if (leftHasFinish !== rightHasFinish) return leftHasFinish ? -1 : 1;
      if (!leftHasFinish) return left.configuredIndex - right.configuredIndex;
      if (leftFinish !== rightFinish) return leftFinish - rightFinish;
      return left.configuredIndex - right.configuredIndex;
    });
  }

  const api = Object.freeze({ orderScorecardEvents });
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesScorecardOrder = api;
})();
