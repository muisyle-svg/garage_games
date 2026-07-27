import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

const outputDir = path.resolve("artifacts");
const previewDir = path.join(outputDir, "previews");
await fs.mkdir(previewDir, { recursive: true });

const workbook = Workbook.create();
const live = workbook.worksheets.add("Live Display");
const leaderboard = workbook.worksheets.add("Leaderboard");
const participants = workbook.worksheets.add("Participant Results");
const corrections = workbook.worksheets.add("Current Run Corrections");
const emergency = workbook.worksheets.add("Emergency Scorekeeper");
const audit = workbook.worksheets.add("Sync Audit Log");

const palette = {
  ink: "#202124",
  muted: "#5F6368",
  header: "#F1F3F4",
  line: "#DADCE0",
  orange: "#E85D2A",
  paleOrange: "#FCE8E1",
  green: "#E6F4EA",
  yellow: "#FEF7E0",
  red: "#FCE8E6",
  white: "#FFFFFF"
};
const titleFormat = {
  fill: palette.header,
  font: { bold: true, color: palette.ink, size: 18 },
  verticalAlignment: "center"
};
const headerFormat = {
  fill: palette.header,
  font: { bold: true, color: palette.ink },
  borders: { bottom: { color: palette.line, style: "continuous", weight: 1 } },
  verticalAlignment: "center",
  wrapText: true
};
const labelFormat = {
  font: { bold: true, color: palette.muted }
};

for (const sheet of [live, leaderboard, participants, corrections, emergency, audit]) {
  sheet.showGridLines = false;
}

// Live display
live.getRange("A1:H1").merge();
live.getRange("A1").values = [["Garage Games 2026 — Live Display"]];
live.getRange("A1:H1").format = titleFormat;
live.getRange("A2:A9").values = [
  ["Competitor"], ["Run Status"], ["Time Remaining"], ["Current Score"],
  ["Completed"], ["Attempted"], ["Bonuses"], ["Revision"]
];
live.getRange("A2:A9").format = labelFormat;
live.getRange("B2:B9").values = [["Waiting"], ["Idle"], [300], [0], [0], [0], [0], [0]];
live.getRange("B2:B9").format.font = { bold: true };
live.getRange("B4:B9").format.numberFormat = "0";
live.getRange("A11:H11").values = [[
  "Order", "Game ID", "Game", "Attempt Time", "Completion Time",
  "Duration", "Bonus", "Status"
]];
live.getRange("A11:H11").format = headerFormat;
live.freezePanes.freezeRows(1);
live.getRange("A1:H31").format.rowHeight = 22;
live.getRange("A1:A31").format.columnWidth = 16;
live.getRange("B1:B31").format.columnWidth = 24;
live.getRange("C1:C31").format.columnWidth = 28;
live.getRange("D1:F31").format.columnWidth = 17;
live.getRange("G1:H31").format.columnWidth = 15;
live.getRange("A12:H31").conditionalFormats.addCustom("=$H12=\"Completed\"", {
  fill: palette.green
});
live.getRange("A12:H31").conditionalFormats.addCustom("=$H12=\"Attempted\"", {
  fill: palette.yellow
});

// Leaderboard
leaderboard.getRange("A1:H1").values = [[
  "Rank", "Competitor", "Score", "Time Remaining",
  "Completed", "Attempted", "Bonuses", "Finished"
]];
leaderboard.getRange("A1:H1").format = headerFormat;
leaderboard.getRange("A2:H2").values = [["", "", "", "", "", "", "", ""]];
leaderboard.freezePanes.freezeRows(1);
leaderboard.getRange("A:A").format.columnWidth = 9;
leaderboard.getRange("B:B").format.columnWidth = 28;
leaderboard.getRange("C:G").format.columnWidth = 16;
leaderboard.getRange("H:H").format.columnWidth = 24;
leaderboard.getRange("C2:G1000").format.numberFormat = "0.0";

// Participant result database
participants.getRange("A1:K1").values = [[
  "Run ID", "Competitor ID", "Competitor", "Status", "Score",
  "Time Remaining", "Completed", "Attempted", "Bonuses", "Finished", "Game Details"
]];
participants.getRange("A1:K1").format = headerFormat;
participants.getRange("A2:K2").values = [["", "", "", "", "", "", "", "", "", "", ""]];
participants.freezePanes.freezeRows(1);
participants.getRange("A:B").format.columnWidth = 26;
participants.getRange("C:C").format.columnWidth = 28;
participants.getRange("D:I").format.columnWidth = 16;
participants.getRange("J:J").format.columnWidth = 24;
participants.getRange("K:K").format.columnWidth = 48;
participants.getRange("E2:I1000").format.numberFormat = "0.0";

// Correction input queue
corrections.getRange("A1:L1").values = [[
  "Correction ID", "Run ID", "Expected Revision", "Game ID", "Field",
  "Numeric Value", "Boolean Value", "Reason", "Status", "Submit",
  "Submitted At", "Message"
]];
corrections.getRange("A1:L1").format = headerFormat;
corrections.getRange("A2:L2").values = [["", "", "", "", "", "", "", "", "", false, "", ""]];
corrections.freezePanes.freezeRows(1);
corrections.getRange("A:B").format.columnWidth = 26;
corrections.getRange("C:C").format.columnWidth = 17;
corrections.getRange("D:E").format.columnWidth = 24;
corrections.getRange("F:G").format.columnWidth = 15;
corrections.getRange("H:H").format.columnWidth = 35;
corrections.getRange("I:J").format.columnWidth = 14;
corrections.getRange("K:K").format.columnWidth = 22;
corrections.getRange("L:L").format.columnWidth = 42;
corrections.getRange("A2:L100").conditionalFormats.addCustom("=$I2=\"Applied\"", {
  fill: palette.green
});
corrections.getRange("A2:L100").conditionalFormats.addCustom("=$I2=\"Conflict\"", {
  fill: palette.red
});

