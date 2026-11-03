import { createServer } from 'node:http';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const evidenceDir = resolve('artifacts');
await mkdir(evidenceDir, { recursive: true });

const fixture = String.raw`<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>OEC Synthetic Conversation Test</title>
<style>
body{font-family:system-ui,sans-serif;max-width:780px;margin:auto;padding:2rem;color:#222}
main{border:1px solid #bbb;border-radius:12px;padding:1.2rem}
textarea{display:block;width:95%;min-height:4rem;margin:.5rem 0}
button{margin:.5rem .5rem .5rem 0;padding:.65rem;border-radius:7px}
#messages{min-height:6rem;border:1px solid #ddd;padding:1rem}
[hidden]{display:none}
@media(max-width:480px){body{padding:.5rem}main{padding:.6rem}}
</style>
</head>
<body>
<header><h1>OEC Synthetic Conversation Test</h1></header>
<main>
<nav aria-label="Synthetic navigation"><button id="panel-button" aria-controls="panel" aria-expanded="false">Toggle diagnostics panel</button></nav>
<section id="panel" aria-label="Synthetic diagnostics panel" hidden><p>Diagnostics panel visible</p></section>
<section aria-label="Synthetic conversation" id="messages"><p id="intro">Synthetic conversation ready</p></section>
<form id="composer">
<label for="prompt">Synthetic prompt</label>
<textarea id="prompt" aria-label="Synthetic prompt" autocomplete="off"></textarea>
<button id="send" type="submit" disabled>Send synthetic message</button>
</form>
<p role="status" id="state">Awaiting synthetic input</p>
</main>
<script>
const prompt=document.getElementById('prompt');
const send=document.getElementById('send');
prompt.addEventListener('input',()=>{send.disabled=!prompt.value.trim()});
document.getElementById('composer').addEventListener('submit',e=>{
 e.preventDefault();
 if(!prompt.value.trim())return;
 document.getElementById('state').textContent='Synthetic echo accepted';
 document.getElementById('messages').insertAdjacentHTML('beforeend','<p>One synthetic message recorded</p>');
 prompt.value='';send.disabled=true;
});
document.getElementById('panel-button').addEventListener('click',()=>{
 const panel=document.getElementById('panel');
 panel.hidden=!panel.hidden;
 document.getElementById('panel-button').setAttribute('aria-expanded',String(!panel.hidden));
});
</script>
</body></html>`;

const server = createServer((req, res) => {
  if (req.method !== 'GET' || req.url !== '/fixture') {
    res.writeHead(404, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' });
    res.end('Not found');
    return;
  }
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store',
                       'X-Content-Type-Options': 'nosniff' });
  res.end(fixture);
});
await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
const base = 'http://127.0.0.1:' + server.address().port;

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [resolve('node_modules/@playwright/mcp/cli.js'),
    '--headless', '--isolated', '--no-webmcp',
    '--timeout-navigation=20000', '--timeout-settle=250',
    '--output-dir', evidenceDir],
  env: { ...process.env, PLAYWRIGHT_MCP_HEADLESS: 'true' },
  stderr: 'pipe'
});
const client = new Client({ name: 'oec-ephemeral-browser-investigator', version: '0.1.0' },
  { capabilities: {} });
let steps = [];
let result = null;
let completed = false;

