import { createHash } from 'node:crypto';
import { services, authenticate, deviceFor } from '../../src/services.mjs';
import { registerDevice } from '../../src/routing.mjs';
import { CHUNK_BYTES, MAX_IMAGE_BYTES } from '../../src/images.mjs';
import { validDeviceId, validDeliveryId, HttpError } from '../../src/policy.mjs';
import { json, errorResponse } from '../../src/http.mjs';

async function readChunk(request, expected) {
  if (!request.body) throw new HttpError(400, 'Image chunk required');
  const reader = request.body.getReader();
  const chunks = []; let bytes = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      bytes += value.length;
      if (bytes > expected) { await reader.cancel(); throw new HttpError(413, 'Image chunk exceeds limit'); }
      chunks.push(Buffer.from(value));
    }
  } finally { reader.releaseLock(); }
  if (bytes !== expected) throw new HttpError(400, 'Image chunk length mismatch');
  return Buffer.concat(chunks);
}

export function createHandler(getServices = services) {
return async function handler(request) {
  try {
    if (request.method !== 'POST') throw new HttpError(405, 'POST required');
    const service = getServices();
    authenticate(request, service);
    const { records, bucket } = service;
    const deviceId = request.headers.get('x-relay-device'), id = request.headers.get('x-relay-delivery');
    const field = request.headers.get('x-relay-field');
    if (!validDeviceId(deviceId) || !validDeliveryId(id) || !['photo', 'document'].includes(field))
      throw new HttpError(400, 'Invalid image destination');
    const device = deviceFor(service, deviceId);
    const integerHeader = name => {
      const value = request.headers.get(name);
      if (!/^(0|[1-9][0-9]{0,8})$/.test(value || '')) throw new HttpError(400, 'Invalid chunk headers');
      return Number(value);
    };
    const part = integerHeader('x-relay-part'), parts = integerHeader('x-relay-parts'), bytes = integerHeader('x-relay-bytes');
    const sha256 = request.headers.get('x-relay-sha256');
    if (bytes < 12 || bytes > MAX_IMAGE_BYTES || parts !== Math.ceil(bytes / CHUNK_BYTES)
        || part >= parts || !/^[a-f0-9]{64}$/.test(sha256 || '')
        || request.headers.get('content-type') !== 'application/octet-stream')
      throw new HttpError(400, 'Invalid image chunk headers');
    const data = await readChunk(request, Math.min(CHUNK_BYTES, bytes - part * CHUNK_BYTES));
    if (part === 0 && (data.toString('ascii', 0, 4) !== 'RIFF' || data.toString('ascii', 8, 12) !== 'WEBP'))
      throw new HttpError(400, 'WebP image required');
    if (service.sharedBot) {
      let name;
      try { name = decodeURIComponent(request.headers.get('x-relay-name') || ''); }
      catch { throw new HttpError(400, 'Invalid device name'); }
      await registerDevice(records, device, request.headers.get('x-relay-instance'), name);
    }
    const path = `devices/${deviceId}/uploads/${id}/${field}.webp`;
    const manifest = { bytes, parts, sha256 };
    const key = 'media/' + createHash('sha256').update(path).digest('hex');
    const now = Date.now();
    const media = await records.change(key, old => {
      if (old) {
        if (old.expiresAt <= now) throw new HttpError(410, 'Image upload expired');
        if (old.sha256 !== sha256 || old.bytes !== bytes || old.parts !== parts || old.botId !== device.botId
            || old.deviceId !== deviceId || old.chatId !== device.chatId)
          throw new HttpError(409, 'Image ID already has different content');
        return { result: old };
      }
      const value = { ...manifest, path, botId: device.botId, deviceId, chatId: device.chatId, expiresAt: now + 8 * 86400_000 };
      return { value, result: value };
    });
    // Register cleanup before blobs are written; interrupted uploads also expire.
    await records.expire(key, media.expiresAt, { path, parts });
    await bucket.putPart(path, manifest, part, data);
    return json({ ok: true, part });
  } catch (error) { return errorResponse(error); }
};
}
export default createHandler();
