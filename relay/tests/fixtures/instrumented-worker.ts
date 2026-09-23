// Test-only entrypoint. Production wrangler.jsonc bundles src/index.ts instead.
import worker, { type Env } from '../../src/index';
import { QuotaGroup as ProductionGroup } from '../../src/quota-group';
export class QuotaGroup extends ProductionGroup {
  private readonly instanceId = crypto.randomUUID();
  override async fetch(request: Request): Promise<Response> {
    if (new URL(request.url).pathname === '/__test/age-reset') {
      const state: any = await this.ctx.storage.get('group');
      if (state?.resetCandidate) { state.resetCandidate.firstSeen -= 61_000; await this.ctx.storage.put('group', state); }
      return Response.json({ aged: true });
    }
    if (new URL(request.url).pathname === '/__test/inspect') {
      return Response.json({ instanceId: this.instanceId, stored: await this.ctx.storage.get('group'),
        attachments: this.ctx.getWebSockets().map(socket => socket.deserializeAttachment()) });
    }
    return super.fetch(request);
  }
}
export default {
  async fetch(request: Request, env: Env) {
    const age = /^\/__test\/age-reset\/([a-f0-9-]+)$/.exec(new URL(request.url).pathname);
    if (age) return env.QUOTA_GROUPS.get(env.QUOTA_GROUPS.idFromName(age[1])).fetch('http://localhost/__test/age-reset');
    const match = /^\/__test\/inspect\/([a-f0-9-]+)$/.exec(new URL(request.url).pathname);
    if (match) return env.QUOTA_GROUPS.get(env.QUOTA_GROUPS.idFromName(match[1])).fetch('http://localhost/__test/inspect');
    return worker.fetch(request, env);
  }
};
