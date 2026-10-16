# UI主题与控件规范

保留现有蓝色工业风格、导航、页面业务与地图深色画布。控件库使用已安装的HandyControl 3.5.1，不新增其他UI框架。

## 资源顺序

App.xaml依次加载HC SkinDefault、HC Theme、应用Light、HandyPalette、Typography、IconGlyphs、Controls、HandyControls。ThemeService切换应用Light/Dark时，同时切换HC SkinDefault/SkinDark，并重新建立HandyPalette适配器，避免弹出层引用首次加载的浅色画刷。

- Themes/Light.xaml与Dark.xaml：应用语义画刷、HC语义颜色。浅色状态文字使用更深的绿/橙/红，保证小字号可读。
- Themes/HandyPalette.xaml：HC文本、区域、默认按钮与主色画刷映射。
- Styles/HandyControls.xaml：真实HC TextBoxExtend、ComboBoxExtend、ButtonDefault、ButtonPrimary模板；统一圆角、字号、输入内边距及表格颜色。
- Shared/Feedback：IUserFeedback与HandyUserFeedback，主窗口专属Growl容器，避免通知浮到其他程序。

## 页面规则

每个自定义UserControl根节点显式设置动态TextPrimaryBrush；WPF对自定义派生控件不保证应用基类隐式样式。TextBlock默认只设置字号，继承控件文字颜色，不覆盖按钮的白色文字。页面标题、次级说明、告警颜色使用语义资源。

禁止在表单或下拉框的局部资源里重新定义黑色TextPrimaryBrush。地图画布及其图元可保持固定深色；普通表单、标签页和数据表必须适配主题。

局部DataGrid样式以AppDataGridStyle为基础，局部TextBox样式以AppTextBoxStyle为基础。保留所有原有绑定、数值校验、命令与业务安全条件。

## 通知与对话框

连接配置保存前使用HC MessageBox确认，默认取消。保存成功、失败、校验错误、显式TCP测试使用Growl结果通知，同时保留页面文字说明。未注入IUserFeedback的测试仍可独立运行。

轮询、重连和状态刷新不弹通知，不增加遥测日志。消息框确认只负责交互，不替代后台业务校验、版本检查或现场安全联锁。

## 验证

ThemeControlsSmokeTest覆盖深浅两种主题、输入/下拉/按钮/状态文字对比度、HC与应用画刷一致性、下拉弹出层、主窗口通知、真实设置说明和默认取消对话框，并渲染设置与地图编辑页面。DeviceMonitoringSmokeTest、HeaderUiSmokeTest和MapEditorSmokeTest用于业务与导航回归。

测试使用独立内存配置、不调用通信API、不下发设备指令。程序运行锁定调试目录时，在Release目录验证，避免强行关闭用户窗口。
