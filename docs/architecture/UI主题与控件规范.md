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

## 驾驶舱主题（2026-11-27）

5199、5270与WPF统一为“控制室驾驶舱”深色主题：深海军蓝底、冷青主色、细描边面板。发光只用于选中、焦点、运行中状态和主按钮悬停，普通卡片与文字不加发光。浅色主题保留，同一套语义令牌换值。

| 令牌 | 深色 | 浅色 | 用途 |
| --- | --- | --- | --- |
| 页面底 | `#060B14` | `#EEF3F8` | 页面背景 |
| 面板 / 抬升 / 悬停 | `#0B1524` / `#0F1C2F` / `#142640` | `#FFFFFF` / `#F7FAFC` / `#EAF2F8` | 卡片、表头、行悬停 |
| 描边 / 强描边 | `#1E3350` / `#2C4A6E` | `#D5E0EB` / `#BDCBD9` | 面板与控件边框 |
| 正文 / 次要 / 弱化 | `#E6F1FF` / `#A9BCD3` / `#7C91AB` | `#0F1B2D` / `#3B4D63` / `#52657B` | 文字三级 |
| 主色 | `#22D3EE` | `#0E7490` | 选中、焦点、主操作 |
| 成功 / 警告 / 危险 / 信息 | `#34D399` / `#FBBF24` / `#F87171` / `#A78BFA` | `#047857` / `#92400E` / `#B91C1C` / `#6D28D9` | 状态文字与状态片 |

- Web令牌集中在`AgvDispatch.Web/src/cockpit-theme.css`，在`design-system.css`之后加载，只覆盖令牌值和通用外观（页头、导航、面板、表格、按钮、状态片），不改业务组件的类名与结构。
- 5270排策智演工作区（`agv-frontend/src/styles/workspace.css`）使用同一组深色令牌，并启用Element Plus暗色变量；内嵌的5199仿真页随令牌一起变色。
- WPF的`Themes/Dark.xaml`、`Light.xaml`按上表取值，保留全部已引用资源键；新增键须在深浅两套主题中同时定义。正文与背景对比度不低于4.5:1。

### 仿真查看

仿真总览与回放遵循“先看全局、再看单车、最后看证据”的顺序：

1. **运行指标条**：完成单数（已完成/总数）、运行车辆（执行任务车辆/全部车辆）、等待路权（当前处于等待状态的路权申请数）、平均等待（这些申请已等待秒数的平均值）。没有任务或路权快照时显示“—”，不补零冒充实测。
2. **车辆状态卡**：每辆车一张卡，左侧色条与状态片颜色与地图车辆颜色一致（执行中、作业等待/载箱待命、空载待命、道路受阻、暂停/故障、离线/过期）。
3. **地图图例**：分“车辆状态”和“道路与路权”两组。道路颜色含义：当前段·路权已占用、后续路段·待申请、等待路权、阻塞/障碍、返程路段。图例颜色取自地图实际描边颜色。
4. **回放播放条**：上一帧、播放/暂停、下一帧、时间滑块、帧序号与倍速放在回放地图正上方并在滚动时吸顶；回放帧内车辆与任务用卡片网格显示，等待原因用警告色。

这些改动只改变展示，不改变接口数据、轮询频率或控制许可。
