import { createHash } from 'node:crypto';
import { services, authenticate, deviceFor, webhookSecret } from '../../src/services.mjs';
import { registerDevice, pollCommands } from '../../src/routing.mjs';
import { durableMethods, validateEnvelope, authorizedUpdate, HttpError } from '../../src/policy.mjs';
import { enqueue, deliver, deliveryResponse, jobKey } from '../../src/delivery.mjs';
import { callTelegram } from '../../src/telegram.mjs';
import { readBody, json, errorResponse } from '../../src/http.mjs';

export function createHandler(getServices = services, sender = callTelegram) {
return async function handler(request) {
  try {
    const service = getServices();
    authenticate(request, service);
    const body = await readBody(request);
    const { records, bucket } = service;
    const device = deviceFor(service, body?.deviceId);
    const envelope = validateEnvelope(body, device);
    if (envelope.method === 'configureRelay') {
      const fields = service.sharedBot ? {
        url: new URL('/api/telegram-webhook', request.url).href, secret_token: webhookSecret(service.token),
        allowed_updates: ['message', 'callback_query'], max_connections: 1, drop_pending_updates: true
      } : { drop_pending_updates: true };
      const result = await sender({ method: service.sharedBot ? 'setWebhook' : 'deleteWebhook', fields, files: [] }, bucket);
      return json(result.body, result.status);
    }
    if (service.sharedBot) await registerDevice(records, device, body.instanceId, body.deviceName);
    if (envelope.method === 'getDelivery') {
      const job = await records.get(jobKey(envelope.deviceId, envelope.id));
      if (!job) throw new HttpError(404, 'Delivery not found');
      if (job.botId !== device.botId || job.deviceId !== device.deviceId || String(job.fields.chat_id) !== device.chatId)
        throw new HttpError(403, 'Delivery belongs to another bot, device, or private chat');
      const response = deliveryResponse(job);
      return json(response.body, response.status, true);
    }
    if (envelope.method === 'getUpdates') {
      const offset = Number(envelope.fields.offset ?? 0);
      if (!Number.isSafeInteger(offset) || offset < 0) throw new HttpError(400, 'Invalid offset');
      if (service.sharedBot) return json({ ok: true, result: await pollCommands(records, device, offset) });
      // Only the PC initiates polling; Netlify never runs a second polling loop.
      const result = await sender({ ...envelope, fields: { ...envelope.fields, offset, timeout: 0, limit: 100 } }, bucket);
      if (result.body.ok && Array.isArray(result.body.result)) {
        const now = Date.now();
        result.body.result = result.body.result.map(update =>
          authorizedUpdate(update, device.chatId) && (!update.message?.date || update.message.date * 1000 >= now - 120_000)
            ? update : { update_id: update.update_id });
      }
      return json(result.body, result.status);
    }
    if (durableMethods.has(envelope.method)) {
      for (const image of envelope.files) {
        const media = await records.get('media/' + createHash('sha256').update(image.path).digest('hex'));
        if (!media) throw new HttpError(404, 'Image upload missing');
        if (media.botId !== device.botId || media.deviceId !== device.deviceId || media.chatId !== device.chatId)
          throw new HttpError(403, 'Image belongs to another bot, device, or private chat');
        if (media.expiresAt <= Date.now()) throw new HttpError(410, 'Image upload expired');
      }
      const notBefore = body.notBefore ?? Date.now();
      if (!Number.isSafeInteger(notBefore) || notBefore > Date.now() + 86400_000 || notBefore < 0)
        throw new HttpError(400, 'Invalid retry time');
      const key = await enqueue(records, envelope, Date.now(), notBefore);
      const response = deliveryResponse(await deliver(records, bucket, key, device, Date.now(), sender));
      return json(response.body, response.status, true);
    }
    const result = await sender(envelope, bucket);
    return json(result.body, result.status);
  } catch (error) { return errorResponse(error); }
};
}
export default createHandler();
