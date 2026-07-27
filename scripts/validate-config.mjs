import fs from "node:fs";
import path from "node:path";

const directory = path.resolve("config/seasons");
let failures = 0;

for (const filename of fs.readdirSync(directory).filter(name => name.endsWith(".json"))) {
  const fullPath = path.join(directory, filename);
  const season = JSON.parse(fs.readFileSync(fullPath, "utf8"));
  const errors = [];
  if (!season.id || !season.name || !season.scoringPolicy) errors.push("missing identity or scoring policy");
  if (!Number.isInteger(season.timerSeconds) || season.timerSeconds <= 0) errors.push("timerSeconds must be positive");
  if (!Array.isArray(season.games) || season.games.length === 0 || season.games.length > 20) errors.push("games must contain 1–20 entries");
  const ids = season.games?.map(game => game.id) ?? [];
  if (new Set(ids).size !== ids.length) errors.push("game IDs must be unique");
  const orders = season.games?.map(game => game.order) ?? [];
  if (new Set(orders).size !== orders.length) errors.push("game order values must be unique");
  if (season.requiredCompletions > season.games.length) errors.push("requiredCompletions exceeds game count");
  if (errors.length) {
    failures++;
    console.error(`${filename}: ${errors.join("; ")}`);
  } else {
    console.log(`${filename}: valid (${season.games.length} games, policy ${season.scoringPolicy})`);
  }
}

if (failures) process.exit(1);

