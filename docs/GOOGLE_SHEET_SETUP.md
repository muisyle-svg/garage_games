# Google Sheet setup

1. Import the supplied 2026 operations workbook into Google Sheets.
2. Open **Extensions → Apps Script** and replace the editor contents with
   `google-apps-script/Code.gs`. Apply `appsscript.json` as the project manifest.
3. Run `setupWorkbook` once and approve the requested spreadsheet permissions.
4. Run `setSyncSecret` and enter a randomly generated secret of at least 24
   characters.
5. Deploy as a Web app that executes as the owner. Restrict access as tightly as the
   event's Google account arrangement permits.
6. In the controller setup screen, enter the `/exec` deployment URL and the exact
   same secret.
7. Add a test competitor and complete a virtual run. Confirm Live Display,
   Participant Results, Leaderboard, and Sync Audit Log update.

Requests are JSON POSTs signed with HMAC-SHA256. The script rejects stale
timestamps, replayed nonces, unknown actions, and duplicate batch records. There is
no general cell-writing API.

Only the correction input area and Emergency Scorekeeper are intended for human
editing. Generated tabs are warning-protected by `setupWorkbook`.

