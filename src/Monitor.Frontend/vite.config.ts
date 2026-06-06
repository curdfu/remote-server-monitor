import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

export default defineConfig({
  plugins: [vue()],
  build: {
    rollupOptions: {
      onwarn(warning, defaultHandler) {
        if (
          warning.code === 'INVALID_ANNOTATION' &&
          warning.id?.includes('@microsoft/signalr/dist/esm/Utils.js') &&
          warning.message.includes('contains an annotation that Rollup cannot interpret')
        ) {
          return;
        }

        defaultHandler(warning);
      }
    }
  },
  server: {
    port: 5173
  }
});
