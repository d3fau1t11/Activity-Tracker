import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import sharp from 'sharp';
import { createHandler as telegramHandler } from '../netlify/functions/telegram.mjs';
import { createHandler as uploadHandler } from '../netlify/functions/upload-image.mjs';
import { createHandler as scheduledHandler } from '../netlify/functions/retry-deliveries.mjs';
import { validateEnvelope, retryDecision } from '../src/policy.mjs';
import { enqueue, deliver, jobKey } from '../src/delivery.mjs';
import { cleanupExpired } from '../src/cleanup.mjs';
import { callTelegram } from '../src/telegram.mjs';
import { repository } from '../src/store.mjs';
import { netlifyImages, CHUNK_BYTES } from '../src/images.mjs';
import { FakeBlobs } from './fake-blobs.mjs';

const token = '123:fixture-only-token', deviceId = 'my-pc', id = 'a'.repeat(32);
const path = `devices/${deviceId}/uploads/${id}/photo.webp`;
process.env.TELEGRAM_BOT_TOKEN = token;
function fixture() {
  const store = new FakeBlobs(), imageStore = new FakeBlobs();
  return { token, device: { deviceId, botId: '123', chatId: '123', enabled: true },
    store, imageStore, records: repository(store), bucket: netlifyImages(imageStore) };
}
function envelope(extra = {}) { return { deviceId, id, method: 'sendMessage', fields: { chat_id: '123', text: 'Fixture report' }, files: [], ...extra }; }
function request(body, credential = token) { return new Request('https://fixture.test/api/telegram', {
  method: 'POST', headers: { authorization: `Bearer ${credential}`, 'content-type': 'application/json' }, body: JSON.stringify(body) }); }
const senderOk = async () => ({ status: 200, body: { ok: true, result: { message_id: 42 } } });

test('malformed JSON envelopes and attachments return client errors without sending or storing', async () => {
  const f = fixture(); let sent = 0;
  const handler = telegramHandler(() => f, async () => { sent++; return senderOk(); });
  for (const body of [null, [], 42, 'text', envelope({ files: [null] })])
    assert.equal((await handler(request(body))).status, 400);
  assert.equal(sent, 0); assert.equal(f.store.data.size, 0);
});

test('JSON requests are bounded by streamed bytes and oversized bodies are cancelled', async () => {
  const f = fixture(), handler = telegramHandler(() => f);
  let cancelled = false, pulls = 0;
  const stream = new ReadableStream({
    pull(controller) { pulls++; controller.enqueue(new Uint8Array(65536)); },
    cancel() { cancelled = true; }
  });
  const response = await handler(new Request('https://fixture.test/api/telegram', {
    method: 'POST', body: stream, duplex: 'half', headers: { authorization: `Bearer ${token}` }
  }));
  assert.equal(response.status, 413); assert.equal(cancelled, true); assert.ok(pulls <= 5);
  const utf8 = envelope({ fields: { chat_id: '123', text: '€'.repeat(50000) } });
  assert.equal((await handler(request(utf8))).status, 413);
  const declared = request(envelope()); declared.headers.set('content-length', '131073');
  assert.equal((await handler(declared)).status, 413); assert.equal(declared.bodyUsed, false);
  assert.equal(f.store.data.size, 0);
});

test('changing the configured chat cannot disclose an old receipt or reuse its private image', async () => {
  const f = fixture(); let sent = 0;
  const handler = telegramHandler(() => f, async () => { sent++; return senderOk(); });
  assert.equal((await handler(request(envelope()))).status, 200);
  const data = await webp(), upload = uploadHandler(() => f);
  assert.equal((await upload(uploadRequest(data))).status, 200);
  f.device.chatId = '456';
  assert.equal((await handler(request(envelope({ method: 'getDelivery', fields: {} })))).status, 403);
  assert.equal((await upload(uploadRequest(data))).status, 409);
  const photo = envelope({ method: 'sendPhoto', fields: { chat_id: '456', photo: 'attach://photo' },
    files: [{ field: 'photo', name: 'image.webp', contentType: 'image/webp', path }] });
  assert.equal((await handler(request(photo))).status, 403); assert.equal(sent, 1);
});

