'use strict';

// Limits come from the server (GET /api/config), so they are defined in one place only.
// Checking them here just gives instant feedback; the server always re-validates.
let maxKg = 500;
let cycleSeconds = 20;
let lowLevelFraction = 0.2;
const POLL_MS = 3000;
const POLL_RUNNING_MS = 1000;   // faster refresh while a batch is mixing

// Dev helper: ?preview=Running draws the selected batch in that state (the backend
// cannot produce Running/Completed until the process simulation exists).
const STATUSES = ['Created', 'Ready', 'Running', 'Completed', 'Error'];
const previewParam = new URLSearchParams(location.search).get('preview');
const preview = STATUSES.includes(previewParam) ? previewParam : null;

const MATERIAL_A = [194, 138, 30];  // same colours as --mat-a / --mat-b in style.css
const MATERIAL_B = [75, 95, 168];
const MIX_LEVEL = { running: 0.6, completed: 0.9 };

const state = {
  recipes: [],
  batches: [],
  tanks: [],
  moves: [],
  selectedId: null,
  detail: null,
  sending: false,
  result: null,          // { id, kind, text } outcome of the last "Send to ERP"
};
const rendered = { table: '', detail: '', inventory: '' };   // skip DOM rebuilds when nothing changed

const $ = (id) => document.getElementById(id);

// ---------- helpers ----------

/** Builds an element. Text is always set via text nodes, so data can never inject HTML. */
function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value);
  }
  for (const child of children) node.append(child);
  return node;
}

const fmtKg = (n) =>
  `${Number(n).toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 3 })} kg`;
const fmtDateTime = (iso) =>
  new Date(iso).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'medium' });
const fmtTime = (iso) =>
  new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

async function api(path, options) {
  let response;
  try {
    response = await fetch('/api' + path, options);
  } catch {
    throw new Error('Cannot reach the Mini-MES API.');
  }
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.error ?? `Request failed (HTTP ${response.status}).`);
  return body;
}

function pill(status) {
  return el('span', { class: 'pill', 'data-status': status, text: status });
}

// ---------- loading ----------

async function refresh() {
  try {
    state.batches = await api('/batches');
    state.tanks = await api('/tanks');
    state.moves = await api('/tanks/movements?limit=12');
    if (!state.batches.some((b) => b.batchId === state.selectedId)) {
      state.selectedId = state.batches[0]?.batchId ?? null;
    }
    const id = state.selectedId;
    const detail = id ? await api('/batches/' + encodeURIComponent(id)) : null;
    if (id !== state.selectedId) return;   // user picked another batch meanwhile; the next refresh handles it
    state.detail = detail;
    setConnection(true);
  } catch {
    setConnection(false);
    return;
  }
  render();
}

function setConnection(ok) {
  $('conn').dataset.ok = ok ? 'yes' : 'no';
  $('connText').textContent = ok ? 'API connected' : 'API unreachable. Retrying…';
}

function select(batchId) {
  state.selectedId = batchId;
  state.result = null;
  refresh();
}

// ---------- rendering ----------

function render() {
  renderSchematic();
  renderPlate();
  renderInventory();
  renderTable();
  renderDetail();
  updateProgress();
}

function displayStatus(batch) {
  return batch ? (preview ?? batch.status) : null;
}

function renderSchematic() {
  const batch = state.detail?.batch;
  const status = displayStatus(batch);
  const svg = $('schematic');
  svg.dataset.state = status ? status.toLowerCase() : 'none';

  // The tanks show the real inventory: material, level, and a fill that follows the level.
  for (const c of ['A', 'B']) {
    const tank = state.tanks.find((t) => t.container === c);
    $(`name${c}`).textContent = tank ? tank.materialName : '—';
    $(`kg${c}`).textContent = tank ? `${Math.round(tank.levelKg).toLocaleString()} kg` : '';
    $(`fill${c}`).style.setProperty('--level', tank ? tank.levelKg / tank.capacityKg : 0);
    $(`vessel${c}`).classList.toggle('low', Boolean(tank?.isLow));
  }

  const a = batch?.ingredients.find((i) => i.container === 'A');
  const b = batch?.ingredients.find((i) => i.container === 'B');

  // The mixer content is the two materials blended in the recipe's ratio.
  const share = a && b ? a.kilograms / (a.kilograms + b.kilograms) : 0.5;
  const mix = MATERIAL_A.map((c, i) => Math.round(c * share + MATERIAL_B[i] * (1 - share)));
  const mixFill = $('mixFill');
  mixFill.style.setProperty('--mix', `rgb(${mix.join(',')})`);
  mixFill.style.setProperty('--level', MIX_LEVEL[svg.dataset.state] ?? 0);

  svg.setAttribute('aria-label', batch
    ? `Mixing line schematic. Batch ${batch.batchId}, ${batch.recipeName}, status ${status}.`
    : 'Mixing line schematic. No batch selected.');
}

