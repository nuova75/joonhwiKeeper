import Fastify, { type FastifyInstance } from 'fastify';
import websocket from '@fastify/websocket';
import fastifyStatic from '@fastify/static';
import { fileURLToPath } from 'node:url';
import { ZodError } from 'zod';
import type { Config } from './config.js';
import { Repo } from './db.js';
import { createNotifier, type Notifier } from './notify/notifier.js';
import { Hub } from './ws/hub.js';
import { parentRoutes } from './routes/parent.js';
import { agentRoutes } from './routes/agent.js';

export interface App {
  app: FastifyInstance;
  repo: Repo;
  hub: Hub;
  notifier: Notifier;
}

export async function buildApp(config: Config, opts: { notifier?: Notifier; logger?: boolean } = {}): Promise<App> {
  // trustProxy: Cloudflare Tunnel 뒤에서 실제 클라이언트 IP(로그인 제한용)를 얻기 위해
  const app = Fastify({ logger: opts.logger ?? true, trustProxy: true });
  const repo = new Repo(config.dbPath);
  const notifier = opts.notifier ?? (await createNotifier(config, repo, app.log));
  const hub = new Hub(repo, notifier, app.log);

  app.setErrorHandler((err, req, reply) => {
    if (err instanceof ZodError) return reply.code(400).send({ error: 'validation', issues: err.issues });
    req.log.error(err);
    const status = (err as { statusCode?: number }).statusCode ?? 500;
    return reply.code(status).send({ error: status >= 500 ? 'internal' : (err as Error).message });
  });

  await app.register(websocket, { options: { maxPayload: 256 * 1024 } });
  // 부모 PWA 정적 파일 (같은 오리진에서 서빙 → CORS 불필요)
  await app.register(fastifyStatic, { root: fileURLToPath(new URL('../public', import.meta.url)) });

  app.get('/healthz', async () => ({ ok: true }));
  await app.register(parentRoutes, { repo, hub, config });
  await app.register(agentRoutes, { repo, hub, notifier });

  app.addHook('onClose', async () => repo.db.close());
  return { app, repo, hub, notifier };
}
