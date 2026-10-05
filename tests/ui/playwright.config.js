const { defineConfig } = require('@playwright/test');
module.exports = defineConfig({
  testDir: '.', testMatch: '*.spec.js', timeout: 30000,
  use: { baseURL: 'http://127.0.0.1:8740', headless: true },
  reporter: 'list', workers: 2,
});
