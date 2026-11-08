# SixLabors.ImageSharp 安全升级验证记录

## 结论

`SixLabors.ImageSharp 3.1.12` 存在 6 个已公开漏洞，安全修复边界为 `3.2.0`。升级尝试被 Six Labors 商业许可证构建检查阻止，当前工程没有可用于编译安全版本的许可证。为避免许可证违规和隐藏漏洞，已恢复到 `3.1.12` 可编译基线；未伪造、绕过或关闭许可证及 NuGet 审计。

## 依赖链

```text
AgvDispatch.Infrastructure
  -> ACadSharp.Image 0.1.6
    -> SixLabors.ImageSharp 3.1.12
    -> SixLabors.ImageSharp.Drawing 2.1.7
```

代码直接使用点：

- `AgvDispatch.Infrastructure/Maps/Import/Smap2DImportService.cs`
- `AgvDispatch.Infrastructure/Maps/Cad/CadUnderlayService.cs`
- `tools/MapTrafficSmokeTest/MapGeometrySmokeTests.cs`

## 漏洞公告

| 公告 | 等级 | 风险范围 |
| --- | --- | --- |
| GHSA-v76p-62qx-wwq2 | High | Tiled fax TIFF 解码缓冲区越界写 |
| GHSA-j3p4-wp97-rph4 | High | HistogramEqualization 未校验直方图索引 |
| GHSA-j9gm-c75j-xc9q | High | TIFF CCITT T4 编码器压缩缓冲区越界写 |
| GHSA-jjfr-hcj7-qf5w | High | TIFF CCITT T6 编码器压缩缓冲区越界写 |
| GHSA-gwg2-r3hj-4w44 | Moderate | ICC CLUT 使用未校验尺寸分配内存 |
| GHSA-wmxv-xphr-5c9g | Moderate | BigTIFF IFD 数量导致非进展循环 |

上述公告对 3.x 的受影响范围均为 `< 3.2.0`。

## 已验证的升级路径

已验证 NuGet 上存在以下安全版本：

- `SixLabors.ImageSharp 3.2.0`
- `SixLabors.ImageSharp.Drawing 2.2.0`
- `ACadSharp.Image 0.1.8` 的 net10 依赖为 ImageSharp `4.1.3` 和 Drawing `3.1.3`

最小升级路径是增加以下直接依赖：

```xml
<PackageReference Include="SixLabors.ImageSharp" Version="3.2.0" />
<PackageReference Include="SixLabors.ImageSharp.Drawing" Version="2.2.0" />
```

执行 `dotnet restore` 成功。执行完整解决方案构建时，包内 target 阻止编译：

```text
No Six Labors license found. Set $(SixLaborsLicenseKey),
set $(SixLaborsLicenseFile), or add a 'sixlabors.lic'
file to the project/workspace.
Please obtain a license from https://sixlabors.com/pricing/
```

另已验证：

- 当前进程没有 `SixLaborsLicenseKey`。
- 当前进程没有 `SixLaborsLicenseFile`。
- 工程目录中没有 `sixlabors.lic` 或可用 `.lic` 文件。
- 用户目录扫描未找到 `sixlabors.lic`。

## 当前回退状态

- 已移除临时添加的 ImageSharp 和 Drawing 直接依赖。
- 已重新执行 `dotnet restore AgvDispatch.sln --force-evaluate`。
- 当前锁定版本重新回到 `ImageSharp 3.1.12`、`Drawing 2.1.7`。
- `dotnet build AgvDispatch.sln -c Release --no-restore --nologo`：成功，0 错误。
- 构建仍显示 4 个 High 和 2 个 Moderate 的 NuGet 审计警告，这是预期的未修复状态，未被屏蔽。

## 当前攻击面

项目没有导入 TIFF、BigTIFF、传真 TIFF 或 ICC 配置文件的公开入口。Smap 导入读取受限 JSON 并生成 RGBA 像素；CAD 底图只接受 DWG/DXF，输出 PNG/SVG。相关漏洞主要由 ImageSharp 的 TIFF、ICC 和 HistogramEqualization 功能触发，因此当前已知业务路径缩小了暴露面，但间接依赖仍会被审计，不能视为安全修复。

## 启用安全版本所需条件

1. 项目取得有效的 Six Labors 商业许可证。
2. 通过 `SixLaborsLicenseFile` 指向 `sixlabors.lic`，或通过安全构建环境提供 `SixLaborsLicenseKey`。
3. 恢复 ImageSharp `3.2.0` 与 Drawing `2.2.0` 的配套引用。
4. 执行完整 restore/build，确认许可证 target 通过。
5. 运行 MapTraffic、MapEditor、CadDetailPipe、NativeSmap、PointCloud3D 和完整解决方案回归。
6. 确认 `dotnet list AgvDispatch.sln package --include-transitive --vulnerable` 不再报告 ImageSharp 漏洞。

如果不能取得许可证，应另立工作项，移除 `ACadSharp.Image`/`ImageSharp` 的 CAD 栅格渲染依赖并替换为可接受的开源渲染方案。该方案需要重新实现 CAD 图层、曲线、块引用、样条、PNG/SVG 输出和现有 UI 预览，不能作为普通补丁直接上线。
