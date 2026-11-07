import { createHash, randomUUID } from 'node:crypto';
import { authorizedUpdate, HttpError } from './policy.mjs';

export const scope = device => `${device.botId}_${device.chatId}`;
export const selector = id => createHash('sha256').update(id).digest('hex').slice(0, 16);
export const registryKey = device => `routing/${scope(device)}`;
const commandsPrefix = (device, id = device.deviceId) => `commands/${scope(device)}/${id}/`;
const entries = registry => Object.values(registry?.devices || {});
const findDevice = (registry, id) => entries(registry).find(d => d.id === id || d.selector === id);
const label = entry => `${entry.name} · ${entry.selector.slice(0, 6)}`;
const actor = from => ({ id: from.id, is_bot: from.is_bot === true, first_name: from.first_name || 'Owner',
  ...(from.username ? { username: from.username } : {}) });
const chat = value => ({ id: value.id, type: value.type });
function commandUpdate(update, data) {
  if (update.callback_query) {
    const event = update.callback_query;
    // Existing menu text can contain clipboard data. Retain only what dispatch needs.
    return { update_id: update.update_id, callback_query: { id: event.id, data, from: actor(event.from),
      message: { message_id: event.message.message_id, chat: chat(event.message.chat) } } };
  }
  const message = update.message;
  return { update_id: update.update_id, message: { message_id: message.message_id, date: message.date,
    chat: chat(message.chat), from: actor(message.from), text: message.text } };
}

export async function registerDevice(records, device, instanceId, name, now = Date.now()) {
  if (!/^[a-f0-9]{32}$/.test(instanceId || '') || typeof name !== 'string'
      || name.length < 1 || name.length > 40 || /[\u0000-\u001f\u007f-\u009f]/.test(name))
    throw new HttpError(400, 'Valid device identity and display name are required');
  const entry = await records.change(registryKey(device), old => {
    const devices = Object.fromEntries(entries(old).filter(d => d.lastSeenAt >= now - 30 * 86400_000).map(d => [d.id, d]));
    const previous = Object.hasOwn(devices, device.deviceId) ? devices[device.deviceId] : null;
    if (previous && previous.instanceId !== instanceId)
      throw new HttpError(409, 'Device ID belongs to another PC. Set DeviceId to auto or choose a unique ID');
    if (!previous && Object.keys(devices).length >= 32) throw new HttpError(409, 'The relay supports up to 32 registered PCs');
    const entry = { id: device.deviceId, selector: selector(device.deviceId), instanceId, name, lastSeenAt: now };
    const value = { ...old, devices: { ...devices, [device.deviceId]: entry }, expiresAt: now + 90 * 86400_000 };
    return { value, result: entry };
  });
  await records.expire(registryKey(device), now + 90 * 86400_000);
  return entry;
}

export async function pollCommands(records, device, offset, now = Date.now()) {
  if (!Number.isSafeInteger(offset) || offset < 0 || offset > 2147483647) throw new HttpError(400, 'Invalid offset');
  const issued = await records.get(`command-sequences/${scope(device)}/${device.deviceId}`);
  if (offset > (issued?.sequence || 0) + 1) throw new HttpError(400, 'Offset exceeds issued commands');
  const acknowledged = await records.change(`command-cursors/${scope(device)}/${device.deviceId}`, old => {
    const next = Math.max(old?.offset || 0, offset);
    return next === (old?.offset || 0) ? { result: next } : { value: { offset: next }, result: next };
  });
  const prefix = commandsPrefix(device), ready = []; let scanned = 0;
  for await (const page of records.list({ prefix, paginate: true })) {
    for (const blob of page.blobs) {
      if (++scanned > 200) throw new HttpError(503, 'Command queue busy; retry shortly');
      const command = await records.get(blob.key);
      if (!command || command.expiresAt <= now || command.update.update_id < acknowledged) {
        await records.delete(blob.key); continue;
      }
      ready.push(command.update);
    }
  }
  return ready.sort((a, b) => a.update_id - b.update_id).slice(0, 100);
}

function choose(registry, update) {
  const callback = update.callback_query;
  if (callback) {
    const data = callback.data || '';
    if (data === 'pc:list') return { action: 'picker' };
    if (/^pc:[a-f0-9]{16}$/.test(data)) {
      const target = findDevice(registry, data.slice(3));
      return target ? { action: 'select', targetId: target.id } : { action: 'reply', text: 'PC unavailable. Open /devices again.' };
    }
    const match = data.match(/^d:([a-f0-9]{16}):(.{1,45})$/s);
    const target = match && findDevice(registry, match[1]);
    if (!target || Buffer.byteLength(data) > 64) return { action: 'reply', text: 'Open /devices and request a fresh /menu.' };
    return { action: 'command', targetId: target.id, update: commandUpdate(update, match[2]) };
  }
  const text = update.message.text?.trim() || '';
  const [command, argument] = text.split(/\s+/, 2);
  const lower = command.toLowerCase().split('@')[0];
  if (['/devices', '/start'].includes(lower)) return { action: 'picker' };
  if (lower === '/select') {
    const target = findDevice(registry, argument);
    return target ? { action: 'select', targetId: target.id } : { action: 'reply', text: 'Use /devices to choose a registered PC.' };
  }
  const target = findDevice(registry, registry?.selected);
  if (lower === '/device') return { action: 'reply', text: target ? `Selected PC: ${label(target)}` : 'Choose a PC with /devices first.' };
  return target ? { action: 'command', targetId: target.id, update: commandUpdate(update) } : { action: 'picker' };
}

