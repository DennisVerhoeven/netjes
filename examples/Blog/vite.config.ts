import netjes from 'netjes/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [netjes()],
  // Lets a second dev server (the e2e suite's /base copy) keep its own dependency cache.
  cacheDir: process.env.VITE_CACHE_DIR,
});
