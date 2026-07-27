# Google Sheet integration

The controller does not need Google to run. This bound Apps Script publishes
local results, accepts structured corrections, and provides an independent
emergency workflow.

## Installation

1. Open the generated **Garage Games 2026 Operations** Google Sheet.
2. Open **Extensions → Apps Script**.
3. Replace the editor contents with `Code.gs` and replace the manifest with
   `appsscript.json`.
4. Run `setupWorkbook`, approve the requested Sheet permissions, and reload.
5. Run `setSyncSecret` and enter a new random value of at least 24 characters.
6. Deploy as a Web App that executes as you. The URL may be available to anyone
   because every controller request is HMAC authenticated and replay protected.
7. Enter the deployment URL and the same secret in the controller setup screen.

Never commit the secret or put it in firmware. Rotating it only requires updating
Script Properties and the controller setup.

## Corrections

Enter a correction on **Current Run Corrections**, including active Run ID,
current revision, stable Game ID, field, new value, and reason. Check **Submit**.
The controller will mark it Applied or Conflict. A conflict is never overwritten
automatically.

Supported fields are:

- `attemptRemainingSeconds`
- `completionRemainingSeconds`
- `bonus`

## Emergency mode

Use **Garage Games → Start emergency run**, enter times and bonus flags directly
on **Emergency Scorekeeper**, enter the final score, and choose
**Finalize emergency run**. Emergency data is intentionally independent of the
local distributed run and is preserved in Participant Results.

