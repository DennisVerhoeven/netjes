import netjes from 'netjes/vite';
import { defineConfig } from 'vite';

// Plain CSS: the benchmark measures Netjes, not Tailwind.
export default defineConfig({
  plugins: [netjes({ tailwind: false })],
});
