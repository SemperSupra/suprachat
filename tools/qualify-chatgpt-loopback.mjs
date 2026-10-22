#!/usr/bin/env node
// Offline, no-container read-only integration oracle.
// Public CI uses --mode mock. A *local* authorized actor may use --mode private
// against an EXACT pinned private source checkout. Never sends ChatGPT prompts.
import assert from 'node:assert/strict';
import {randomBytes} from 'node:crypto';
import {spawn, execFileSync} from 'node:child_process';
import {createServer} from 'node:http';
import {fileURLToPath, pathToFileURL} from 'node:url';
import fs from 'node:fs/promises';
import path from 'node:path';

const TARGET = 'https://chatgpt.com/c/123e4567-e89b-42d3-a456-426614174000';
const ONE_MINUTE = 60_000;
const scriptRoot = path.dirname(fileURLToPath(import.meta.url));

function argsToObject(args) {
  const options = {};
  for (let i = 0; i < args.length; i += 2) {
    const key = args[i];
    if (!['--mode', '--public-root', '--private-root', '--private-sha', '--dotnet'].includes(key)
        || !args[i + 1]) throw new Error('INVALID_BATCH_ARGUMENT');
    options[key.slice(2)] = args[i + 1];
  }
  if (!['mock', 'private'].includes(options.mode)) throw new Error('INVALID_BATCH_MODE');
  if (options.mode === 'private' && (!options['private-root'] || !options['private-sha'])) {
    throw new Error('PINNED_PRIVATE_SOURCE_REQUIRED');
  }
  return options;
}

async function childOutput(program, args, environment, input) {
  return await new Promise((resolve, reject) => {
    const child = spawn(program, args, {env: environment, shell: false, stdio: ['pipe', 'pipe', 'pipe']});
    let stdout = '';
    let stderr = '';
    const guard = setTimeout(() => child.kill(), ONE_MINUTE);
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', chunk => {
      stdout += chunk;
      if (stdout.length > 128 * 1024) child.kill();
    });
    child.stderr.on('data', chunk => {
      stderr += chunk;
      if (stderr.length > 32 * 1024) child.kill();
    });
    child.on('error', () => {clearTimeout(guard); reject(new Error('CHILD_PROCESS_UNAVAILABLE'));});
    child.on('close', code => {
      clearTimeout(guard);
      if (code !== 0) return reject(new Error('AUTOMATION_CLI_EXECUTION_FAILED'));
      resolve(stdout);
    });
    child.stdin.end(input ?? '');
  });
}

function unpackCli(stdout) {
  const data = JSON.parse(stdout.trim());
  assert.equal(data.ok, true);
  assert.equal(data.value?.schema, 'suprachat-chatgpt-relay-observation/v1');
  assert.equal(data.value?.may_submit, false);
  return data.value;
}

function unpackRpc(stdout) {
  const rows = stdout.trim().split(/\r?\n/).filter(Boolean);
  assert.equal(rows.length, 1, 'JSON-RPC stdio must return exactly one response line');
  const data = JSON.parse(rows[0]);
  assert.equal(data.jsonrpc, '2.0');
  assert.equal(data.id, 1);
  assert.equal(data.result?.schema, 'suprachat-chatgpt-relay-observation/v1');
  assert.equal(data.result?.may_submit, false);
  return data.result;
}

const options = argsToObject(process.argv.slice(2));
const publicRoot = path.resolve(options['public-root'] || path.join(scriptRoot, '..'));
const project = path.join(publicRoot, 'SupraChat.Automation', 'SupraChat.Automation.csproj');
await fs.access(project);
const dotnet = options.dotnet || 'dotnet';
const token = randomBytes(32).toString('base64url');
let relay;
let port;
let ready = false;
let privateSha = null;