function renderPlate() {
  const batch = state.detail?.batch;
  const status = displayStatus(batch);
  $('plate').dataset.status = status ?? 'none';
  $('plateStatus').textContent = status ?? 'No batch';
  $('plateBatch').textContent = batch?.batchId ?? '—';
  $('plateRecipe').textContent = batch?.recipeName ?? '—';
  $('platePlanned').textContent = batch ? fmtKg(batch.plannedQuantityKg) : '—';

  const note = $('plateNote');
  note.hidden = !(preview && batch);
  note.textContent = preview ? `Preview mode: the drawing shows "${preview}". The real status is ${batch?.status}.` : '';
}

function tankCard(tank, typedValue) {
  const fraction = tank.levelKg / tank.capacityKg;
  const percent = Math.round(fraction * 100);

  const input = el('input', {
    type: 'number', id: `refill${tank.container}`, min: '0', step: 'any', placeholder: 'kg',
    'aria-label': `Quantity to add to TK-${tank.container} in kg`,
  });
  input.value = typedValue;

  return el('article', { class: 'tank-card', 'data-container': tank.container },
    el('h3', {},
      el('span', { class: 'tag', text: `TK-${tank.container}` }),
      tank.materialName,
      ...(tank.isLow ? [el('span', { class: 'low-flag', text: 'LOW' })] : [])),
    el('div', { class: 'level', role: 'img', 'aria-label': `${percent} percent full` },
      el('div', { class: 'level-bar', style: `width:${(fraction * 100).toFixed(1)}%` }),
      el('div', { class: 'level-mark', style: `left:${lowLevelFraction * 100}%` })),
    el('div', { class: 'level-text' }, `${fmtKg(tank.levelKg)} `,
      el('small', { text: `of ${fmtKg(tank.capacityKg)} · ${percent} %` })),
    el('form', {
      class: 'refill',
      novalidate: '',
      onsubmit: (event) => { event.preventDefault(); refillTank(tank.container, input); },
    }, input, el('button', { class: 'btn', type: 'submit', text: 'Add material' })));
}

function renderInventory() {
  const signature = JSON.stringify([state.tanks, state.moves]);
  if (signature === rendered.inventory) return;
  rendered.inventory = signature;

  // Keep whatever the operator was typing in the refill boxes while the data refreshes.
  const typed = { A: $('refillA')?.value ?? '', B: $('refillB')?.value ?? '' };

  const tanks = $('tanks');
  tanks.replaceChildren(...state.tanks.map((t) => tankCard(t, typed[t.container] ?? '')));

  const moves = $('moves');
  moves.replaceChildren();
  if (state.moves.length === 0) {
    moves.append(el('li', {}, el('span', { class: 'empty', text: 'No material movements yet. Starting a batch or refilling a tank adds the first line.' })));
    return;
  }
  for (const m of state.moves) {
    const out = m.deltaKg < 0;
    moves.append(el('li', {},
      el('time', { datetime: m.timestampUtc, text: fmtTime(m.timestampUtc) }),
      el('span', { class: 'tk', text: `TK-${m.container}` }),
      el('span', { class: `qty ${out ? 'out' : 'in'}`, text: `${out ? '−' : '+'}${fmtKg(Math.abs(m.deltaKg))}` }),
      el('span', { class: 'reason', text: m.reason })));
  }
}

function showInventoryMessage(text, kind = 'bad') {
  const msg = $('invMsg');
  msg.hidden = !text;
  msg.dataset.kind = kind;
  msg.textContent = text ?? '';
}

async function refillTank(container, input) {
  showInventoryMessage(null);
  const kg = Number(input.value);
  if (!(kg > 0)) return showInventoryMessage(`Enter how many kg to add to TK-${container}.`);

  try {
    await api(`/tanks/${container}/refill`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ quantityKg: kg }),
    });
    input.value = '';
    showInventoryMessage(`Added ${fmtKg(kg)} to TK-${container}.`, 'ok');
    await refresh();
  } catch (error) {
    showInventoryMessage(error.message);
  }
}

function renderTable() {
  const signature = JSON.stringify([state.batches, state.selectedId]);
  if (signature === rendered.table) return;
  rendered.table = signature;

  const body = $('rows');
  body.replaceChildren();

  if (state.batches.length === 0) {
    body.append(el('tr', {}, el('td', { class: 'empty-row', colspan: '5', text: 'No batches yet. Create the first one.' })));
    return;
  }

  for (const batch of state.batches) {
    const selected = batch.batchId === state.selectedId;
    const row = el('tr', selected ? { 'aria-current': 'true' } : {},
      el('td', {}, el('button', {
        class: 'row-btn',
        type: 'button',
        text: batch.batchId,
        'aria-label': `Select batch ${batch.batchId}`,
        onclick: () => select(batch.batchId),
      })),
      el('td', { text: batch.recipeName }),
      el('td', { class: 'num', text: fmtKg(batch.plannedQuantityKg) }),
      el('td', {}, pill(batch.status)),
      el('td', { text: fmtDateTime(batch.createdAtUtc) }));
    body.append(row);
  }
}

