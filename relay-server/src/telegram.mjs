import sharp from 'sharp';

export async function callTelegram(job, bucket, fetcher = fetch) {
  const token = process.env.TELEGRAM_BOT_TOKEN;
  if (!token) throw new Error('Telegram server environment is incomplete');
  let body;
  if (job.files?.length) {
    body = new FormData();
    for (const [key, value] of Object.entries(job.fields)) {
      if (!job.files.some(file => file.field === key))
        body.set(key, typeof value === 'object' ? JSON.stringify(value) : String(value));
    }
    for (const image of job.files) {
      const file = bucket.file(image.path);
      const [metadata] = await file.getMetadata();
      if (Number(metadata.size) > 16 * 1024 * 1024 || metadata.contentType !== 'image/webp')
        return { status: 400, body: { ok: false, error_code: 400, description: 'Stored image exceeds limits' } };
      const [data] = await file.download();
      if (data.length < 12 || data.length > 16 * 1024 * 1024 || data.toString('ascii', 0, 4) !== 'RIFF' || data.toString('ascii', 8, 12) !== 'WEBP')
        return { status: 400, body: { ok: false, error_code: 400, description: 'Stored file is not a valid WebP image' } };
      if (job.method === 'sendPhoto') {
        // WebP is the stored original; a JPEG preview uses Telegram's photo interface.
        const preview = await sharp(data, { limitInputPixels: 33_554_432 }).rotate()
          .resize({ width: 3840, height: 2160, fit: 'inside', withoutEnlargement: true }).jpeg({ quality: 90 }).toBuffer();
        body.set(image.field, new Blob([preview], { type: 'image/jpeg' }), image.name.replace(/\.webp$/, '.jpg'));
      } else body.set(image.field, new Blob([data], { type: 'image/webp' }), image.name);
    }
  } else body = JSON.stringify(job.fields);
  const response = await fetcher(`https://api.telegram.org/bot${token}/${job.method}`, {
    method: 'POST', body, headers: typeof body === 'string' ? { 'Content-Type': 'application/json' } : undefined,
    signal: AbortSignal.timeout(15_000), redirect: 'error'
  });
  return { status: response.status, body: await response.json() };
}
