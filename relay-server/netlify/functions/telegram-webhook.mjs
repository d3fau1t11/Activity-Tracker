import { services, authenticateWebhook } from '../../src/services.mjs';
import { readBody, json, errorResponse } from '../../src/http.mjs';
import { handleWebhook } from '../../src/routing.mjs';
import { callTelegram } from '../../src/telegram.mjs';

export function createHandler(getServices = services, sender = callTelegram) {
  return async request => {
    try {
      const service = getServices();
      authenticateWebhook(request, service);
      const update = await readBody(request);
      await handleWebhook(service.records, service.bucket, service.device, update, sender);
      return json({ ok: true });
    } catch (error) { return errorResponse(error); }
  };
}
export default createHandler();
