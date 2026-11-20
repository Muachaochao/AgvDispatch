import { defineConfig, searchForWorkspaceRoot } from "vite";
import vue from "@vitejs/plugin-vue";
import { fileURLToPath, URL } from "node:url";

const frontend = fileURLToPath(new URL(".", import.meta.url));
const mainWebSource = fileURLToPath(new URL("../../../AgvDispatch.Web/src", import.meta.url));

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
      "@main-web": mainWebSource,
    },
    dedupe: ["vue", "vue-router", "pinia", "three", "@tabler/icons-vue"],
  },
  server: {
    fs: { allow: [searchForWorkspaceRoot(frontend), mainWebSource] },
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
