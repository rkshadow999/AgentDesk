import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  base: "./",
  plugins: [react()],
  build: {
    chunkSizeWarningLimit: 2500,
    rollupOptions: {
      output: {
        assetFileNames: "assets/asset-[hash][extname]"
      }
    }
  },
  worker: {
    rollupOptions: {
      output: {
        entryFileNames: "assets/worker-[hash].js"
      }
    }
  },
  test: {
    environment: "jsdom",
    setupFiles: "./tests/setup.ts"
  }
});
