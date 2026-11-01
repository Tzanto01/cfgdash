import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    host: '127.0.0.1',
    port: 4310,
    proxy: { '/api': { target: 'http://127.0.0.1:4311' } }
  },
  build: {
    outDir: 'dist/cfgdash/browser',
    emptyOutDir: true,
    assetsInlineLimit: 0
  },
  test: {
    environment: 'jsdom',
    globals: true,
    include: ['src/**/*.test.{ts,tsx}'],
    setupFiles: ['src/test/setup.ts'],
    restoreMocks: true
  }
});