function renderDetail() {
  const signature = JSON.stringify([state.detail, state.result, state.sending, state.selectedId]);
  if (signature === rendered.detail) return;
  rendered.detail = signature;

  const host = $('detail');
  host.replaceChildren();

  if (!state.detail) {
    host.append(el('p', { class: 'empty', text: 'No batches yet. Create the first one above.' }));
    return;
  }

  const { batch, events, messages } = state.detail;

  host.append(el('div', { class: 'detail-head' },
    el('span', { class: 'id', text: batch.batchId }), pill(batch.status)));

  host.append(el('dl', { class: 'facts' },
    el('dt', { text: 'Recipe' }), el('dd', { class: 'text', text: batch.recipeName }),
    el('dt', { text: 'Planned' }), el('dd', { text: fmtKg(batch.plannedQuantityKg) }),
    el('dt', { text: 'Created' }), el('dd', { text: fmtDateTime(batch.createdAtUtc) }),
    ...(batch.startedAtUtc ? [el('dt', { text: 'Started' }), el('dd', { text: fmtDateTime(batch.startedAtUtc) })] : []),
    ...(batch.completedAtUtc ? [el('dt', { text: 'Completed' }), el('dd', { text: fmtDateTime(batch.completedAtUtc) })] : []),
    ...batch.ingredients.flatMap((i) => [
      el('dt', { text: `TK-${i.container} ${i.ingredientName}` }),
      el('dd', { text: fmtKg(i.kilograms) }),
    ])));

  if (batch.errorMessage) {
    host.append(el('p', { class: 'error-box', text: batch.errorMessage }));
  }

  if (batch.status === 'Created') {
    host.append(el('button', {
      class: 'btn primary',
      type: 'button',
      id: 'sendBtn',
      text: state.sending ? 'Sending…' : 'Send to ERP',
      ...(state.sending ? { disabled: '' } : {}),
      onclick: sendToErp,
    }));
  }

  if (batch.status === 'Ready') {
    host.append(el('button', {
      class: 'btn primary',
      type: 'button',
      text: 'Start batch',
      onclick: () => runAction('start'),
    }));
  }

  if (batch.status === 'Running') {
    host.append(
      el('div', { class: 'progress', role: 'progressbar', 'aria-label': 'Mixing progress', 'aria-valuemin': '0', 'aria-valuemax': '100' },
        el('div', { class: 'progress-bar', id: 'progressBar' })),
      el('p', { class: 'progress-text mono', id: 'progressText' }),
      el('button', {
        class: 'btn danger',
        type: 'button',
        text: 'Simulate fault',
        onclick: () => runAction('fault'),
      }));
  }

  if (state.result && state.result.id === batch.batchId) {
    host.append(el('p', { class: 'msg', 'data-kind': state.result.kind, role: 'status', text: state.result.text }));
  }

  // Every XML sent to the ERP, kept in the database exactly as sent, with how the exchange ended.
  host.append(el('h3', { class: 'sub', text: 'XML messages sent to the ERP' }));
  if (messages.length === 0) {
    host.append(el('p', { class: 'empty', text: 'None yet. Send the batch to the ERP to create the first one.' }));
  } else {
    host.append(el('ul', { class: 'xml-list' }, ...messages.map((m) =>
      el('li', {},
        el('time', { datetime: m.createdAtUtc, text: fmtTime(m.createdAtUtc) }),
        el('span', { class: `xml-result r-${m.result}`, text: m.result }),
        el('a', {
          href: `/api/messages/${m.id}/xml`,
          target: '_blank',
          rel: 'noopener',
          text: 'View XML',
          'aria-label': `View the XML of message ${m.id}`,
        }),
        ...(m.detail ? [el('span', { class: 'xml-detail', text: m.detail })] : [])))));
  }

  host.append(el('h3', { class: 'sub', text: 'Event log' }));
  host.append(el('ul', { class: 'events' }, ...events.map((e) =>
    el('li', { class: `lvl-${e.level}` },
      el('time', { datetime: e.timestampUtc, text: fmtTime(e.timestampUtc) }),
      el('span', { text: e.message })))));
}

// ---------- new batch form ----------

