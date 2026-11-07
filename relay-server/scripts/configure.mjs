import { validDeviceId } from '../src/policy.mjs';
const required = ['RELAY_SITE_URL', 'TELEGRAM_BOT_TOKEN', 'TELEGRAM_CHAT_ID'];
for (const key of required) if (!process.env[key]) throw new Error(`Missing ${key}`);
const deviceId = process.env.RELAY_DEVICE_ID || 'setup-client';
if (!validDeviceId(deviceId)) throw new Error('Use a valid device ID');
const site = new URL(process.env.RELAY_SITE_URL);
if (site.protocol !== 'https:' || site.pathname !== '/' || site.search || site.hash || site.username || site.password)
  throw new Error('RELAY_SITE_URL must be an HTTPS origin');
const response = await fetch(new URL('/api/telegram', site), {
  method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${process.env.TELEGRAM_BOT_TOKEN}` },
  signal: AbortSignal.timeout(25_000), redirect: 'error',
  body: JSON.stringify({ deviceId, id: '0'.repeat(32), method: 'configureRelay', fields: {}, files: [] })
});
const result = await response.json();
if (!result.ok) throw new Error(result.description || 'Netlify setup failed; verify the deployed token, device, and chat settings');
console.log('Telegram routing configured through Netlify. Start the Windows apps and use /devices to choose a PC.');
