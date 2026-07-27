/**
 * Garage Games 2026 Google Sheet mirror and emergency controller.
 *
 * Install this as a bound Apps Script project in the generated workbook.
 * Run setupWorkbook() once, setSyncSecret() once, then deploy as a Web App:
 * execute as the owner and allow access to anyone with the deployment URL.
 * Requests are authenticated inside the body because Apps Script web-app
 * events do not expose arbitrary HTTP request headers.
 */

const GG = Object.freeze({
  protocolVersion: 1,
  sheets: Object.freeze({
    live: 'Live Display',
    leaderboard: 'Leaderboard',
    participants: 'Participant Results',
    corrections: 'Current Run Corrections',
    emergency: 'Emergency Scorekeeper',
    audit: 'Sync Audit Log'
  }),
  correctionColumns: Object.freeze({
    id: 1,
    runId: 2,
    revision: 3,
    gameId: 4,
    field: 5,
    numeric: 6,
    boolean: 7,
    reason: 8,
    status: 9,
    submit: 10,
    submittedAt: 11,
    message: 12
  })
});

function onOpen() {
  SpreadsheetApp.getUi()
    .createMenu('Garage Games')
    .addItem('Start emergency run', 'emergencyStart')
    .addItem('Finalize emergency run', 'emergencyFinalize')
    .addSeparator()
    .addItem('Configure workbook', 'setupWorkbook')
    .addItem('Set sync secret', 'setSyncSecret')
    .addToUi();
}

function doGet() {
  return jsonOutput({
    ok: true,
    service: 'garage-games-2026-sheet',
    protocolVersion: GG.protocolVersion,
    serverTime: new Date().toISOString()
  });
}

function doPost(e) {
  try {
    const payload = authenticateRequest_(e);
    let result;
    switch (String(payload.action || '')) {
      case 'syncBatch':
        result = syncBatch_(payload);
        break;
      case 'pullCorrections':
        result = pullCorrections_(payload);
        break;
      case 'ackCorrection':
        result = acknowledgeCorrection_(payload);
        break;
      case 'health':
        result = { ok: true, serverTime: new Date().toISOString() };
        break;
      default:
        throw new Error('Unknown action.');
    }
    return jsonOutput(result);
  } catch (error) {
    appendAudit_('request-error', '', '', String(error), '{}', 'Rejected');
    return jsonOutput({ ok: false, error: String(error) });
  }
}

function authenticateRequest_(e) {
  if (!e || !e.postData || !e.postData.contents) {
    throw new Error('POST body is required.');
  }
  const envelope = JSON.parse(e.postData.contents);
  const timestamp = String(envelope.timestamp || '');
  const nonce = String(envelope.nonce || '');
  const signature = String(envelope.signature || '');
  const payloadJson = String(envelope.payloadJson || '');
  const secret = PropertiesService.getScriptProperties().getProperty('GG_SYNC_SECRET');
  if (!secret) throw new Error('Sync secret is not configured.');
  if (!timestamp || !nonce || !signature || !payloadJson) {
    throw new Error('Signed envelope is incomplete.');
  }

  const now = Math.floor(Date.now() / 1000);
  const requestTime = Number(timestamp);
  if (!Number.isFinite(requestTime) || Math.abs(now - requestTime) > 300) {
    throw new Error('Request timestamp is outside the five-minute window.');
  }

  const cache = CacheService.getScriptCache();
  if (cache.get('nonce:' + nonce)) throw new Error('Request nonce was already used.');

  const material = timestamp + '.' + nonce + '.' + payloadJson;
  const expected = Utilities.base64Encode(
    Utilities.computeHmacSha256Signature(material, secret));
  if (!constantTimeEquals_(signature, expected)) throw new Error('Invalid signature.');
  cache.put('nonce:' + nonce, '1', 600);
  return JSON.parse(payloadJson);
}

function constantTimeEquals_(left, right) {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++) {
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  }
  return difference === 0;
}

