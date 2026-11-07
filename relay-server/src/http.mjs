import { HttpError } from './policy.mjs';

export function json(body, status = 200, stored = false) {
  return new Response(JSON.stringify(body), { status, headers: {
    'Content-Type': 'application/json', 'Cache-Control': 'no-store', ...(stored ? { 'X-Relay-Stored': 'true' } : {})
  } });
}
export async function readBody(request) {
  if (request.method !== 'POST') throw new HttpError(405, 'POST required');
  const limit = 128 * 1024;
  const declared = request.headers.get('content-length');
  if (declared && /^\d+$/.test(declared) && Number(declared) > limit) throw new HttpError(413, 'Request exceeds limit');
  if (!request.body) throw new HttpError(400, 'JSON body required');
  const reader = request.body.getReader();
  const chunks = []; let bytes = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      bytes += value.byteLength;
      if (bytes > limit) { await reader.cancel(); throw new HttpError(413, 'Request exceeds limit'); }
      chunks.push(Buffer.from(value));
    }
  } finally { reader.releaseLock(); }
  const text = Buffer.concat(chunks, bytes).toString('utf8');
  try { return JSON.parse(text); } catch { throw new HttpError(400, 'Invalid JSON'); }
}
export function errorResponse(error) {
  const status = error instanceof HttpError ? error.status : 503;
  // Do not log upstream URLs, tokens, account keys, command bodies, or image data.
  return json({ ok: false, error_code: status, description: error instanceof HttpError ? error.message : 'Relay temporarily unavailable' }, status);
}
