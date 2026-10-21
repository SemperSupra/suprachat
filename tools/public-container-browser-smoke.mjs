import {chromium, firefox, webkit} from '/opt/suprachat/node_modules/playwright/index.mjs';

const id = '123e4567-e89b-42d3-a456-426614174000';
const target = 'https://chatgpt.com/c/' + id;
const results = [];
const html = '<!doctype html><meta charset="utf-8"><title>Synthetic ChatGPT</title>' +
'<textarea id="prompt-textarea"></textarea><button data-testid="send-button">Send</button>' +
'<script>document.querySelector("button").addEventListener("click", () => {' +
' const text = document.querySelector("textarea").value;' +
' const result = document.createElement("article");' +
' result.setAttribute("data-message-author-role","assistant");' +
' result.textContent = text === "QUALIFY_PUBLIC_SYNTHETIC" ? "PUBLIC_SYNTHETIC_OK" : "WRONG_REQUEST";' +
' document.body.appendChild(result);' +
'});</script>';
for (const [name, browserType] of Object.entries({chromium, firefox, webkit})) {
  let browser;
  try {
    browser = await browserType.launch({headless: true});
    const page = await browser.newPage();
    await page.route('https://chatgpt.com/**', (route) => route.fulfill({
      status: 200, contentType: 'text/html', body: html,
    }));
    await page.goto(target);
    await page.locator('#prompt-textarea').fill('QUALIFY_PUBLIC_SYNTHETIC');
    await page.locator('button[data-testid="send-button"]').click();
    await page.locator('[data-message-author-role="assistant"]').getByText('PUBLIC_SYNTHETIC_OK').waitFor();
    const ok = (await page.locator('[data-message-author-role="assistant"]').count()) === 1;
    results.push({engine: name, status: ok ? 'PASS' : 'FAIL'});
    await page.close();
  } catch (error) {
    results.push({engine: name, status: 'FAIL', error_class: error?.name || 'Error'});
  } finally {
    await browser?.close();
  }
}
console.log(JSON.stringify({
  schema: 'suprachat-public-linux-container-capability/v1',
  image: 'mcr.microsoft.com/playwright:v1.62.0-noble',
  source: 'public-synthetic-fixture',
  network: 'none',
  touched_real_chatgpt: false,
  private_source_used: false,
  results,
}, null, 2));
if (results.some((x) => x.status !== 'PASS')) process.exitCode = 1;