function syncBatch_(request) {
  const records = Array.isArray(request.records) ? request.records : [];
  const lock = LockService.getScriptLock();
  // Do not hold a controller retry for the full HTTP timeout when another
  // batch is still applying. The controller will retry the idempotent records.
  lock.waitLock(5000);
  try {
    let applied = 0;
    records.forEach(record => {
      const key = String(record.idempotencyKey || '');
      if (!key || wasProcessed_(key)) return;
      const action = String(record.action || '');
      const payload = record.payload || {};
      if (action === 'state') updateLiveDisplay_(payload);
      if (action === 'result') upsertParticipantResult_(payload);
      appendAudit_(
        key,
        action,
        payload.runId || payload.snapshot?.runId || '',
        payload.type || payload.snapshot?.status || '',
        JSON.stringify(payload),
        'Applied');
      markProcessed_(key);
      applied++;
    });
    if (applied > 0) rebuildLeaderboard_();
    return { ok: true, applied: applied };
  } finally {
    lock.releaseLock();
  }
}

function updateLiveDisplay_(payload) {
  const snapshot = payload.snapshot;
  if (!snapshot) return;
  const sheet = getSheet_(GG.sheets.live);
  const score = payload.score;
  sheet.getRange('B2:B9').setValues([
    [snapshot.competitorName || ''],
    [snapshot.status || ''],
    [snapshot.remainingSeconds ?? ''],
    [score ? score.total : ''],
    [snapshot.completionCount ?? 0],
    [snapshot.attemptCount ?? 0],
    [snapshot.bonusCount ?? 0],
    [snapshot.revision ?? 0]
  ]);
  const games = Array.isArray(snapshot.games) ? snapshot.games : [];
  const rows = games.map(game => [
    game.order,
    game.gameId,
    game.name,
    game.attemptRemainingSeconds ?? '',
    game.completionRemainingSeconds ?? '',
    game.durationSeconds ?? '',
    game.bonus === true,
    game.completed ? 'Completed' : game.attempted ? 'Attempted' : 'Waiting'
  ]);
  sheet.getRange('A12:H31').clearContent();
  if (rows.length) sheet.getRange(12, 1, rows.length, 8).setValues(rows);
}

function upsertParticipantResult_(payload) {
  const sheet = getSheet_(GG.sheets.participants);
  const runId = String(payload.runId || '');
  if (!runId) return;
  const row = findRowByValue_(sheet, 1, runId) || Math.max(sheet.getLastRow() + 1, 2);
  const score = payload.score || {};
  sheet.getRange(row, 1, 1, 11).setValues([[
    runId,
    payload.competitorId || '',
    payload.competitorName || '',
    payload.status || '',
    score.total ?? '',
    payload.remainingSeconds ?? '',
    payload.completionCount ?? '',
    payload.attemptCount ?? '',
    payload.bonusCount ?? '',
    payload.finishedAt || new Date().toISOString(),
    JSON.stringify(payload.games || [])
  ]]);
}

function rebuildLeaderboard_() {
  const participants = getSheet_(GG.sheets.participants);
  const leaderboard = getSheet_(GG.sheets.leaderboard);
  if (participants.getLastRow() < 2) {
    leaderboard.getRange('A2:H1000').clearContent();
    return;
  }
  const values = participants.getRange(2, 1, participants.getLastRow() - 1, 11)
    .getValues()
    .filter(row => row[0] && Number.isFinite(Number(row[4])))
    .sort((left, right) =>
      Number(right[4]) - Number(left[4]) ||
      Number(right[5]) - Number(left[5]) ||
      String(left[9]).localeCompare(String(right[9])));
  const rows = values.map((row, index) => [
    index + 1, row[2], row[4], row[5], row[6], row[7], row[8], row[9]
  ]);
  leaderboard.getRange('A2:H1000').clearContent();
  if (rows.length) leaderboard.getRange(2, 1, rows.length, 8).setValues(rows);
}

function pullCorrections_(request) {
  const limit = Math.max(1, Math.min(Number(request.limit || 25), 100));
  const sheet = getSheet_(GG.sheets.corrections);
  if (sheet.getLastRow() < 2) return { ok: true, corrections: [] };
  const values = sheet.getRange(2, 1, sheet.getLastRow() - 1, 12).getValues();
  const corrections = values
    .filter(row => row[GG.correctionColumns.status - 1] === 'Queued')
    .slice(0, limit)
    .map(row => ({
      correctionId: String(row[GG.correctionColumns.id - 1]),
      runId: String(row[GG.correctionColumns.runId - 1]),
      expectedRevision: Number(row[GG.correctionColumns.revision - 1]),
      gameId: String(row[GG.correctionColumns.gameId - 1]),
      field: String(row[GG.correctionColumns.field - 1]),
      numericValue: row[GG.correctionColumns.numeric - 1] === ''
        ? null : Number(row[GG.correctionColumns.numeric - 1]),
      booleanValue: row[GG.correctionColumns.boolean - 1] === ''
        ? null : Boolean(row[GG.correctionColumns.boolean - 1]),
      reason: String(row[GG.correctionColumns.reason - 1] || 'Sheet correction')
    }));
  return { ok: true, corrections: corrections };
}

