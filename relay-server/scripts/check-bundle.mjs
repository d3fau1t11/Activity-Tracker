import { zipFunctions } from '@netlify/zip-it-and-ship-it';
import { readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const destination = path.join(root, '.netlify', 'bundle-check');
await mkdir(destination, { recursive: true });
const results = await zipFunctions(path.join(root, 'netlify', 'functions'), destination, {
  basePath: root, config: {
    '*': { nodeBundler: 'nft', nodeVersion: '22', externalNodeModules: ['sharp'] },
    'retry-deliveries': { schedule: '*/5 * * * *' }
  }, manifest: path.join(destination, 'manifest.json'), parallelLimit: 2
});
const names = results.map(item => item.name).sort();
if (JSON.stringify(names) !== JSON.stringify(['retry-deliveries', 'telegram', 'telegram-webhook', 'upload-image']))
  throw new Error('Expected all four relay functions to be bundled');
for (const item of results) {
  if (item.bundler !== 'nft' || item.bundlerErrors?.length) throw new Error(`Bundling failed: ${item.name}`);
  if (item.size > 50 * 1024 * 1024) throw new Error(`Function archive exceeds 50 MiB: ${item.name}`);
  console.log(`${item.name}: ${item.bundler} archive ${(item.size / 1024 / 1024).toFixed(1)} MiB`);
}
const manifest = JSON.parse(await readFile(path.join(destination, 'manifest.json'), 'utf8'));
if (manifest.functions.length !== 4) throw new Error('Function manifest is incomplete');
console.log('PASS: all relay functions packaged. Native image dependencies must be installed on the deployment platform.');
