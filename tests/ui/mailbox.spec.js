const { test, expect } = require('@playwright/test');

const folders = [
  { id: 'inbox', name: 'Inbox', canMove: true }, { id: 'sent', name: 'Sent', canMove: false },
  { id: 'drafts', name: 'Drafts', canMove: false }, { id: 'archive', name: 'Archive', canMove: true },
  { id: 'trash', name: 'Trash', canMove: true }, { id: 'custom:projects', name: 'Projects', canMove: true },
];
const original = {
  id: 'message-one', subject: 'A test conversation', from: { email: 'sender@example.com' },
  to: [{ email: 'owner@example.com' }], cc: [], bcc: [], textBody: 'Original body', htmlBody: null,
  preview: 'Original body', attachments: [], isRead: false,
};

async function workspace(page, { uncertainSend = false, failedSave = false } = {}) {
  const writes = [];
  let draft = {
    ...original, id: 'draft-message', draftId: 'stable-draft', subject: 'Saved draft', textBody: 'Draft body',
    to: [{ email: 'to@example.com' }], cc: [{ email: 'cc@example.com' }], bcc: [{ email: 'hidden@example.com' }],
    attachments: [{ id: 'old-file', name: 'old.txt', contentType: 'text/plain', size: 4 }],
  };
  let deleted = false;
  await page.route('**/api/v1/**', async route => {
    const request = route.request();
    const url = new URL(request.url()); const path = url.pathname.replace('/api/v1', '');
    const method = request.method(); const body = request.postDataJSON();
    if (method !== 'GET') writes.push({ path, method, body, headers: request.headers() });
    let data;
    if (path === '/providers') data = [{ provider: 'outlook', configured: true }, { provider: 'gmail', configured: true }];
    else if (path === '/session') data = { user: { email: 'owner@example.com' }, csrfToken: 'test-csrf' };
    else if (path === '/mailboxes') data = [{ id: 'box-one', email: 'owner@example.com', status: 'active', provider: 'gmail' }];
    else if (path === '/keys') data = [];
    else if (path.endsWith('/folders')) data = folders;
    else if (method === 'GET' && path.endsWith('/messages')) data = {
      messages: url.searchParams.get('folder') === 'drafts' ? (deleted ? [] : [draft]) : [original], nextCursor: null,
    };
    else if (method === 'GET' && path.includes('/drafts/')) data = draft;
    else if (method === 'GET' && path.includes('/messages/')) data = original;
    else if (path.endsWith('/read')) data = null;
    else if (path.endsWith('/move')) data = original;
    else if (path.endsWith('/send')) data = { id: 'operation-one', status: uncertainSend ? 'unknown' : 'accepted' };
    else if (method === 'DELETE' && path.includes('/drafts/')) { deleted = true; data = null; }
    else if ((method === 'POST' || method === 'PUT') && path.includes('/drafts')) {
      if (failedSave) return route.fulfill({ status: 502, json: { error: { code: 'provider_unavailable', message: 'Not confirmed' } } });
      const retained = draft.attachments.filter(file => body.keepAttachmentIds?.includes(file.id));
      draft = { ...draft, ...body.message, attachments: [...retained, ...(body.message.attachments || []).map((file, i) => ({ id: 'new-file-' + i, name: file.name, contentType: file.contentType, size: 3 }))] };
      data = draft;
    } else throw new Error('Unexpected mocked API request: ' + method + ' ' + path);
    return route.fulfill(data === null ? { status: 204 } : { status: 200, json: data });
  });
  await page.goto('/'); await expect(page.getByRole('button', { name: 'A test conversation', exact: false })).toBeVisible();
  return writes;
}