async function saveCommand(records, device, decision) {
  const cursor = await records.get(`command-cursors/${scope(device)}/${decision.targetId}`);
  if (decision.sequence < (cursor?.offset || 0)) return;
  const prefix = commandsPrefix(device, decision.targetId);
  const update = { ...decision.update, update_id: decision.sequence };
  if (update.callback_query) update.callback_query = { ...update.callback_query,
    message: { ...update.callback_query.message, date: Math.floor(decision.createdAt / 1000) } };
  const key = prefix + String(decision.sequence).padStart(10, '0');
  const expiresAt = Math.min(decision.createdAt + 120_000,
    update.message ? update.message.date * 1000 + 120_000 : Infinity);
  await records.change(key, old => old ? { result: true } : { value: { update, expiresAt }, result: true });
  await records.expire(key, expiresAt);
}

async function sendChecked(sender, bucket, job) {
  const response = await sender({ ...job, files: [] }, bucket);
  if (!response.body?.ok) throw new HttpError(503, 'Telegram response deferred; retry the webhook');
}

export async function handleWebhook(records, bucket, device, update, sender, now = Date.now()) {
  if (!update || typeof update !== 'object' || !Number.isSafeInteger(update.update_id) || update.update_id < 0)
    throw new HttpError(400, 'Invalid Telegram update');
  if (!authorizedUpdate(update, device.chatId)) return;
  if (update.message && (typeof update.message.text !== 'string'
      || !Number.isSafeInteger(update.message.date) || update.message.date * 1000 < now - 120_000
      || update.message.date * 1000 > now + 30_000)) return;
  if (update.callback_query && (typeof update.callback_query.id !== 'string' || typeof update.callback_query.data !== 'string')) return;
  const key = `webhooks/${scope(device)}/${update.update_id}`;
  let decision = await records.get(key);
  if (decision?.expiresAt <= now) decision = null;
  if (!decision) {
    const registry = await records.get(registryKey(device));
    const chosen = choose(registry, update);
    if (chosen.action === 'select') {
      const source = update.message || update.callback_query.message;
      chosen.update = { update_id: update.update_id, message: { message_id: source.message_id || 0,
        date: Math.floor(now / 1000), chat: source.chat, from: update.message?.from || update.callback_query.from, text: '/menu' } };
    }
    // A durable per-PC sequence keeps polling offsets monotonic even when Telegram resets IDs after inactivity.
    const sequence = chosen.update ? await records.change(`command-sequences/${scope(device)}/${chosen.targetId}`, old => {
      const next = (old?.sequence || 0) + 1;
      if (next >= 2147483647) throw new HttpError(503, 'Command sequence exhausted');
      return { value: { sequence: next }, result: next };
    }) : undefined;
    const selectionSequence = chosen.action === 'select' ? await records.change(`selection-sequences/${scope(device)}`, old => {
      const next = (old?.sequence || 0) + 1;
      return { value: { sequence: next }, result: next };
    }) : undefined;
    decision = await records.change(key, old => old?.expiresAt > now ? { result: old } : {
      value: { ...chosen, sequence, selectionSequence, createdAt: now, expiresAt: now + 7 * 86400_000 },
      result: { ...chosen, sequence, selectionSequence, createdAt: now, expiresAt: now + 7 * 86400_000 }
    });
    await records.expire(key, decision.expiresAt);
  }
  const leaseId = randomUUID();
  const claimed = await records.change(key, old => {
    if (old.done) return { result: null };
    if (old.leaseUntil > now) throw new HttpError(503, 'Webhook processing in progress');
    const value = { ...old, leaseId, leaseUntil: now + 60_000 };
    return { value, result: value };
  });
  if (!claimed) return;
  decision = claimed;
  try {
    if (decision.action === 'select') {
      await records.change(registryKey(device), old => {
        if (!findDevice(old, decision.targetId)) throw new HttpError(503, 'Selected PC is unavailable');
        if ((old.selectedSequence || 0) >= decision.selectionSequence) return { result: true };
        return { value: { ...old, selected: decision.targetId, selectedSequence: decision.selectionSequence }, result: true };
      });
    }
    if (decision.update) await saveCommand(records, device, decision);
    const registry = await records.get(registryKey(device));
    if (update.callback_query) {
      const target = findDevice(registry, decision.targetId);
      // Acknowledge promptly; the PC reports the actual operation result later.
      await sendChecked(sender, bucket, { method: 'answerCallbackQuery', fields: { callback_query_id: update.callback_query.id,
        text: decision.action === 'reply' ? decision.text : target ? `Queued for ${label(target)}` : 'Choose a PC' } });
    }
    if (decision.action === 'picker') {
      const list = entries(registry).sort((a, b) => a.name.localeCompare(b.name));
      await sendChecked(sender, bucket, { method: 'sendMessage', fields: { chat_id: device.chatId, parse_mode: 'HTML',
        text: list.length ? '<b>Choose a PC</b>\nCommands apply to the selected PC. Menu buttons stay tied to their own PC.'
          : 'No PCs registered. Start the Windows app with Relay enabled and SharedBot set to true.',
        reply_markup: { inline_keyboard: list.map(d => [{ text: `${label(d)}${d.lastSeenAt < now - 90_000 ? ' (offline)' : ''}`, callback_data: 'pc:' + d.selector }]) }
      } });
    } else if (decision.action === 'reply' && !update.callback_query) {
      await sendChecked(sender, bucket, { method: 'sendMessage', fields: { chat_id: device.chatId, text: decision.text } });
    }
    await records.change(key, old => {
      if (old.leaseId !== leaseId) throw new HttpError(503, 'Webhook lease changed');
      return { value: { ...old, done: true, leaseUntil: 0 }, result: true };
    });
  } catch (error) {
    await records.change(key, old => old?.leaseId === leaseId
      ? { value: { ...old, leaseUntil: 0 }, result: true } : { result: true }).catch(() => {});
    throw error;
  }
}
