import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import router from './router';
import './console.css';
import './status-polish.css';
import './design-system.css';

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.mount('#app');