const textOf = (r) => (r.content ?? []).filter(x => x.type === 'text').map(x => x.text).join('\n');
async function invoke(name, args = {}) {
  const r = await client.callTool({ name, arguments: args });
  if (r.isError) throw new Error('MCP_TOOL_ERROR:' + name);
  steps.push(name);
  return textOf(r);
}
function requireObserved(ok, code) {
  if (!ok) throw new Error('OBSERVATION_MISMATCH:' + code);
}
try {
  await client.connect(transport);
  const tools = await client.listTools();
  const names = new Set(tools.tools.map(x => x.name));
  for (const name of ['browser_navigate', 'browser_snapshot', 'browser_click', 'browser_type']) {
    requireObserved(names.has(name), 'missing-' + name);
  }

  await invoke('browser_navigate', { url: base + '/fixture' });
  const before = await invoke('browser_snapshot');
  requireObserved(before.includes('Synthetic prompt') && before.includes('Send synthetic message'),
    'synthetic-controls-absent');
  // Adaptive sequence: the next action is admitted only after observing its expected control.
  await invoke('browser_type', { target: '#prompt', element: 'Synthetic prompt input',
    text: 'fixture-only-probe', submit: false });
  const typed = await invoke('browser_snapshot');
  requireObserved(typed.includes('Send synthetic message'), 'composer-lost-after-type');
  await invoke('browser_click', { target: '#send', element: 'Send synthetic message button' });
  const after = await invoke('browser_snapshot');
  requireObserved(after.includes('Synthetic echo accepted') &&
    after.includes('One synthetic message recorded'), 'synthetic-submit-not-observed');
  await invoke('browser_click', { target: '#panel-button',
    element: 'Toggle diagnostics panel button' });
  const panel = await invoke('browser_snapshot');
  requireObserved(panel.includes('Diagnostics panel visible'), 'panel-state-not-observed');

  let syntheticScreenshot = false;
  if (names.has('browser_take_screenshot')) {
    await invoke('browser_take_screenshot', { filename: 'artifacts/synthetic-interface.png',
      fullPage: true });
    syntheticScreenshot = true;
  }
  let mobileSnapshot = false;
  if (names.has('browser_resize')) {
    await invoke('browser_resize', { width: 390, height: 844 });
    const mobile = await invoke('browser_snapshot');
    mobileSnapshot = mobile.includes('Synthetic prompt');
    requireObserved(mobileSnapshot, 'mobile-controls-missing');
  }

  // Public, signed-out page only: no login attempts, user prompts, account state or conversations.
  let publicSite = 'UNKNOWN';
  try {
    await invoke('browser_navigate', { url: 'https://chatgpt.com/' });
    const publicSnapshot = await invoke('browser_snapshot');
    publicSite = publicSnapshot.trim().length ? 'PUBLIC_SIGNED_OUT_PAGE_OBSERVABLE' :
      'PUBLIC_PAGE_NO_ACCESSIBILITY_SNAPSHOT';
  } catch {
    publicSite = 'PUBLIC_SITE_BLOCKED_OR_UNAVAILABLE';
  }

  result = {
    schema: 'oec-playwright-mcp-public-evidence/v1',
    source: 'microsoft/playwright-mcp',
    upstream_version: '0.0.84',
    browser: 'headless-isolated',
    interaction: 'MCP_TOOL_CALLS',
    discovered_tool_count: tools.tools.length,
    synthetic_navigation: 'PASS',
    synthetic_accessibility_snapshot: 'PASS',
    synthetic_fill_click_diff: 'PASS',
    synthetic_toggle: 'PASS',
    synthetic_screenshot: syntheticScreenshot,
    synthetic_mobile_snapshot: mobileSnapshot,
    public_chatgpt_signed_out_probe: publicSite,
    authenticated_chatgpt: false,
    chatgpt_private_session_access: false,
    external_plugin_connection: false,
    tool_calls: steps,
    evidence_class: 'DIRECT_BROWSER_SYNTHETIC_UI_AND_PUBLIC_SITE_ONLY',
    emitted_sensitive_content: false
  };
  completed = true;
} finally {
  await client.close().catch(() => {});
  await new Promise(ok => server.close(ok));
  if (result) await writeFile(resolve(evidenceDir, 'receipt.json'),
    JSON.stringify(result, null, 2) + '\n');
}
if (!completed) process.exitCode = 1;
else console.log('QUALIFIED_PLAYWRIGHT_MCP_INTERACTIVE_SYNTHETIC_UI public_site=' +
  result.public_chatgpt_signed_out_probe + ' no_auth=true mcp_tool_calls=' +
  result.tool_calls.length);
