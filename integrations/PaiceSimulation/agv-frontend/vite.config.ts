import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import { fileURLToPath, URL } from "node:url";

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  server: {
    host: "127.0.0.1",
    strictPort: true,
    port: 5270,
    proxy: {
      "/api": {
        target: "http://127.0.0.1:5271",
        changeOrigin: true,
      },
      "/ws": {
        target: "ws://127.0.0.1:5271",
        ws: true,
      },
    },
  },
  build: {
    chunkSizeWarningLimit: 1200,
    rollupOptions: {
      output: {
        manualChunks: {
          three: ["three"],
          echarts: ["echarts"],
          element: ["element-plus"],
        },
      },
    },
  },
});
