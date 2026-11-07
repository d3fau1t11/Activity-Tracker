import { getStore } from '@netlify/blobs';
import { createHash } from 'node:crypto';
import { HttpError } from './policy.mjs';

export function repository(suppliedStore) {
  let cached = suppliedStore;
  const store = () => cached ??= getStore({ name: 'activity-tracker-records', consistency: 'strong' });
  return {
    get: key => store().get(key, { type: 'json' }),
    set: (key, value) => store().setJSON(key, value),
    delete: key => store().delete(key),
    list: options => store().list(options),
    // Conditional writes fence concurrent sends without an external database.
    async change(key, decide) {
      for (let attempt = 0; attempt < 8; attempt++) {
        const old = await store().getWithMetadata(key, { type: 'json' });
        const decision = decide(old?.data);
        if (decision.value === undefined) return decision.result;
        if (old && !old.etag) throw new HttpError(503, 'Storage concurrency metadata unavailable');
        const written = await store().setJSON(key, decision.value, old ? { onlyIfMatch: old.etag } : { onlyIfNew: true });
        if (written.modified) return decision.result;
      }
      throw new HttpError(503, 'Storage busy; retry later');
    },
    async expire(key, expiresAt, image) {
      const day = new Date(expiresAt).toISOString().slice(0, 10);
      const id = createHash('sha256').update(key).digest('hex');
      await store().setJSON(`expires/${day}/${id}`, { key, expiresAt, ...(image ? { image } : {}) });
    }
  };
}