// Emergency operation tab
emergency.getRange("A1:F1").merge();
emergency.getRange("A1").values = [["Garage Games 2026 — Emergency Scorekeeper"]];
emergency.getRange("A1:F1").format = titleFormat;
emergency.getRange("A2:A8").values = [
  ["Run ID"], ["Competitor"], ["Status"], ["Started At"],
  ["Timer Seconds"], ["Time Remaining"], ["Final Score"]
];
emergency.getRange("A2:A8").format = labelFormat;
emergency.getRange("B2:B8").values = [[""], [""], ["Idle"], [""], [300], [""], [""]];
emergency.getRange("B7").formulas = [["=IF(B5=\"\",\"\",MAX(0,B6-((NOW()-B5)*86400)))"]];
emergency.getRange("B5").format.numberFormat = "yyyy-mm-dd hh:mm:ss";
emergency.getRange("B6:B8").format.numberFormat = "0.0";
emergency.getRange("A11:F11").values = [[
  "Game ID", "Game", "Attempt Remaining", "Completion Remaining", "Bonus", "Status"
]];
emergency.getRange("A11:F11").format = headerFormat;
const emergencyGames = Array.from({ length: 20 }, (_, index) => [
  index < 13 ? `slot-${String(index + 1).padStart(2, "0")}` : "",
  index < 13 ? `2026 Game ${index + 1}` : "",
  "", "", false, ""
]);
emergency.getRange("A12:F31").values = emergencyGames;
for (let row = 12; row <= 31; row++) {
  emergency.getRange(`F${row}`).formulas = [[
    `=IF(B${row}="","",IF(D${row}<>"","Completed",IF(C${row}<>"","Attempted","Waiting")))`
  ]];
}
emergency.getRange("H2:L2").merge();
emergency.getRange("H2").values = [["Emergency workflow"]];
emergency.getRange("H2:L2").format = headerFormat;
emergency.getRange("H3:L8").merge();
emergency.getRange("H3").values = [[
  "Use Garage Games → Start emergency run. Enter attempt and completion " +
  "remaining times directly in the table, mark bonuses, enter the final score " +
  "in B8, then choose Finalize emergency run. This mode is independent of the " +
  "local controller and remains usable during a controller outage."
]];
emergency.getRange("H3:L8").format = {
  fill: palette.paleOrange,
  wrapText: true,
  verticalAlignment: "top",
  font: { color: palette.ink }
};
emergency.freezePanes.freezeRows(1);
emergency.getRange("A:A").format.columnWidth = 21;
emergency.getRange("B:B").format.columnWidth = 28;
emergency.getRange("C:E").format.columnWidth = 19;
emergency.getRange("F:F").format.columnWidth = 16;
emergency.getRange("H:L").format.columnWidth = 15;
emergency.getRange("A12:F31").conditionalFormats.addCustom("=$F12=\"Completed\"", {
  fill: palette.green
});
emergency.getRange("A12:F31").conditionalFormats.addCustom("=$F12=\"Attempted\"", {
  fill: palette.yellow
});

// Sync audit log
audit.getRange("A1:G1").values = [[
  "Occurred At", "Idempotency Key", "Action", "Run ID",
  "Event/Status", "Payload", "Result"
]];
audit.getRange("A1:G1").format = headerFormat;
audit.getRange("A2:G2").values = [["", "", "", "", "", "", ""]];
audit.freezePanes.freezeRows(1);
audit.getRange("A:A").format.columnWidth = 23;
audit.getRange("B:B").format.columnWidth = 34;
audit.getRange("C:E").format.columnWidth = 18;
audit.getRange("F:F").format.columnWidth = 60;
audit.getRange("G:G").format.columnWidth = 16;

// Compact structural verification.
const inspection = await workbook.inspect({
  kind: "sheet,table",
  maxChars: 5000,
  tableMaxRows: 14,
  tableMaxCols: 12
});
console.log(inspection.ndjson);
const errors = await workbook.inspect({
  kind: "match",
  searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A",
  options: { useRegex: true, maxResults: 100 },
  summary: "final formula error scan"
});
console.log(errors.ndjson);

for (const sheetName of [
  "Live Display", "Leaderboard", "Participant Results",
  "Current Run Corrections", "Emergency Scorekeeper", "Sync Audit Log"
]) {
  const preview = await workbook.render({
    sheetName,
    autoCrop: "all",
    scale: 1,
    format: "png"
  });
  await fs.writeFile(
    path.join(previewDir, `${sheetName.replaceAll(" ", "-").toLowerCase()}.png`),
    new Uint8Array(await preview.arrayBuffer()));
}

const output = await SpreadsheetFile.exportXlsx(workbook);
const outputPath = path.join(outputDir, "garage-games-2026-operations.xlsx");
await output.save(outputPath);
console.log(outputPath);

