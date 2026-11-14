import { createApp } from 'vue';
import { createPinia } from 'pinia';
import {
  ElAlert,
  ElButton,
  ElCollapse,
  ElCollapseItem,
  ElConfigProvider,
  ElDatePicker,
  ElDescriptions,
  ElDescriptionsItem,
  ElDrawer,
  ElForm,
  ElFormItem,
  ElInput,
  ElInputNumber,
  ElOption,
  ElPagination,
  ElSelect,
  ElStep,
  ElSteps,
  ElTag,
  ElTable,
  ElTableColumn
} from 'element-plus';
import 'element-plus/es/components/base/style/css';
import 'element-plus/es/components/alert/style/css';
import 'element-plus/es/components/button/style/css';
import 'element-plus/es/components/collapse/style/css';
import 'element-plus/es/components/collapse-item/style/css';
import 'element-plus/es/components/descriptions/style/css';
import 'element-plus/es/components/descriptions-item/style/css';
import 'element-plus/es/components/icon/style/css';
import 'element-plus/es/components/input/style/css';
import 'element-plus/es/components/input-number/style/css';
import 'element-plus/es/components/form/style/css';
import 'element-plus/es/components/form-item/style/css';
import 'element-plus/es/components/select/style/css';
import 'element-plus/es/components/option/style/css';
import 'element-plus/es/components/date-picker/style/css';
import 'element-plus/es/components/table/style/css';
import 'element-plus/es/components/pagination/style/css';
import 'element-plus/es/components/step/style/css';
import 'element-plus/es/components/steps/style/css';
import 'element-plus/es/components/tag/style/css';
import 'element-plus/es/components/tooltip/style/css';
import 'element-plus/es/components/popper/style/css';
import 'element-plus/es/components/scrollbar/style/css';
import 'element-plus/es/components/drawer/style/css';
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
app.component('ElAlert', ElAlert);
app.component('ElButton', ElButton);
app.component('ElCollapse', ElCollapse);
app.component('ElCollapseItem', ElCollapseItem);
app.component('ElConfigProvider', ElConfigProvider);
app.component('ElDatePicker', ElDatePicker);
app.component('ElDescriptions', ElDescriptions);
app.component('ElDescriptionsItem', ElDescriptionsItem);
app.component('ElDrawer', ElDrawer);
app.component('ElForm', ElForm);
app.component('ElFormItem', ElFormItem);
app.component('ElInput', ElInput);
app.component('ElInputNumber', ElInputNumber);
app.component('ElOption', ElOption);
app.component('ElPagination', ElPagination);
app.component('ElSelect', ElSelect);
app.component('ElStep', ElStep);
app.component('ElSteps', ElSteps);
app.component('ElTag', ElTag);
app.component('ElTable', ElTable);
app.component('ElTableColumn', ElTableColumn);
app.mount('#app');