function renderCharge() {
  const box = $('charge');
  const recipe = state.recipes.find((r) => r.id === Number($('recipe').value));
  const qty = Number($('qty').value);
  box.replaceChildren();

  if (!recipe || !(qty > 0)) {
    box.textContent = 'Enter a quantity to see the charge per tank.';
    return;
  }
  for (const i of recipe.ingredients) {
    box.append(el('div', { class: 'line' },
      el('span', { text: `TK-${i.container} ${i.ingredientName} (${i.percentage}%)` }),
      el('span', { text: fmtKg(qty * i.percentage / 100) })));
  }
  if (qty > maxKg) {
    box.append(el('div', { class: 'over', text: `Above the mixer capacity of ${maxKg} kg.` }));
  }
}

function showFormMessage(text, kind = 'bad') {
  const msg = $('formMsg');
  msg.hidden = !text;
  msg.dataset.kind = kind;
  msg.textContent = text ?? '';
}

async function createBatch(event) {
  event.preventDefault();
  showFormMessage(null);

  const recipeId = Number($('recipe').value);
  const quantity = Number($('qty').value);
  if (!recipeId) return showFormMessage('Select a recipe.');
  if (!(quantity > 0) || quantity > maxKg) {
    return showFormMessage(`Enter a quantity above 0 and up to ${maxKg} kg (the mixer capacity).`);
  }

  $('createBtn').disabled = true;
  try {
    const batch = await api('/batches', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ recipeId, plannedQuantityKg: quantity }),
    });
    $('qty').value = '';
    renderCharge();
    state.selectedId = batch.batchId;
    state.result = null;
    showFormMessage(`Created batch ${batch.batchId}.`, 'ok');
    await refresh();
  } catch (error) {
    showFormMessage(error.message);
  } finally {
    $('createBtn').disabled = false;
  }
}

// ---------- start / fault ----------

/** Mixing progress, computed from the stored start time. Runs often; touches only two elements. */
function updateProgress() {
  const batch = state.detail?.batch;
  const bar = $('progressBar');
  if (!bar || !batch?.startedAtUtc) return;

  const elapsed = Math.max(0, (Date.now() - Date.parse(batch.startedAtUtc)) / 1000);
  const fraction = Math.min(1, elapsed / cycleSeconds);
  bar.style.width = `${(fraction * 100).toFixed(1)}%`;
  bar.parentElement.setAttribute('aria-valuenow', String(Math.round(fraction * 100)));
  $('progressText').textContent = `Mixing ${Math.min(Math.floor(elapsed), cycleSeconds)} / ${cycleSeconds} s`;
}

/** POSTs a batch action ("start" or "fault") and shows the server's error if it is refused. */
async function runAction(action) {
  const id = state.selectedId;
  state.result = null;
  try {
    await api(`/batches/${encodeURIComponent(id)}/${action}`, { method: 'POST' });
  } catch (error) {
    state.result = { id, kind: 'bad', text: error.message };
  }
  await refresh();
}

// ---------- send to ERP ----------

async function sendToErp() {
  const id = state.selectedId;
  state.sending = true;
  state.result = null;
  renderDetail();

  try {
    const response = await fetch(`/api/batches/${encodeURIComponent(id)}/submit`, { method: 'POST' });
    const body = await response.json().catch(() => null);
    if (body?.outcome) {
      const kind = { Accepted: 'ok', Rejected: 'bad', Unavailable: 'warn' }[body.outcome];
      state.result = { id, kind, text: body.message };
    } else {
      state.result = { id, kind: 'bad', text: body?.error ?? `Request failed (HTTP ${response.status}).` };
    }
  } catch {
    state.result = { id, kind: 'bad', text: 'Cannot reach the Mini-MES API.' };
  } finally {
    state.sending = false;
    await refresh();
  }
}

// ---------- start ----------

async function start() {
  $('newBatch').addEventListener('submit', createBatch);
  $('recipe').addEventListener('change', renderCharge);
  $('qty').addEventListener('input', renderCharge);

  try {
    const config = await api('/config');
    maxKg = config.maxQuantityKg;
    cycleSeconds = config.mixerCycleSeconds;
    lowLevelFraction = Number(config.lowLevelFraction);
    $('qty').max = String(maxKg);
  } catch {
    // keep the defaults; the connection indicator will show the problem
  }

  try {
    state.recipes = await api('/recipes');
    $('recipe').replaceChildren(...state.recipes.map((r) => el('option', { value: String(r.id), text: r.name })));
  } catch (error) {
    showFormMessage(error.message);
  }

  await refresh();

  // Keep the screen live: poll faster while any batch is mixing; skip while hidden or sending.
  const poll = async () => {
    if (!document.hidden && !state.sending) await refresh();
    const mixing = state.batches.some((b) => b.status === 'Running');
    setTimeout(poll, mixing ? POLL_RUNNING_MS : POLL_MS);
  };
  setTimeout(poll, POLL_MS);
  setInterval(updateProgress, 250);   // smooth bar between polls
}

start();
