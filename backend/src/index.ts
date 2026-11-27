import { config } from './config.js';
import { buildApp } from './app.js';
import { startWatchdog } from './watchdog.js';

const { app, repo, hub, notifier } = await buildApp(config);
const watchdog = startWatchdog({ repo, hub, notifier, config });

const shutdown = async () => {
  clearInterval(watchdog);
  await app.close();
  process.exit(0);
};
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);

await app.listen({ port: config.port, host: config.host });