function acknowledgeCorrection_(request) {
  const sheet = getSheet_(GG.sheets.corrections);
  const row = findRowByValue_(sheet, GG.correctionColumns.id, String(request.correctionId || ''));
  if (!row) throw new Error('Correction was not found.');
  sheet.getRange(row, GG.correctionColumns.status)
    .setValue(request.accepted === true ? 'Applied' : 'Conflict');
  sheet.getRange(row, GG.correctionColumns.message).setValue(String(request.message || ''));
  return { ok: true };
}

function onEdit(e) {
  if (!e || !e.range) return;
  const sheet = e.range.getSheet();
  if (sheet.getName() !== GG.sheets.corrections ||
      e.range.getRow() < 2 ||
      e.range.getColumn() !== GG.correctionColumns.submit ||
      String(e.value).toUpperCase() !== 'TRUE') {
    return;
  }
  const row = e.range.getRow();
  const values = sheet.getRange(row, 1, 1, 12).getValues()[0];
  if (!values[GG.correctionColumns.runId - 1] ||
      !values[GG.correctionColumns.gameId - 1] ||
      !values[GG.correctionColumns.field - 1]) {
    sheet.getRange(row, GG.correctionColumns.status).setValue('Missing required fields');
    return;
  }
  sheet.getRange(row, GG.correctionColumns.id)
    .setValue(values[GG.correctionColumns.id - 1] || Utilities.getUuid());
  sheet.getRange(row, GG.correctionColumns.status).setValue('Queued');
  sheet.getRange(row, GG.correctionColumns.submittedAt).setValue(new Date());
}

function emergencyStart() {
  const ui = SpreadsheetApp.getUi();
  const response = ui.prompt('Emergency run', 'Competitor name:', ui.ButtonSet.OK_CANCEL);
  if (response.getSelectedButton() !== ui.Button.OK) return;
  const name = response.getResponseText().trim();
  if (!name) throw new Error('Competitor name is required.');
  const sheet = getSheet_(GG.sheets.emergency);
  sheet.getRange('B2').setValue(Utilities.getUuid());
  sheet.getRange('B3').setValue(name);
  sheet.getRange('B4').setValue('Active');
  sheet.getRange('B5').setValue(new Date());
  sheet.getRange('B6').setValue(300);
  sheet.getRange('B7').setFormula('=IF(B5="","",MAX(0,B6-(NOW()-B5)*86400))');
  sheet.getRange('B8').clearContent();
  sheet.getRange('C12:E31').clearContent();
  appendAudit_('emergency:' + sheet.getRange('B2').getValue(), 'emergency-start',
    sheet.getRange('B2').getValue(), name, '{}', 'Applied');
}

function emergencyFinalize() {
  const sheet = getSheet_(GG.sheets.emergency);
  const runId = String(sheet.getRange('B2').getValue() || '');
  const name = String(sheet.getRange('B3').getValue() || '');
  if (!runId || !name) throw new Error('No emergency run is active.');
  const finalScore = Number(sheet.getRange('B8').getValue());
  if (!Number.isFinite(finalScore)) {
    throw new Error('Enter the final score in B8 before finalizing.');
  }
  const participantSheet = getSheet_(GG.sheets.participants);
  const gameValues = sheet.getRange('A12:E31').getValues().filter(row => row[0]);
  participantSheet.appendRow([
    runId, '', name, 'Emergency', finalScore,
    Number(sheet.getRange('B7').getValue() || 0),
    gameValues.filter(row => row[3] !== '').length,
    gameValues.filter(row => row[2] !== '').length,
    gameValues.filter(row => row[4] === true).length,
    new Date(),
    JSON.stringify(gameValues)
  ]);
  sheet.getRange('B4').setValue('Finalized');
  appendAudit_('emergency-final:' + runId, 'emergency-finalize', runId, name,
    JSON.stringify({ finalScore: finalScore }), 'Applied');
  rebuildLeaderboard_();
}

