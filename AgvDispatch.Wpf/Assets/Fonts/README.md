# 阿里 Iconfont 离线资源

当前框架使用 Windows 内置的 `Segoe Fluent Icons` 作为占位图标字体。

拿到阿里 Iconfont 项目文件后：

1. 将 `iconfont.ttf` 放入本目录；
2. 在 `AgvDispatch.Wpf.csproj` 中将字体标记为 `Resource`；
3. 把 `Styles/IconGlyphs.xaml` 中的 `AppIconFont` 改为项目内字体 URI；
4. 将菜单中的 Unicode 编码替换为阿里项目导出的编码；
5. 随项目保存图标来源和授权说明，不允许运行时引用公网 CSS。