if (options.mode === 'private') {
  const privateRoot = await fs.realpath(options['private-root']);
  privateSha = execFileSync('git', ['-C', privateRoot, 'rev-parse', 'HEAD'],
    {encoding: 'utf8', timeout: 20_000}).trim();
  if (!/^[0-9a-f]{40}$/.test(options['private-sha']) ||
      privateSha !== options['private-sha']) throw new Error('PRIVATE_SOURCE_PIN_MISMATCH');
  const source = path.join(privateRoot, 'tools', 'chatgpt-local-bridge.mjs');
  await fs.access(source);
  const bridge = await import(pathToFileURL(source).href);
  relay = bridge.createRelayServer({
    port: 0, token,
    logger: {error() {}},
    ackTimeoutMs: 500,
    longPollTimeoutMs: 500,
    requestWaitTimeoutMs: 500,
  });
  port = (await relay.listen()).port;
} else {
  // Public synthetic relay: same status schema but no private code.
  relay = createServer((req, res) => {
    if (req.method !== 'GET' || req.url !== '/state' ||
        req.headers.authorization !== 'Bearer ' + token) {
      res.writeHead(401); res.end(); return;
    }
    res.writeHead(200, {'content-type': 'application/json', 'cache-control': 'no-store'});
    res.end(JSON.stringify({
      clients: ready ? [{
        conversationUrl: TARGET, browserState: 'ready', busy: false,
      }] : [],
      queueDepth: 0,
      quarantineCounts: {clients: 0, conversations: 0},
      requests: [{id: 'must-not-escape', prompt: 'must-not-escape'}],
    }));
  });
  await new Promise((resolve, reject) =>
    relay.once('error', reject).listen(0, '127.0.0.1', resolve));
  port = relay.address().port;
}

const cliArgs = (...rest) => [
  'run', '--project', project, '--configuration', 'Release', '--no-build',
  '--', ...rest,
];
const env = {...process.env, SUPRACHAT_CHATGPT_RELAY_TOKEN: token};
const result = {
  schema: 'suprachat-readonly-local-relay-batch/v1',
  mode: options.mode,
  private_source_sha: privateSha,
  actual_chatgpt_network_access: false,
  posted_chatgpt_prompt: false,
  used_browser_profiles: false,
  leaked_token: false,
  stages: [],
};

try {
  const initial = await childOutput(dotnet,
    cliArgs('chatgpt-bridge-observe', '--url', TARGET, '--port', String(port)), env);
  assert(!initial.includes(token) && !initial.includes('must-not-escape'));
  const before = unpackCli(initial);
  assert.equal(before.connected, true);
  assert.equal(before.matching_ready_clients, 0);
  result.stages.push({name: 'loopback-no-client', state: 'PASS'});

  if (options.mode === 'private') {
    const heartbeat = await fetch('http://127.0.0.1:' + port + '/client-state', {
      method: 'POST',
      headers: {
        authorization: 'Bearer ' + token,
        origin: 'https://chatgpt.com',
        'content-type': 'application/json',
      },
      body: JSON.stringify({
        clientId: 'synthetic-readonly-client',
        browserState: 'ready', visible: true, focused: true,
        conversationUrl: TARGET,
      }),
    });
    assert.equal(heartbeat.status, 200);
  } else {
    ready = true;
  }

  const checked = await childOutput(dotnet,
    cliArgs('chatgpt-bridge-observe', '--url', TARGET, '--port', String(port)), env);
  assert(!checked.includes(token) && !checked.includes('must-not-escape'));
  const observation = unpackCli(checked);
  assert.equal(observation.connected, true);
  assert.equal(observation.matching_ready_clients, 1);
  assert.equal(observation.state, 'RELAY_REPORTS_READY_UNVERIFIED');
  result.stages.push({name: 'loopback-synthetic-ready', state: 'PASS'});

  const rpcInput = JSON.stringify({
    jsonrpc: '2.0', id: 1, method: 'chatgpt/bridge/observe',
    params: {url: TARGET, port},
  }) + '\n';
  const rpcOut = await childOutput(dotnet, cliArgs('stdio'), env, rpcInput);
  assert(!rpcOut.includes(token) && !rpcOut.includes('must-not-escape'));
  const rpc = unpackRpc(rpcOut);
  assert.equal(rpc.connected, true);
  assert.equal(rpc.matching_ready_clients, observation.matching_ready_clients);
  assert.equal(rpc.canonical_conversation_url, observation.canonical_conversation_url);
  result.stages.push({name: 'loopback-cli-agent-parity', state: 'PASS'});

  const withoutToken = await childOutput(dotnet,
    cliArgs('chatgpt-bridge-observe', '--url', TARGET, '--port', String(port)),
    {...process.env, SUPRACHAT_CHATGPT_RELAY_TOKEN: ''});
  const denied = unpackCli(withoutToken);
  assert.equal(denied.connected, false);
  assert.equal(denied.error_code, 'NO_LOCAL_RELAY_TOKEN');
  result.stages.push({name: 'missing-token-no-network', state: 'PASS'});
} finally {
  if (options.mode === 'private') await relay.close();
  else await new Promise((resolve) => relay.close(resolve));
}
console.log(JSON.stringify(result, null, 2));