function setupWorkbook() {
  const required = Object.values(GG.sheets);
  required.forEach(name => {
    if (!SpreadsheetApp.getActive().getSheetByName(name)) {
      SpreadsheetApp.getActive().insertSheet(name);
    }
  });

  getSheet_(GG.sheets.corrections).getRange('A1:L1').setValues([[
    'Correction ID', 'Run ID', 'Expected Revision', 'Game ID', 'Field',
    'Numeric Value', 'Boolean Value', 'Reason', 'Status', 'Submit',
    'Submitted At', 'Message'
  ]]);
  getSheet_(GG.sheets.participants).getRange('A1:K1').setValues([[
    'Run ID', 'Competitor ID', 'Competitor', 'Status', 'Score',
    'Time Remaining', 'Completed', 'Attempted', 'Bonuses', 'Finished', 'Game Details'
  ]]);
  getSheet_(GG.sheets.leaderboard).getRange('A1:H1').setValues([[
    'Rank', 'Competitor', 'Score', 'Time Remaining',
    'Completed', 'Attempted', 'Bonuses', 'Finished'
  ]]);
  getSheet_(GG.sheets.audit).getRange('A1:G1').setValues([[
    'Occurred At', 'Idempotency Key', 'Action', 'Run ID',
    'Event/Status', 'Payload', 'Result'
  ]]);
  const checkbox = SpreadsheetApp.newDataValidation().requireCheckbox().build();
  getSheet_(GG.sheets.corrections).getRange('G2:G1000').setDataValidation(checkbox);
  getSheet_(GG.sheets.corrections).getRange('J2:J1000').setDataValidation(checkbox);
  getSheet_(GG.sheets.emergency).getRange('E12:E31').setDataValidation(checkbox);

  [GG.sheets.live, GG.sheets.leaderboard, GG.sheets.participants, GG.sheets.audit]
    .forEach(name => {
      const protection = getSheet_(name).protect();
      protection.setDescription('Generated by Garage Games controller');
      protection.setWarningOnly(true);
    });
  SpreadsheetApp.getUi().alert('Garage Games workbook configuration is complete.');
}

function setSyncSecret() {
  const ui = SpreadsheetApp.getUi();
  const response = ui.prompt(
    'Sync secret',
    'Enter the same long random secret configured in the Windows controller:',
    ui.ButtonSet.OK_CANCEL);
  if (response.getSelectedButton() !== ui.Button.OK) return;
  const secret = response.getResponseText();
  if (secret.length < 24) throw new Error('Use a secret of at least 24 characters.');
  PropertiesService.getScriptProperties().setProperty('GG_SYNC_SECRET', secret);
  ui.alert('Sync secret saved in Script Properties.');
}

function wasProcessed_(key) {
  const cache = CacheService.getScriptCache();
  if (cache.get('processed:' + key)) return true;
  const sheet = getSheet_(GG.sheets.audit);
  const lastRow = sheet.getLastRow();
  if (lastRow >= 2) {
    const firstRow = Math.max(2, lastRow - 1999);
    const values = sheet.getRange(firstRow, 2, lastRow - firstRow + 1, 1).getValues().flat();
    if (values.some(value => String(value) === key)) {
      cache.put('processed:' + key, '1', 21600);
      return true;
    }
  }
  return false;
}

function markProcessed_(key) {
  CacheService.getScriptCache().put('processed:' + key, '1', 21600);
}

function appendAudit_(key, action, runId, eventName, payload, result) {
  getSheet_(GG.sheets.audit).appendRow([
    new Date(), key, action, runId, eventName, payload, result
  ]);
}

function findRowByValue_(sheet, column, value) {
  if (!value || sheet.getLastRow() < 2) return 0;
  const values = sheet.getRange(2, column, sheet.getLastRow() - 1, 1).getValues();
  const index = values.findIndex(row => String(row[0]) === value);
  return index < 0 ? 0 : index + 2;
}

function getSheet_(name) {
  const sheet = SpreadsheetApp.getActive().getSheetByName(name);
  if (!sheet) throw new Error('Required sheet is missing: ' + name);
  return sheet;
}

function jsonOutput(value) {
  return ContentService
    .createTextOutput(JSON.stringify(value))
    .setMimeType(ContentService.MimeType.JSON);
}