test('the secret bot credential, configured device and private chat are required before reading a request', async () => {
  const f = fixture(); let sent = 0;
  const handler = telegramHandler(() => f, async () => { sent++; return senderOk(); });
  const unauthorized = request(envelope(), '123');
  assert.equal((await handler(unauthorized)).status, 401); assert.equal(unauthorized.bodyUsed, false);
  assert.equal((await handler(request(envelope({ deviceId: 'other-pc' })))).status, 403);
  assert.equal((await handler(request(envelope({ fields: { chat_id: '456', text: 'Foreign' } })))).status, 403);
  assert.throws(() => validateEnvelope(envelope({ method: 'sendPhoto', fields: { chat_id: '123', photo: 'https://foreign.test' } }), f.device));
  assert.throws(() => validateEnvelope(envelope({ method: 'sendPhoto', files: [{ field: 'photo', name: 'image.webp', contentType: 'image/webp', path: 'devices/foreign/image.webp' }] }), f.device));
  f.device.enabled = false;
  assert.equal((await handler(request(envelope()))).status, 403);
  assert.equal(sent, 0); assert.equal(f.store.data.size, 0);
});

test('Netlify stores an outage delivery and replays its result once under a stable ID', async () => {
  const f = fixture(); let sent = 0;
  const sender = async () => (++sent === 1 ? { status: 503, body: { ok: false } } : senderOk());
  const handler = telegramHandler(() => f, sender);
  const first = await handler(request(envelope()));
  assert.equal(first.status, 202); assert.equal(first.headers.get('x-relay-stored'), 'true');
  const key = jobKey(deviceId, id);
  assert.equal((await f.records.get(key)).state, 'pending');
  await deliver(f.records, f.bucket, key, f.device, Date.now() + 120000, sender);
  const replay = await handler(request(envelope()));
  assert.equal(replay.status, 200); assert.equal((await replay.json()).result.message_id, 42); assert.equal(sent, 2);
  assert.equal((await handler(request(envelope({ fields: { chat_id: '123', text: 'Changed' } })))).status, 409);
  assert.equal((await handler(request(envelope({ method: 'getDelivery', fields: {} })))).status, 200); assert.equal(sent, 2);
});

test('conditional blob writes preserve concurrent changes and one delivery lease prevents concurrent sends', async () => {
  const f = fixture(), now = Date.now();
  const change = () => f.records.change('counter', old => ({ value: { count: (old?.count || 0) + 1 }, result: true }));
  await Promise.all([change(), change()]); assert.equal((await f.records.get('counter')).count, 2);
  const normalized = validateEnvelope(envelope(), f.device);
  await Promise.all([enqueue(f.records, normalized, now), enqueue(f.records, normalized, now)]);
  let sent = 0, release;
  const paused = new Promise(resolve => release = resolve);
  const sender = async () => { sent++; await paused; return senderOk(); };
  const key = jobKey(deviceId, id);
  const first = deliver(f.records, f.bucket, key, f.device, now, sender);
  await new Promise(resolve => setImmediate(resolve));
  await deliver(f.records, f.bucket, key, f.device, now, sender); assert.equal(sent, 1);
  release(); await first;
  const second = await enqueue(f.records, { ...normalized, id: 'b'.repeat(32) }, now);
  f.device.enabled = false;
  assert.equal((await deliver(f.records, f.bucket, second, f.device, now, sender)).state, 'failed'); assert.equal(sent, 1);
});

test('Telegram rate limits and an explicit direct retry time delay the cloud send', async () => {
  assert.equal(retryDecision(429, { parameters: { retry_after: 300 } }, 1, 0).nextAttempt, 300000);
  assert.equal(retryDecision(400, { ok: false }, 1).state, 'failed');
  const f = fixture(); let sent = 0;
  const response = await telegramHandler(() => f, async () => { sent++; return senderOk(); })(request(envelope({ notBefore: Date.now() + 300000 })));
  assert.equal(response.status, 202); assert.equal(sent, 0);
});

test('fallback polling enforces the private actor, retains offsets and never installs a webhook', async () => {
  const f = fixture(); let method;
  const fresh = { update_id: 10, message: { date: Math.floor(Date.now() / 1000), from: { id: 123 }, chat: { id: 123, type: 'private' }, text: '/status' } };
  const handler = telegramHandler(() => f, async job => {
    method = job.method; assert.equal(job.fields.timeout, 0);
    return { status: 200, body: { ok: true, result: [fresh,
      { ...fresh, update_id: 11, message: { ...fresh.message, from: { id: 456 } } },
      { ...fresh, update_id: 12, message: { ...fresh.message, date: 1 } }] } };
  });
  const response = await handler(request(envelope({ method: 'getUpdates', fields: { offset: 0, timeout: 30 } })));
  const result = (await response.json()).result;
  assert.equal(method, 'getUpdates'); assert.equal(result[0].message.text, '/status');
  assert.deepEqual(result[1], { update_id: 11 }); assert.deepEqual(result[2], { update_id: 12 });
  assert.equal((await handler(request(envelope({ method: 'getUpdates', fields: { offset: -1 } })))).status, 400);
});

