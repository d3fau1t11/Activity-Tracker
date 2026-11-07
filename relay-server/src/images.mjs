import { createHash } from 'node:crypto';
import { getStore } from '@netlify/blobs';
import { HttpError } from './policy.mjs';

export const CHUNK_BYTES = 1024 * 1024;
export const MAX_IMAGE_BYTES = 16 * CHUNK_BYTES;
const pathPattern = /^devices\/[A-Za-z0-9_-]{3,64}\/uploads\/[a-f0-9]{32}\/(photo|document)\.webp$/;
const manifestKey = path => `${path}/manifest`;
const partKey = (path, part) => `${path}/part-${part}`;
const sha256 = data => createHash('sha256').update(data).digest('hex');

function validateManifest(path, manifest) {
  if (!pathPattern.test(path) || !manifest || !Number.isSafeInteger(manifest.bytes)
      || manifest.bytes < 12 || manifest.bytes > MAX_IMAGE_BYTES
      || manifest.parts !== Math.ceil(manifest.bytes / CHUNK_BYTES)
      || !/^[a-f0-9]{64}$/.test(manifest.sha256)) throw new HttpError(400, 'Invalid image manifest');
}

// No public image URL or arbitrary-key read endpoint. Store access remains on the server.
// Lazily resolve the store so local pairing scripts do not need Netlify credentials.
export function netlifyImages(suppliedStore) {
  let cachedStore = suppliedStore;
  const store = () => cachedStore ??= getStore({ name: 'activity-tracker-images', consistency: 'strong' });
  return {
    async putPart(path, manifest, part, data) {
      validateManifest(path, manifest);
      const expected = Math.min(CHUNK_BYTES, manifest.bytes - part * CHUNK_BYTES);
      if (!Number.isSafeInteger(part) || part < 0 || part >= manifest.parts || data.length !== expected)
        throw new HttpError(400, 'Invalid image chunk');
      const written = await store().setJSON(manifestKey(path), manifest, { onlyIfNew: true });
      if (!written.modified) {
        const old = await store().get(manifestKey(path), { type: 'json' });
        if (!old || old.bytes !== manifest.bytes || old.parts !== manifest.parts || old.sha256 !== manifest.sha256)
          throw new HttpError(409, 'Image ID already has different content');
      }
      const chunk = await store().set(partKey(path, part), data, { onlyIfNew: true });
      if (!chunk.modified) {
        const old = await store().get(partKey(path, part), { type: 'arrayBuffer' });
        if (!old || !Buffer.from(old).equals(data)) throw new HttpError(409, 'Image chunk already has different content');
      }
    },
    async remove(path, parts) {
      if (!pathPattern.test(path) || !Number.isSafeInteger(parts) || parts < 1 || parts > 16)
        throw new HttpError(400, 'Invalid image cleanup reference');
      await Promise.all(Array.from({ length: parts }, (_, part) => store().delete(partKey(path, part))));
      await store().delete(manifestKey(path));
    },
    file(path) {
      if (!pathPattern.test(path)) throw new HttpError(400, 'Invalid private image reference');
      const readManifest = async () => {
        const value = await store().get(manifestKey(path), { type: 'json' });
        if (!value) throw new HttpError(404, 'Image upload missing');
        validateManifest(path, value);
        return value;
      };
      return {
        async getMetadata() {
          const value = await readManifest();
          return [{ size: String(value.bytes), contentType: 'image/webp' }];
        },
        async download() {
          const value = await readManifest();
          const chunks = await Promise.all(Array.from({ length: value.parts }, async (_, part) => {
            const data = await store().get(partKey(path, part), { type: 'arrayBuffer' });
            if (!data || data.byteLength !== Math.min(CHUNK_BYTES, value.bytes - part * CHUNK_BYTES))
              throw new HttpError(503, 'Image upload incomplete');
            return Buffer.from(data);
          }));
          const data = Buffer.concat(chunks);
          if (data.length !== value.bytes || sha256(data) !== value.sha256)
            throw new HttpError(409, 'Stored image integrity check failed');
          return [data];
        }
      };
    }
  };
}
