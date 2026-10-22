#!/usr/bin/env node
// Public, provider-agnostic launcher for locally admitted PRIVATE browser fixtures.
// This launcher contains no private source and MUST NOT be run in public CI with
// a private checkout. Does not access chatgpt.com or browser profile credentials.
import fs from 'node:fs/promises';
import path from 'node:path';
import {pathToFileURL} from 'node:url';

function getArgs(argv) {
  const parsed = {};
  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i];
    if (!['--private-root', '--playwright-root', '--receipt', '--browsers'].includes(key) ||
        i + 1 >= argv.length || !argv[i + 1]) {
      throw new Error('MISSING_OR_UNKNOWN_ARG');
    }
    parsed[key] = argv[i + 1];
  }
  for (const key of ['--private-root', '--playwright-root', '--receipt']) {
    if (!parsed[key]) throw new Error('REQUIRED_ARG_ABSENT');
  }
  return parsed;
}

const args = getArgs(process.argv.slice(2));
const privateRoot = await fs.realpath(args['--private-root']);
const playwrightRoot = await fs.realpath(args['--playwright-root']);
const playwrightPkg = path.join(playwrightRoot, 'node_modules', 'playwright');
const manifest = JSON.parse(await fs.readFile(path.join(playwrightPkg, 'package.json'), 'utf8'));
if (manifest.version !== '1.62.0') throw new Error('PLAYWRIGHT_VERSION_DRIFT');
const smokePath = path.join(privateRoot, 'tests', 'browser', 'smoke.mjs');
const builtPath = path.join(privateRoot, 'dist', 'scripts', 'chatgpt-local-bridge', 'chatgpt-local-bridge.user.js');
for (const needed of [smokePath, builtPath]) await fs.access(needed);

const playwright = await import(pathToFileURL(path.join(playwrightPkg, 'index.mjs')).href);
const module = await import(pathToFileURL(smokePath).href);
const engines = (args['--browsers'] || 'chromium').split(',');
if (!engines.length || engines.some((name) => !['chromium', 'firefox', 'webkit'].includes(name))) {
  throw new Error('UNSUPPORTED_BROWSER_ENGINE');
}

const receipt = {
  schema: 'suprachat-private-bridge-native-browser-smoke/v1',
  provenance: 'local-pinned-private-branch-only',
  script_id: 'chatgpt-local-bridge',
  playwright_version: manifest.version,
  provider_contacted: false,
  authenticated_browser_access: false,
  private_source_copied_to_public: false,
  browser_results: [],
};
let anyFailure = false;
for (const name of engines) {
  try {
    await module.run({
      browserType: playwright[name], name, root: privateRoot,
      scriptId: 'chatgpt-local-bridge',
    });
    receipt.browser_results.push({name, state: 'PASS'});
    console.log('Native offline bridge smoke: ' + name + ' PASS');
  } catch (err) {
    anyFailure = true;
    receipt.browser_results.push({
      name, state: 'FAIL',
      reason_class: err?.name || 'Error',
    });
    // No exception message, private source path, screenshot, or transcript in receipt.
    console.error('Native offline bridge smoke: ' + name + ' FAIL (' + (err?.name || 'Error') + ')');
  }
}
const destination = path.resolve(args['--receipt']);
await fs.mkdir(path.dirname(destination), {recursive: true});
await fs.writeFile(destination, JSON.stringify(receipt, null, 2) + '\n');
if (anyFailure) process.exitCode = 1;