test('authorized setup removes a webhook through Netlify for direct-first polling', async () => {
  const f = fixture(); let called;
  const handler = telegramHandler(() => f, async job => { called = job; return senderOk(); });
  assert.equal((await handler(request(envelope({ method: 'configureRelay', fields: {} }), 'wrong'))).status, 401);
  assert.equal((await handler(request(envelope({ method: 'configureRelay', fields: {} })))).status, 200);
  assert.equal(called.method, 'deleteWebhook'); assert.equal(called.fields.drop_pending_updates, true);
});

const hash = data => createHash('sha256').update(data).digest('hex');
function uploadRequest(data, full = data, extra = {}) {
  return new Request('https://fixture.test/api/upload-image', { method: 'POST', body: data, headers: {
    authorization: `Bearer ${token}`, 'content-type': 'application/octet-stream',
    'x-relay-device': deviceId, 'x-relay-delivery': id, 'x-relay-field': 'photo',
    'x-relay-part': '0', 'x-relay-parts': String(Math.ceil(full.length / CHUNK_BYTES)),
    'x-relay-bytes': String(full.length), 'x-relay-sha256': hash(full), ...extra } });
}
async function webp() { return sharp({ create: { width: 48, height: 32, channels: 3, background: '#167890' } }).webp({ lossless: true }).toBuffer(); }

test('private image upload rejects unauthorized callers, foreign devices, excessive sizes and invalid chunks', async () => {
  const f = fixture(), data = await webp(), handler = uploadHandler(() => f);
  const bad = uploadRequest(data, data, { authorization: 'Bearer wrong' });
  assert.equal((await handler(bad)).status, 401); assert.equal(bad.bodyUsed, false);
  assert.equal((await handler(uploadRequest(data, data, { 'x-relay-device': 'other-pc' }))).status, 403);
  for (const extra of [{ 'x-relay-field': '../foreign' }, { 'x-relay-bytes': '16777217' },
    { 'x-relay-parts': '17' }, { 'x-relay-part': '-1' }, { 'x-relay-part': '1' }, { 'x-relay-sha256': 'invalid' }])
    assert.equal((await handler(uploadRequest(data, data, extra))).status, 400);
  assert.equal((await handler(uploadRequest(Buffer.alloc(12)))).status, 400);
  assert.equal((await handler(uploadRequest(data.subarray(0, data.length - 1), data))).status, 400);
  assert.equal((await handler(uploadRequest(Buffer.concat([data, Buffer.alloc(1)]), data))).status, 413);
  assert.equal(f.imageStore.data.size, 0);
  const response = await handler(uploadRequest(data));
  assert.equal(response.status, 200); assert.equal(response.headers.has('x-relay-stored'), false);
  assert.deepEqual((await f.bucket.file(path).download())[0], data);
});

test('immutable chunk retries assemble exact WebP bytes and detect incomplete or corrupt data', async () => {
  const f = fixture(), handler = uploadHandler(() => f);
  const full = Buffer.alloc(CHUNK_BYTES + 23, 75); full.write('RIFF', 0); full.write('WEBP', 8);
  const first = full.subarray(0, CHUNK_BYTES), last = full.subarray(CHUNK_BYTES);
  assert.equal((await handler(uploadRequest(first, full))).status, 200);
  assert.equal((await handler(uploadRequest(first, full))).status, 200); assert.equal(f.imageStore.data.size, 2);
  await assert.rejects(() => f.bucket.file(path).download(), /incomplete/);
  const changed = Buffer.from(first); changed[100] ^= 1;
  assert.equal((await handler(uploadRequest(changed, full))).status, 409);
  assert.equal((await handler(uploadRequest(first, full, { 'x-relay-sha256': 'f'.repeat(64) }))).status, 409);
  assert.equal((await handler(uploadRequest(last, full, { 'x-relay-part': '1' }))).status, 200);
  assert.deepEqual((await f.bucket.file(path).download())[0], full);
  f.imageStore.data.get(path + '/part-1').data[0] ^= 1;
  await assert.rejects(() => f.bucket.file(path).download(), /integrity/);
  assert.throws(() => f.bucket.file('foreign/../file'), /Invalid private/);
});

