import test from 'node:test';
import assert from 'node:assert/strict';
import { createHandler as telegramHandler } from '../netlify/functions/telegram.mjs';
import { createHandler as webhookHandler } from '../netlify/functions/telegram-webhook.mjs';
import { createHandler as scheduledHandler } from '../netlify/functions/retry-deliveries.mjs';
import { repository } from '../src/store.mjs';
import { registerDevice, registryKey, selector, pollCommands, handleWebhook } from '../src/routing.mjs';
import { webhookSecret, services } from '../src/services.mjs';
import { enqueue, jobKey } from '../src/delivery.mjs';
import { FakeBlobs } from './fake-blobs.mjs';
import { cleanupExpired } from '../src/cleanup.mjs';

const token = '123:fixture-only-token';
const account = { botId: '123', chatId: '123', enabled: true };
const instance = id => (id === 'pc-one' ? '1' : '2').repeat(32);
const message = (id, text, extra = {}) => ({ update_id: id, message: { message_id: id,
  from: { id: 123, first_name: 'Owner', is_bot: false }, chat: { id: 123, type: 'private' },
  date: Math.floor(Date.now() / 1000), text, ...extra } });
const callback = (id, data, extra = {}) => ({ update_id: id, callback_query: { id: String(id), data,
  from: { id: 123, first_name: 'Owner', is_bot: false }, message: { message_id: 50, chat: { id: 123, type: 'private' } }, ...extra } });
const request = (id, offset = 0, extra = {}) => new Request('https://fixture.test/api/telegram', {
  method: 'POST', headers: { authorization: `Bearer ${token}` }, body: JSON.stringify({
    deviceId: id, deviceName: id, instanceId: instance(id), id: 'a'.repeat(32), method: 'getUpdates', fields: { offset }, ...extra }) });
const webhook = (update, secret = webhookSecret(token)) => new Request('https://fixture.test/api/telegram-webhook', {
  method: 'POST', headers: { 'x-telegram-bot-api-secret-token': secret }, body: JSON.stringify(update) });

function fixture() {
  const records = repository(new FakeBlobs()), sent = [];
  const service = { token, sharedBot: true, device: account, records, bucket: {} };
  const sender = async job => { sent.push(job); return { status: 200, body: { ok: true, result: true } }; };
  return { ...service, sent, sender, api: telegramHandler(() => service, sender), hook: webhookHandler(() => service, sender) };
}
async function poll(f, id, offset = 0) {
  const response = await f.api(request(id, offset));
  assert.equal(response.status, 200);
  return (await response.json()).result;
}
async function registerBoth(f) {
  await Promise.all([poll(f, 'pc-one'), poll(f, 'pc-two')]);
}

test('preset bot/chat configuration supports shared routing without RELAY_DEVICE_ID', () => {
  process.env.TELEGRAM_BOT_TOKEN = token; process.env.TELEGRAM_CHAT_ID = '123';
  delete process.env.RELAY_DEVICE_ID; delete process.env.RELAY_MULTI_DEVICE;
  assert.equal(services().sharedBot, true);
  assert.equal(services().device.chatId, '123');
});

test('two PCs register independently and duplicate explicit IDs fail instead of combining queues', async () => {
  const f = fixture(); await registerBoth(f);
  assert.equal(Object.keys((await f.records.get(registryKey(account))).devices).length, 2);
  assert.equal(f.sent.length, 0, 'PC polling never calls Telegram getUpdates');
  assert.equal((await f.api(request('pc-one', 0, { instanceId: '2'.repeat(32) }))).status, 409);
  assert.equal((await f.api(request('pc-three', 0, { instanceId: 'bad' }))).status, 400);
  const unauthorized = request('pc-one'); unauthorized.headers.set('authorization', 'Bearer wrong');
  assert.equal((await f.api(unauthorized)).status, 401); assert.equal(unauthorized.bodyUsed, false);
});

