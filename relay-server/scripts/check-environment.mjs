const names = ['TELEGRAM_BOT_TOKEN', 'TELEGRAM_CHAT_ID', 'RELAY_DEVICE_ID'];
let failed = false;
for (const name of names) {
  const present = Boolean(process.env[name]);
  console.log(`${name}: ${present ? 'configured' : 'missing'}`);
  if (!present) failed = true;
}
process.exitCode = failed ? 1 : 0;
