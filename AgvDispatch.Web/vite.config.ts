import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { env } from 'node:process';
export default defineConfig({ plugins: [vue()], define:{__AGV_RELEASE_ID__:JSON.stringify(env.AGV_RELEASE_ID||'development')}, server: { port: 5189, strictPort: true },
  build: { target: 'es2022', sourcemap: false,
    rollupOptions: { output: { manualChunks: { three: ['three', 'three/examples/jsm/controls/OrbitControls.js'], vue: ['vue'] } } } } });