test('the webhook requires its derived secret and configured private actor before storing commands', async () => {
  const f = fixture(); await registerBoth(f);
  const unauthorized = webhook(message(1, '/screenshot'), token);
  assert.equal((await f.hook(unauthorized)).status, 401); assert.equal(unauthorized.bodyUsed, false);
  assert.equal((await f.hook(webhook(message(2, '/screenshot'), 'é'.repeat(64)))).status, 401);
  for (const update of [message(3, '/screenshot', { from: { id: 456 } }),
    message(4, '/screenshot', { chat: { id: 123, type: 'group' } }), message(5, '/screenshot', { date: 1 }),
    callback(6, `d:${selector('pc-one')}:cmd:screenshot`, { from: { id: 456 } })])
    assert.equal((await f.hook(webhook(update))).status, 200);
  assert.deepEqual(await poll(f, 'pc-one'), []); assert.equal(f.sent.length, 0);
  assert.equal((await f.hook(webhook(null))).status, 400);
});

test('commands require explicit selection and each PC only receives its own queue', async () => {
  const f = fixture(); await registerBoth(f);
  await f.hook(webhook(message(10, '/screenshot')));
  assert.equal(f.sent.at(-1).fields.reply_markup.inline_keyboard.length, 2);
  assert.deepEqual(await poll(f, 'pc-one'), []); assert.deepEqual(await poll(f, 'pc-two'), []);
  await f.hook(webhook(message(11, '/select pc-one')));
  assert.equal((await poll(f, 'pc-one'))[0].message.text, '/menu');
  await poll(f, 'pc-one', 2);
  await f.hook(webhook(message(12, '/screenshot')));
  const commands = await poll(f, 'pc-one');
  assert.equal(commands.length, 1); assert.equal(commands[0].message.text, '/screenshot');
  assert.deepEqual(await poll(f, 'pc-two'), []);
  await poll(f, 'pc-one', commands[0].update_id + 1);
  assert.deepEqual(await poll(f, 'pc-one', 0), [], 'Restart cannot replay a command already acknowledged');
  assert.equal((await f.api(request('pc-one', 2147483647))).status, 400);
});

test('device picker opens the chosen menu and older menu buttons stay tied to their original PC', async () => {
  const f = fixture(); await registerBoth(f);
  await f.hook(webhook(callback(20, `pc:${selector('pc-two')}`)));
  assert.equal((await poll(f, 'pc-two'))[0].message.text, '/menu');
  await f.hook(webhook(callback(21, `d:${selector('pc-one')}:cmd:screenshot`)));
  assert.equal((await poll(f, 'pc-one'))[0].callback_query.data, 'cmd:screenshot');
  await f.hook(webhook(callback(22, 'cmd:screenshot')));
  assert.match(f.sent.at(-1).fields.text, /fresh/);
  await f.hook(webhook(callback(23, 'pc:list')));
  assert.match(f.sent.at(-1).fields.text, /Choose a PC/);
});

test('a webhook replay keeps its original target and acknowledged work is never recreated', async () => {
  const f = fixture(); await registerBoth(f);
  await f.hook(webhook(message(30, '/select pc-one'))); await poll(f, 'pc-one', 2);
  const update = message(31, '/stop');
  await f.hook(webhook(update));
  const first = await poll(f, 'pc-one'); await poll(f, 'pc-one', first[0].update_id + 1);
  await f.hook(webhook(message(32, '/select pc-two'))); await poll(f, 'pc-two', 2);
  await f.records.change('webhooks/123_123/31', old => ({ value: { ...old, done: false }, result: true }));
  await f.hook(webhook(update));
  assert.deepEqual(await poll(f, 'pc-one'), []); assert.deepEqual(await poll(f, 'pc-two'), []);
});

test('new commands and selections continue after Telegram resets its source update IDs', async () => {
  const f = fixture(); await registerBoth(f);
  await f.hook(webhook(message(100000, '/select pc-one'))); await poll(f, 'pc-one', 2);
  await f.hook(webhook(message(1, '/select pc-two')));
  assert.equal((await f.records.get(registryKey(account))).selected, 'pc-two');
  assert.equal((await poll(f, 'pc-two'))[0].update_id, 1);
  await f.hook(webhook(message(2, '/status')));
  assert.equal((await poll(f, 'pc-two'))[1].update_id, 2);
});

test('expired commands are dropped and concurrent webhook requests enqueue only once', async () => {
  const f = fixture(); await registerBoth(f);
  await f.hook(webhook(message(40, '/select pc-one'))); await poll(f, 'pc-one', 2);
  const update = message(41, '/status');
  const responses = await Promise.all([f.hook(webhook(update)), f.hook(webhook(update))]);
  assert.ok(responses.every(r => [200, 503].includes(r.status)));
  assert.equal((await poll(f, 'pc-one')).length, 1);
  assert.deepEqual(await pollCommands(f.records, { ...account, deviceId: 'pc-one' }, 0, Date.now() + 121000), []);
});

