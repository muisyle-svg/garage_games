# Security

- Wi-Fi credentials, Google secrets, databases, exports, and participant data are
  excluded from Git.
- The legacy Wi-Fi password must be rotated before any old sketch is published.
- Apps Script accepts only signed POST actions. Timestamp and nonce checks limit
  replay, while record idempotency protects retries.
- Use a dedicated Google account or event-owned Drive folder and review deployment
  access before each event.
- Release artifacts are built in GitHub Actions from tagged source. Never distribute
  an untracked local firmware image as the event release.

If a credential is exposed, rotate it immediately, remove it from all firmware and
controller configurations, invalidate the Apps Script deployment if needed, and
document affected releases. Removing a secret from the latest commit is not enough;
assume repository history and downloaded artifacts retain it.

