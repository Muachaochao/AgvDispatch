# 复用来源

- 项目：https://gitee.com/medicalprogrammer/agv-web-visible
- 作者/版权：Copyright (c) 2025 医码当先
- 许可证：MIT，原文见本目录 LICENSE。
- 核对提交：211179fc23395b93c34a8667ad7f4c7812ac28ba
- 原文件：src/sdk/three/utils/MeshUtils.ts

MeshUtils.ts 从该提交复制，保留上游函数；仅将 IPoint 的类型依赖改为本地类型，并添加本来源注释。

当前运行页面直接复用 makePoint、makePathObject、makeWaitingArea、makeLabel：站点、路线、等待位和车辆标签均使用上游实现。Three.js 场景管理、Vue 任务/设备页面和 C# 适配层为本次新增。

这不是上游整套页面的原样部署。没有移植上游浏览器寻路、动画完成后上报业务完成、Node.js 调度后台或远程代理配置。新的页面使用现有 C# 后台作为任务事实来源。

上游其他模型、HDR 和图片资产未复制，第一版使用本地几何绘制车辆，不依赖外部模型地址。新增依赖的许可仍分别适用。