test('setup installs one authenticated webhook and scheduled deliveries resolve each job device', async () => {
  const f = fixture();
  const response = await f.api(request('setup-client', 0, { method: 'configureRelay', fields: {} }));
  assert.equal(response.status, 200);
  const configured = f.sent.at(-1);
  assert.equal(configured.method, 'setWebhook'); assert.equal(configured.fields.url, 'https://fixture.test/api/telegram-webhook');
  assert.equal(configured.fields.secret_token, webhookSecret(token)); assert.equal(configured.fields.max_connections, 1);
  assert.equal(await f.records.get(registryKey(account)), null, 'Setup is not registered as a PC');
  for (const [index, deviceId] of ['pc-one', 'pc-two'].entries()) await enqueue(f.records, {
    deviceId, id: String(index + 1).repeat(32), botId: '123', method: 'sendMessage', fields: { chat_id: '123', text: 'Fixture' }, files: []
  });
  const scheduled = scheduledHandler(() => f, f.sender);
  assert.equal((await (await scheduled()).json()).processed, 2);
  for (const [index, deviceId] of ['pc-one', 'pc-two'].entries())
    assert.equal((await f.records.get(jobKey(deviceId, String(index + 1).repeat(32)))).state, 'delivered');
});

test('a failed acknowledgement can retry promptly while retaining one immutable command', async () => {
  const f = fixture(); await registerBoth(f);
  const update = callback(50, `d:${selector('pc-one')}:cmd:stop`);
  await assert.rejects(() => handleWebhook(f.records, f.bucket, account, update,
    async () => ({ status: 503, body: { ok: false } })), /deferred/);
  await handleWebhook(f.records, f.bucket, account, update, f.sender);
  assert.equal((await poll(f, 'pc-one')).length, 1);
});

test('renewed device records survive an older expiry index and reused source update IDs remain valid', async () => {
  const f = fixture(), now = Date.now(); await registerBoth(f);
  await f.records.expire(registryKey(account), now - 1);
  await cleanupExpired(f.records, f.bucket, now);
  assert.equal(Object.keys((await f.records.get(registryKey(account))).devices).length, 2);
  await f.hook(webhook(message(60, '/select pc-one'))); await poll(f, 'pc-one', 2);
  await f.hook(webhook(message(61, '/status'))); await poll(f, 'pc-one', 3);
  await f.records.change('webhooks/123_123/61', old => ({ value: { ...old, expiresAt: now - 1 }, result: true }));
  await f.records.expire('webhooks/123_123/61', now - 1);
  await f.hook(webhook(message(61, '/stop')));
  await cleanupExpired(f.records, f.bucket, now);
  assert.equal((await poll(f, 'pc-one'))[0].message.text, '/stop');
  assert.equal((await f.records.get('webhooks/123_123/61')).done, true);
});

test('source age limits text expiry and callback execution age reflects the click rather than the old menu', async () => {
  const f = fixture(), now = Date.now(); await registerBoth(f);
  await f.hook(webhook(message(70, '/select pc-one'))); await poll(f, 'pc-one', 2);
  await handleWebhook(f.records, f.bucket, account, message(71, '/stop', { date: Math.floor((now - 110000) / 1000) }), f.sender, now);
  assert.deepEqual(await pollCommands(f.records, { ...account, deviceId: 'pc-one' }, 0, now + 15000), []);
  const oldMenu = callback(72, `d:${selector('pc-one')}:cmd:screenshot`);
  oldMenu.callback_query.message.date = 1;
  oldMenu.callback_query.message.text = 'Sensitive clipboard preview fixture';
  await handleWebhook(f.records, f.bucket, account, oldMenu, f.sender, now);
  const queued = await poll(f, 'pc-one');
  assert.equal(queued[0].callback_query.message.date, Math.floor(now / 1000));
  assert.equal(queued[0].callback_query.message.text, undefined);
  assert.equal((await f.records.get('webhooks/123_123/72')).update.callback_query.message.text, undefined);
});