test('marks mail read, moves to a custom folder, and trashes without permanent deletion', async ({ page }) => {
  const writes = await workspace(page);
  await page.locator('[data-message="message-one"]').click();
  await page.getByRole('button', { name: 'Mark read', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Mark unread', exact: true })).toBeVisible();
  expect(writes[0].body).toEqual({ isRead: true }); expect(writes[0].headers['x-csrf-token']).toBe('test-csrf');
  await page.getByRole('combobox', { name: 'Move destination' }).selectOption('custom:projects');
  await page.getByRole('button', { name: 'Move', exact: true }).click();
  await expect(page.locator('#reader')).toContainText('Select a conversation');
  expect(writes[1].body).toEqual({ destinationFolderId: 'custom:projects', sourceFolderId: 'inbox' });
  await page.locator('[data-message="message-one"]').click();
  await page.getByRole('button', { name: 'Move to Trash' }).click();
  await expect(page.locator('#reader')).toContainText('Select a conversation');
  expect(writes[2].body.destinationFolderId).toBe('trash'); expect(writes.every(x => x.method !== 'DELETE')).toBe(true);
});

test('saves an unfinished message without a recipient or subject and never sends it', async ({ page }) => {
  const writes = await workspace(page);
  await page.getByRole('button', { name: '+ Compose' }).click();
  await page.getByLabel('Message', { exact: true }).fill('Unfinished body');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Draft saved');
  expect(writes).toHaveLength(1); expect(writes[0].path).toBe('/mailboxes/box-one/drafts');
  expect(writes[0].body.message.to).toEqual([]); expect(writes[0].body.message.subject).toBe('');
});

test('reopens a draft with CC/BCC, retains attachments, and sends only after the Send button', async ({ page }) => {
  const writes = await workspace(page);
  await page.getByRole('combobox', { name: 'Folder', exact: true }).selectOption('drafts');
  await page.locator('[data-message="draft-message"]').click();
  await expect(page.locator('#reader')).toContainText('BCC: hidden@example.com');
  await page.getByRole('button', { name: 'Edit draft' }).click();
  await expect(page.getByLabel('CC', { exact: true })).toHaveValue('cc@example.com');
  await expect(page.getByLabel('BCC', { exact: true })).toHaveValue('hidden@example.com');
  await page.locator('#files').setInputFiles({ name: 'new.txt', mimeType: 'text/plain', buffer: Buffer.from('new') });
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Draft saved');
  expect(writes).toHaveLength(1); expect(writes[0].method).toBe('PUT');
  expect(writes[0].body.keepAttachmentIds).toEqual(['old-file']);
  expect(writes[0].body.message.attachments[0].contentBase64).toBe('bmV3');
  await expect(page.getByRole('checkbox', { name: 'new.txt', exact: false })).toBeChecked();
  await page.getByRole('button', { name: 'Send message', exact: true }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible();
  expect(writes[1].body.keepAttachmentIds).toEqual(['old-file', 'new-file-0']);
  expect(writes[1].body.message.attachments).toEqual([]);
  expect(writes[2].path).toBe('/mailboxes/box-one/drafts/stable-draft/send');
  expect(writes[2].headers['idempotency-key']).toBeTruthy();
});

test('saving a reply includes the source message, then keeps the same draft on edits', async ({ page }) => {
  const writes = await workspace(page);
  await page.locator('[data-message="message-one"]').click();
  await page.getByRole('button', { name: 'Reply', exact: true }).click();
  await page.getByLabel('Message', { exact: true }).fill('My reply');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Draft saved');
  expect(writes[0].body.replyMessageId).toBe('message-one');
  expect(writes[0].body.message.to).toEqual([{ email: 'sender@example.com' }]);
  await page.getByLabel('Message', { exact: true }).fill('Edited reply');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(writes[1].method).toBe('PUT'); expect(writes[1].path).toContain('stable-draft');
  expect(writes[1].body.replyMessageId).toBeUndefined();
});

test('an uncertain draft send blocks resending and preserves a visible operation ID', async ({ page }) => {
  const writes = await workspace(page, { uncertainSend: true });
  await page.getByRole('combobox', { name: 'Folder', exact: true }).selectOption('drafts');
  await page.locator('[data-message="draft-message"]').click();
  await page.getByRole('button', { name: 'Edit draft' }).click();
  await page.getByRole('button', { name: 'Send message', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('operation-one');
  await expect(page.getByRole('button', { name: 'Send message', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toBeDisabled();
  expect(writes.filter(x => x.path.endsWith('/send'))).toHaveLength(1);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click(); await expect(page.getByRole('dialog')).not.toBeVisible();
});

test('an unconfirmed draft save blocks retries until the draft is reopened', async ({ page }) => {
  const writes = await workspace(page, { failedSave: true });
  await page.getByRole('button', { name: '+ Compose' }).click();
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Reopen Drafts');
  await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Send message', exact: true })).toBeDisabled();
  expect(writes).toHaveLength(1);
});

test('deleting a draft requires confirmation and API write scopes are opt in', async ({ page }) => {
  const writes = await workspace(page);
  await page.getByRole('combobox', { name: 'Folder', exact: true }).selectOption('drafts');
  await page.locator('[data-message="draft-message"]').click();
  page.once('dialog', dialog => dialog.dismiss());
  await page.getByRole('button', { name: 'Delete draft' }).click(); expect(writes).toHaveLength(0);
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Delete draft' }).click();
  await expect(page.locator('.message-list')).toContainText('No messages found');
  expect(writes[0].method).toBe('DELETE'); expect(writes[0].path).toContain('/drafts/stable-draft');
  await page.locator('[data-view="keys"]').click();
  await expect(page.getByRole('checkbox', { name: 'mail:write', exact: true })).not.toBeChecked();
  await expect(page.getByRole('checkbox', { name: 'mail:read', exact: true })).toBeChecked();
});
