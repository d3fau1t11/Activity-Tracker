import { createHash, timingSafeEqual } from 'node:crypto';
import { repository } from './store.mjs';
import { netlifyImages } from './images.mjs';
import { validDeviceId, HttpError } from './policy.mjs';

export function services() {
  const token = process.env.TELEGRAM_BOT_TOKEN, deviceId = process.env.RELAY_DEVICE_ID, chatId = process.env.TELEGRAM_CHAT_ID;
  const sharedBot = process.env.RELAY_MULTI_DEVICE !== 'false';
  if (!/^\d+:[A-Za-z0-9_-]{8,}$/.test(token || '') || (!sharedBot && !validDeviceId(deviceId)) || !/^[0-9]{1,20}$/.test(chatId || ''))
    throw new HttpError(503, sharedBot ? 'Configure the server bot token and private chat ID'
      : 'Configure the server bot token, device ID, and private chat ID');
  return { token, sharedBot, device: { deviceId, chatId, botId: token.split(':')[0], enabled: process.env.RELAY_ENABLED !== 'false' },
    records: repository(), bucket: netlifyImages() };
}

export function deviceFor(service, id) {
  if (!validDeviceId(id)) throw new HttpError(400, 'Invalid device ID');
  if (!service.sharedBot && service.device.deviceId !== id) throw new HttpError(403, 'Device not authorized');
  return { ...service.device, deviceId: id };
}

export const webhookSecret = token => createHash('sha256').update('ActivityTracker.TelegramWebhook.v1:' + token).digest('hex');
export function authenticateWebhook(request, service) {
  const supplied = request.headers.get('x-telegram-bot-api-secret-token') || '';
  const expected = webhookSecret(service.token);
  if (Buffer.byteLength(supplied) !== Buffer.byteLength(expected) || !timingSafeEqual(Buffer.from(supplied), Buffer.from(expected)))
    throw new HttpError(401, 'Telegram webhook authentication required');
  if (!service.sharedBot || !service.device.enabled) throw new HttpError(403, 'Shared bot routing disabled');
}

export function authenticate(request, service) {
  const supplied = request.headers.get('authorization')?.match(/^Bearer (\S+)$/)?.[1] || '';
  const digest = value => createHash('sha256').update(value).digest();
  if (!service.token || !supplied || supplied.length > 256 || !timingSafeEqual(digest(supplied), digest(service.token)))
    throw new HttpError(401, 'Bot token authentication required');
  if (!service.device.enabled) throw new HttpError(403, 'Device disabled');
}
