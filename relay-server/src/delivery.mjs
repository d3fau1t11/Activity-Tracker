import { createHash, randomUUID } from 'node:crypto';
import { retryDecision, HttpError } from './policy.mjs';
import { callTelegram } from './telegram.mjs';

export const jobKey = (deviceId, id) => `jobs/${deviceId}_${id}`;
export const pendingKey = key => key.replace(/^jobs\//, 'pending/');

export async function enqueue(records, envelope, now = Date.now(), notBefore = now) {
  const key = jobKey(envelope.deviceId, envelope.id);
  const hash = createHash('sha256').update(JSON.stringify(envelope)).digest('hex');
  const job = await records.change(key, old => {
    if (old) {
      if (old.hash !== hash) throw new HttpError(409, 'Delivery ID already has different content');
      return { result: old };
    }
    const value = { ...envelope, hash, state: 'pending', attempts: 0, createdAt: now,
      nextAttempt: Math.max(now, notBefore), leaseUntil: 0, expiresAt: now + 30 * 86400_000 };
    return { value, result: value };
  });
  // Repair these indexes on replay after an interrupted enqueue.
  if (job.state === 'pending') await records.set(pendingKey(key), { key });
  await records.expire(key, job.expiresAt);
  return key;
}

export async function deliver(records, bucket, key, device, now = Date.now(), sender = callTelegram) {
  const leaseId = randomUUID();
  const claim = await records.change(key, job => {
    if (!job) throw new HttpError(404, 'Delivery not found');
    if (job.state !== 'pending' || job.nextAttempt > now || job.leaseUntil > now)
      return { result: { job, send: false } };
    let error;
    if (job.createdAt < now - 7 * 86400_000) error = { status: 410, description: 'Delivery expired after seven days' };
    else if (!device.enabled || job.deviceId !== device.deviceId || job.botId !== device.botId
        || String(job.fields.chat_id) !== device.chatId) error = { status: 403, description: 'Device, bot, or private chat configuration changed' };
    if (error) {
      const value = { ...job, state: 'failed', status: error.status,
        result: { ok: false, error_code: error.status, description: error.description } };
      return { value, result: { job: value, send: false } };
    }
    const value = { ...job, leaseId, leaseUntil: now + 60_000, attempts: job.attempts + 1 };
    return { value, result: { job: value, send: true } };
  });
  if (!claim.send) {
    if (claim.job.state !== 'pending') await records.delete(pendingKey(key));
    return claim.job;
  }
  let result;
  try { result = await sender(claim.job, bucket); }
  catch (error) {
    const status = error instanceof HttpError ? error.status : 0;
    result = { status, body: { ok: false, error_code: status || 503,
      description: error instanceof HttpError ? error.message : 'Telegram delivery temporarily unavailable' } };
  }
  const decision = retryDecision(result.status, result.body, claim.job.attempts, now);
  const saved = await records.change(key, old => {
    if (!old || old.leaseId !== leaseId) throw new HttpError(503, 'Delivery lease changed; query the stored result');
    const value = { ...old, ...decision, leaseUntil: 0, result: result.body, status: result.status, updatedAt: now };
    return { value, result: value };
  });
  if (saved.state !== 'pending') await records.delete(pendingKey(key));
  return saved;
}

export function deliveryResponse(job) {
  if (job.state === 'delivered') return { status: 200, body: job.result };
  if (job.state === 'failed') return { status: Math.max(400, job.status || 503), body: job.result };
  return { status: 202, body: { ok: false, error_code: 503, description: 'Stored privately in Netlify for Telegram retry' } };
}
