const { spawn } = require('node:child_process');
const https = require('node:https');
const net = require('node:net');
const path = require('node:path');

const webUrl = 'https://localhost:8443/';
const expoPort = 8081;
const proxyPort = 8443;
const projectRoot = path.resolve(__dirname, '..');
let expo;
let proxy;
let stopping = false;

function stop(code) {
  if (stopping) return;
  stopping = true;
  expo?.kill();
  proxy?.kill();
  process.exitCode = code;
}

process.on('SIGINT', () => stop(130));
process.on('SIGTERM', () => stop(143));

function isListening(port) {
  return new Promise((resolve) => {
    const socket = net.connect({ host: '127.0.0.1', port });
    socket.once('connect', () => { socket.destroy(); resolve(true); });
    socket.once('error', () => resolve(false));
    socket.setTimeout(1000, () => { socket.destroy(); resolve(false); });
  });
}

function isWebReady() {
  return new Promise((resolve) => {
    const request = https.get(webUrl, { rejectUnauthorized: false, timeout: 2000 }, (response) => {
      response.resume();
      resolve(response.statusCode < 500);
    });
    request.once('error', () => resolve(false));
    request.once('timeout', () => { request.destroy(); resolve(false); });
  });
}

async function waitFor(check, child, label) {
  for (let attempt = 0; attempt < 120 && !stopping; attempt++) {
    if (await check()) return;
    if (child.exitCode !== null) throw new Error(`${label} exited before it was ready.`);
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`Timed out waiting for ${label}.`);
}

async function main() {
  if (await isListening(expoPort)) {
    throw new Error(`Port ${expoPort} is already in use. Stop the existing Expo server and run npm run web again.`);
  }

  if (!(await isListening(proxyPort))) {
    proxy = spawn('caddy', ['run', '--config', './Caddyfile.dev'], {
      cwd: projectRoot,
      stdio: 'inherit',
    });
    proxy.once('error', (error) => {
      console.error(`Could not start Caddy: ${error.message}`);
      stop(1);
    });
    proxy.once('exit', (code) => stop(code || 1));
    await waitFor(() => isListening(proxyPort), proxy, 'Caddy');
  } else {
    console.log('Using the HTTPS proxy already listening on port 8443.');
  }

  expo = spawn(process.execPath, [require.resolve('expo/bin/cli'), 'start', '--web', '--port', String(expoPort)], {
    cwd: projectRoot,
    env: { ...process.env, BROWSER: 'none' },
    stdio: 'inherit',
  });
  expo.once('error', (error) => {
    console.error(`Could not start Expo: ${error.message}`);
    stop(1);
  });
  expo.once('exit', (code) => stop(code || 0));

  await waitFor(isWebReady, expo, 'Expo Web through the HTTPS proxy');
  console.log(`\nWeb browser: ${webUrl} (Metro listens internally on http://localhost:${expoPort})\n`);
  if (process.platform === 'darwin' && process.stdout.isTTY) {
    spawn('open', [webUrl], { stdio: 'ignore' }).once('error', (error) => {
      console.error(`Could not open the browser: ${error.message}`);
    });
  }
}

main().catch((error) => {
  console.error(error.message);
  stop(1);
});
