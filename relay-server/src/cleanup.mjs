import { pendingKey } from './delivery.mjs';

// Expiry indexes are grouped by date; no paid database or TTL service.
export async function cleanupExpired(records, bucket, now = Date.now(), deadline = now + 6000) {
  let removed = 0;
  const today = new Date(now).toISOString().slice(0, 10);
  const root = await records.list({ prefix: 'expires/', directories: true });
  for (const directory of root.directories.sort()) {
    if (directory.slice(8, 18) > today) continue;
    for await (const page of records.list({ prefix: directory, paginate: true })) {
      for (const blob of page.blobs) {
        if (Date.now() >= deadline || removed >= 25) return removed;
        const entry = await records.get(blob.key);
        if (!entry || entry.expiresAt > now) continue;
        if (entry.image) await bucket.remove(entry.image.path, entry.image.parts);
        await records.delete(entry.key);
        if (entry.key.startsWith('jobs/')) await records.delete(pendingKey(entry.key));
        await records.delete(blob.key);
        removed++;
      }
    }
  }
  return removed;
}
