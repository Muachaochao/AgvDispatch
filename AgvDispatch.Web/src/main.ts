import { createApp } from 'vue';
import { createPinia } from 'pinia';
import {
  ElButton,
  ElConfigProvider,
  ElDatePicker,
  ElInput,
  ElOption,
  ElPagination,
  ElSelect,
  ElTable,
  ElTableColumn
} from 'element-plus';
import 'element-plus/es/components/base/style/css';
import 'element-plus/es/components/button/style/css';
import 'element-plus/es/components/icon/style/css';
import 'element-plus/es/components/input/style/css';
import 'element-plus/es/components/select/style/css';
import 'element-plus/es/components/option/style/css';
import 'element-plus/es/components/date-picker/style/css';
import 'element-plus/es/components/table/style/css';
import 'element-plus/es/components/pagination/style/css';
import 'element-plus/es/components/tooltip/style/css';
import 'element-plus/es/components/popper/style/css';
import 'element-plus/es/components/scrollbar/style/css';
import 'element-plus/theme-chalk/dark/css-vars.css';
import App from './App.vue';
import router from './router';
import { applyUiTheme, preferredTheme } from './ui-theme';
import './console.css';
import './status-polish.css';
import './design-system.css';

applyUiTheme(preferredTheme());

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.component('ElButton', ElButton);
app.component('ElConfigProvider', ElConfigProvider);
app.component('ElDatePicker', ElDatePicker);
app.component('ElInput', ElInput);
app.component('ElOption', ElOption);
app.component('ElPagination', ElPagination);
app.component('ElSelect', ElSelect);
app.component('ElTable', ElTable);
app.component('ElTableColumn', ElTableColumn);
app.mount('#app');
