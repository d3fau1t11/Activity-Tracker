export class FakeBlobs {
  data = new Map(); revision = 0;
  async set(key, value, options = {}) {
    const old = this.data.get(key);
    if ((options.onlyIfNew && old) || (options.onlyIfMatch && old?.etag !== options.onlyIfMatch)) return { modified: false };
    const etag = String(++this.revision);
    this.data.set(key, { data: Buffer.from(value), etag });
    return { modified: true, etag };
  }
  setJSON(key, value, options) { return this.set(key, JSON.stringify(value), options); }
  async get(key, options = {}) {
    const value = this.data.get(key);
    if (!value) return null;
    return options.type === 'json' ? JSON.parse(value.data.toString()) : Uint8Array.from(value.data).buffer;
  }
  async getWithMetadata(key, options) {
    const old = this.data.get(key);
    if (!old) return null;
    return { data: await this.get(key, options), etag: old.etag, metadata: {} };
  }
  async delete(key) { this.data.delete(key); }
  list(options = {}) {
    const prefix = options.prefix || '', directories = new Set(), blobs = [];
    for (const [key, value] of this.data) {
      if (!key.startsWith(prefix)) continue;
      const suffix = key.slice(prefix.length);
      if (options.directories && suffix.includes('/')) directories.add(prefix + suffix.split('/')[0] + '/');
      else blobs.push({ key, etag: value.etag });
    }
    const page = { blobs, directories: [...directories] };
    return options.paginate ? (async function* () { yield page; })() : Promise.resolve(page);
  }
}
