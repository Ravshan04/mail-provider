const { test, expect } = require('@playwright/test');

async function swagger(page) {
  await page.goto('/swagger');
  await expect(page.locator('.swagger-ui .info .title')).toContainText('Clarvio Mail API');
  await expect(page.locator('.opblock')).toHaveCount(32);
}
async function execute(page, operationId) {
  const block = page.locator('#' + operationId);
  await block.locator('.opblock-summary').click();
  await block.getByRole('button', { name: 'Try it out', exact: true }).click();
  await block.getByRole('button', { name: 'Execute', exact: true }).click();
  return block;
}

test('Swagger renders every operation with local assets and the application CSP', async ({ page }) => {
  const violations = [];
  page.on('console', message => { if (message.type() === 'error' && /content security policy|refused to/i.test(message.text())) violations.push(message.text()); });
  await swagger(page);
  await expect(page.getByText('Resolver error', { exact: false })).toHaveCount(0);
  expect(violations).toEqual([]);
  const config = await page.evaluate(() => ({ persist: window.ui.getConfigs().persistAuthorization, validator: window.ui.getConfigs().validatorUrl }));
  expect(config).toEqual({ persist: false, validator: null });
});

test('Authorize sends a scoped Bearer key in Try it out and does not persist it', async ({ page }) => {
  let authorization;
  await page.route('**/api/v1/mailboxes', async route => {
    authorization = route.request().headers().authorization;
    await route.fulfill({ status: 200, json: [] });
  });
  await swagger(page);
  await page.locator('.scheme-container').getByRole('button', { name: 'Authorize', exact: false }).click();
  const auth = page.locator('.auth-container').filter({ has: page.getByRole('heading', { name: /^ApiKey\b/ }) });
  await auth.getByRole('textbox', { name: 'auth-bearer-value', exact: true }).fill('cm_sk_swagger_test_token');
  await auth.getByRole('button', { name: 'Apply credentials', exact: true }).click();
  await auth.getByRole('button', { name: 'Close', exact: true }).click();
  const block = await execute(page, 'operations-Mailboxes-ListMailboxes');
  await expect.poll(() => authorization).toBe('Bearer cm_sk_swagger_test_token');
  await expect(block.locator('.responses-inner')).toContainText('200');
  await page.reload();
  await expect(page.locator('.opblock')).toHaveCount(32);
  const saved = await page.evaluate(() => window.ui.authSelectors.authorized().toJS());
  expect(saved).toEqual({});
});

test('session mutations automatically send CSRF without making a real logout request', async ({ page }) => {
  let csrf;
  await page.route('**/api/v1/session', route => route.fulfill({ status: 200, json: { csrfToken: 'swagger-test-csrf', user: { email: 'test@example.com' } } }));
  await page.route('**/api/v1/logout', async route => {
    csrf = route.request().headers()['x-csrf-token'];
    await route.fulfill({ status: 204 });
  });
  await swagger(page);
  await execute(page, 'operations-Authentication-Logout');
  await expect.poll(() => csrf).toBe('swagger-test-csrf');
});
