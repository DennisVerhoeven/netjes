import netjes from 'netjes/vite';
import { defineConfig } from 'vite';

// netjes() brings React, Tailwind CSS, the TanStack Query client and the Vitest defaults.
export default defineConfig({
  plugins: [netjes()],
});
