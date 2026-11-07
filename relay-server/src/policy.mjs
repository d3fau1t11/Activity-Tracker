export const durableMethods = new Set(['sendMessage', 'sendPhoto', 'sendDocument', 'editMessageText']);
export const allowedMethods = new Set([...durableMethods, 'getMe', 'getUpdates', 'getDelivery', 'configureRelay', 'sendChatAction', 'setMyCommands', 'answerCallbackQuery']);
export const validDeviceId = value => typeof value === 'string' && /^[A-Za-z0-9_-]{3,64}$/.test(value);
export const validDeliveryId = value => typeof value === 'string' && /^[a-f0-9]{32}$/.test(value);

export class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}
export function validateEnvelope(body, device) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) throw new HttpError(400, 'Invalid request');
  if (!validDeviceId(body.deviceId) || !validDeliveryId(body.id) || !allowedMethods.has(body.method))
    throw new HttpError(400, 'Unsupported request');
  if (!device || device.enabled !== true || device.deviceId !== body.deviceId) throw new HttpError(403, 'Device not authorized');
  if (!body.fields || typeof body.fields !== 'object' || Array.isArray(body.fields)) throw new HttpError(400, 'Invalid fields');
  if (JSON.stringify(body.fields).length > 64 * 1024) throw new HttpError(413, 'Fields exceed limit');
  if ('chat_id' in body.fields && String(body.fields.chat_id) !== String(device.chatId))
    throw new HttpError(403, 'Chat not authorized');
  if (['sendMessage', 'sendPhoto', 'sendDocument', 'editMessageText', 'sendChatAction'].includes(body.method)
      && String(body.fields.chat_id) !== String(device.chatId)) throw new HttpError(403, 'Chat is required');
  const files = body.files || [];
  if (!Array.isArray(files) || files.length > 1) throw new HttpError(400, 'Invalid attachments');
  for (const file of files) {
    const field = body.method === 'sendPhoto' ? 'photo' : body.method === 'sendDocument' ? 'document' : null;
    if (!file || typeof file !== 'object' || file.field !== field || file.contentType !== 'image/webp'
        || file.path !== `devices/${body.deviceId}/uploads/${body.id}/${field}.webp`
        || typeof file.name !== 'string' || !/^[A-Za-z0-9_.-]{1,180}\.webp$/.test(file.name))
      throw new HttpError(400, 'Invalid private image reference');
  }
  // Never let a device make Telegram fetch an arbitrary URL or attach a foreign file_id.
  for (const key of ['photo', 'document']) {
    if (key in body.fields && (!files.some(f => f.field === key) || body.fields[key] !== `attach://${key}`))
      throw new HttpError(400, 'Only uploaded private images are supported');
  }
  return { deviceId: body.deviceId, id: body.id, botId: device.botId, method: body.method, fields: body.fields, files };
}

export function authorizedUpdate(update, chatId) {
  // Pairing supports private chats only, so the actor and chat are both the owner's ID.
  const event = update.callback_query || update.message;
  const chat = update.callback_query?.message?.chat || update.message?.chat;
  return chat?.type === 'private' && String(chat.id) === String(chatId) && String(event?.from?.id) === String(chatId);
}

export function retryDecision(status, body, attempt, now = Date.now()) {
  if (body?.ok === true) return { state: 'delivered', nextAttempt: now };
  if (status === 429 || status >= 500 || status === 0) {
    const delay = Math.max(Number(body?.parameters?.retry_after || 0) * 1000,
      Math.min(6 * 3600_000, 30_000 * 2 ** Math.min(attempt, 10)));
    return { state: attempt >= 20 ? 'failed' : 'pending', nextAttempt: now + delay };
  }
  return { state: 'failed', nextAttempt: now };
}
