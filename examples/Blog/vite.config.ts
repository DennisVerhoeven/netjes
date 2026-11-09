import netjes from 'netjes/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  // The help center page package, which Program.cs mounts at /help: its pages join the page module map, and its
  // stylesheet and sources join the Tailwind build.
  plugins: [netjes({ packages: ['@netjes-examples/help-center'] })],
  // Lets a second dev server (the e2e suite's /base copy) keep its own dependency cache.
  cacheDir: process.env.VITE_CACHE_DIR,
});
