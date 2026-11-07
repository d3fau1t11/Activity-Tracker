import { services } from '../../src/services.mjs';
import { deliver } from '../../src/delivery.mjs';
import { cleanupExpired } from '../../src/cleanup.mjs';

export function createHandler(getServices = services, sender) {
return async function handler() {
  const started = Date.now();
  const { records, bucket, device } = getServices();
  let removed = 0, processed = 0, scanned = 0;
  try { removed = await cleanupExpired(records, bucket, started, started + 6000); }
  catch { console.warn('Expiry cleanup deferred; delivery retries will continue.'); }
  for await (const page of records.list({ prefix: 'pending/', paginate: true })) {
    for (const blob of page.blobs) {
      if (Date.now() - started > 10_000 || processed >= 20 || scanned++ >= 100)
        return Response.json({ processed, removed });
      const key = blob.key.replace(/^pending\//, 'jobs/');
      const job = await records.get(key);
      if (!job || job.state !== 'pending') { await records.delete(blob.key); continue; }
      if (job.nextAttempt > Date.now() || job.leaseUntil > Date.now()) continue;
      await deliver(records, bucket, key, device, Date.now(), sender);
      processed++;
    }
  }
  return Response.json({ processed, removed });
};
}
export default createHandler();
