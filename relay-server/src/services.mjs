import { createHash, timingSafeEqual } from 'node:crypto';
import { repository } from './store.mjs';
import { netlifyImages } from './images.mjs';
import { validDeviceId, HttpError } from './policy.mjs';

export function services() {
  const token = process.env.TELEGRAM_BOT_TOKEN, deviceId = process.env.RELAY_DEVICE_ID, chatId = process.env.TELEGRAM_CHAT_ID;
  if (!/^\d+:[A-Za-z0-9_-]{8,}$/.test(token || '') || !validDeviceId(deviceId) || !/^[0-9]{1,20}$/.test(chatId || ''))
    throw new HttpError(503, 'Configure the server bot token, device ID, and private chat ID');
  return { token, device: { deviceId, chatId, botId: token.split(':')[0], enabled: process.env.RELAY_ENABLED !== 'false' },
    records: repository(), bucket: netlifyImages() };
}

export function authenticate(request, service) {
  const supplied = request.headers.get('authorization')?.match(/^Bearer (\S+)$/)?.[1] || '';
  const digest = value => createHash('sha256').update(value).digest();
  if (!service.token || !supplied || supplied.length > 256 || !timingSafeEqual(digest(supplied), digest(service.token)))
    throw new HttpError(401, 'Bot token authentication required');
  if (!service.device.enabled) throw new HttpError(403, 'Device disabled');
}
