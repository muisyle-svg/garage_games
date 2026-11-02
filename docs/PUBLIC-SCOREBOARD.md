# Public scoreboard on the event guide website

The scorekeeper can copy its Leaderboards tab (overall leaderboard, event leaderboards, and
each listed run's scorecard) to the event guide website, so anyone can follow the standings
on their phone at https://muisyle-svg.github.io/garage-games-guide/scoreboard/.

## How it works

- Every 10 seconds the app checks whether the standings changed. When they did, it uploads
  `scores.json` to the `live-scores` branch of `muisyle-svg/garage-games-guide`, at most once
  a minute. Only names, run types, times, and points are sent.
- That upload rebuilds the website, which takes about a minute. The page refreshes itself
  every 30 seconds, so results show up on phones one to three minutes after they are recorded.
- If the internet drops, the scorekeeper carries on as normal; the website just falls behind
  and catches up on the next upload.
- The Leaderboards tab shows a "Public scoreboard" line with the last upload time or what
  went wrong.

## One-time setup on the event PC

1. On GitHub, create a fine-grained personal access token
   (Settings → Developer settings → Personal access tokens → Fine-grained tokens):
   - Repository access: only `muisyle-svg/garage-games-guide`
   - Permissions: Contents → Read and write
   - Expiration: past the event date
2. Create `public-scoreboard.json` in the app's data folder,
   `%LOCALAPPDATA%\GarageGamesV2\public-scoreboard.json`:

   ```json
   {
     "token": "github_pat_paste_the_token_here"
   }
   ```

   The file stays on this computer and is never committed. Changes are picked up within
   10 seconds; no restart needed.

Optional settings in the same file (defaults shown):

```json
{
  "enabled": true,
  "repository": "muisyle-svg/garage-games-guide",
  "branch": "live-scores",
  "path": "scores.json",
  "siteUrl": "https://muisyle-svg.github.io/garage-games-guide/scoreboard/",
  "minimumSecondsBetweenUploads": 60
}
```

Set `"enabled": false` to stop publishing (for example while testing with made-up runs).
Practice runs published by mistake disappear from the website as soon as they are deleted
in the scorekeeper and the next upload goes out.