test('private WebP originals remain documents while photo mode creates a JPEG preview', async () => {
  const f = fixture(), data = await webp();
  await uploadHandler(() => f)(uploadRequest(data));
  for (const [method, field, type] of [['sendPhoto', 'photo', 'image/jpeg'], ['sendDocument', 'document', 'image/webp']]) {
    let sent;
    await callTelegram({ method, fields: { chat_id: '123' }, files: [{ field, name: 'image.webp', path }] }, f.bucket,
      async (_url, options) => { sent = options.body.get(field); return new Response('{"ok":true}', { status: 200 }); });
    assert.equal(sent.type, type);
    assert.equal((await sharp(Buffer.from(await sent.arrayBuffer())).metadata()).format, method === 'sendPhoto' ? 'jpeg' : 'webp');
  }
});

test('shared-PC image uploads register header identities and keep bytes, jobs, and recipients separate', async () => {
  const f = fixture(); f.sharedBot = true;
  const data = await webp(), upload = uploadHandler(() => f);
  const identities = [
    { deviceId, deviceName: 'Office PC', instanceId: '1'.repeat(32) },
    { deviceId: 'other-pc', deviceName: 'Home PC', instanceId: '2'.repeat(32) }
  ];
  for (const identity of identities) {
    const headers = { 'x-relay-device': identity.deviceId, 'x-relay-instance': identity.instanceId,
      'x-relay-name': encodeURIComponent(identity.deviceName) };
    assert.equal((await upload(uploadRequest(data, data, headers))).status, 200);
    const imagePath = `devices/${identity.deviceId}/uploads/${id}/photo.webp`;
    assert.deepEqual((await f.bucket.file(imagePath).download())[0], data);
    const body = envelope({ ...identity, method: 'sendPhoto',
      fields: { chat_id: '123', photo: 'attach://photo' },
      files: [{ field: 'photo', name: 'image.webp', contentType: 'image/webp', path: imagePath }] });
    const send = telegramHandler(() => f, async job => {
      assert.equal(job.deviceId, identity.deviceId);
      assert.deepEqual((await f.bucket.file(job.files[0].path).download())[0], data);
      return senderOk();
    });
    assert.equal((await send(request(body))).status, 200);
    assert.equal((await f.records.get(jobKey(identity.deviceId, id))).state, 'delivered');
    assert.equal((await send(request({ ...body, fields: { ...body.fields, chat_id: '456' } }))).status, 403);
    assert.equal((await send(request({ ...body, files: [{ ...body.files[0],
      path: `devices/foreign-pc/uploads/${id}/photo.webp` }] }))).status, 400);
  }
  const registry = await f.records.get('routing/123_123');
  assert.equal(Object.keys(registry.devices).length, 2);
  assert.equal(registry.devices[deviceId].name, 'Office PC');
  assert.equal((await upload(uploadRequest(data, data, {
    'x-relay-instance': '2'.repeat(32), 'x-relay-name': 'Collision' }))).status, 409);
  f.device.chatId = '456';
  assert.equal((await upload(uploadRequest(data, data, {
    'x-relay-instance': '1'.repeat(32), 'x-relay-name': 'Office%20PC' }))).status, 409);
  assert.deepEqual((await f.bucket.file(path).download())[0], data);
});

test('date-indexed cleanup removes expired private images and records without an external database', async () => {
  const f = fixture(), data = await webp(), now = Date.now();
  await uploadHandler(() => f)(uploadRequest(data));
  const mediaKey = [...f.store.data.keys()].find(key => key.startsWith('media/'));
  const media = await f.records.get(mediaKey);
  await f.records.set(mediaKey, { ...media, expiresAt: now - 1 });
  await f.records.expire(mediaKey, now - 1, { path: media.path, parts: media.parts });
  await f.records.set('jobs/expired', { expiresAt: now - 1 }); await f.records.expire('jobs/expired', now - 1);
  await f.records.set('jobs/current', { expiresAt: now + 100000 }); await f.records.expire('jobs/current', now + 100000);
  assert.equal(await cleanupExpired(f.records, f.bucket, now), 2);
  assert.equal(f.imageStore.data.size, 0); assert.equal(await f.records.get(mediaKey), null);
  assert.ok(await f.records.get('jobs/current'));
});

test('scheduled retries send only due pending jobs and remove the pending index on success', async () => {
  const f = fixture(), now = Date.now();
  const normalized = validateEnvelope(envelope(), f.device);
  await enqueue(f.records, normalized, now - 1);
  const later = await enqueue(f.records, { ...normalized, id: 'b'.repeat(32) }, now + 60000);
  let sent = 0;
  const response = await scheduledHandler(() => f, async () => { sent++; return senderOk(); })();
  assert.equal((await response.json()).processed, 1); assert.equal(sent, 1);
  assert.equal((await f.records.get(jobKey(deviceId, id))).state, 'delivered');
  assert.equal((await f.records.get(later)).state, 'pending');
});
