# ScreenshotDock

Windows 原生截图与临时素材收纳工具。C# / .NET 10 / WPF。按照提供的产品 PRD v1.0 实现 V1.0 MVP。

**Ctrl + Shift + A → 框选 → 工具栏标注 → 完成 / 复制 / 保存 → 顶部收纳。**

## 运行

已发布程序：`artifacts/ScreenshotDock-win-x64/ScreenshotDock.exe`。

开发运行（PowerShell）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

脚本优先使用项目内 `.tools/dotnet`，或系统 .NET 10 SDK；缺少时下载官方 10.0.401 SDK 至项目 `.tools`，不改系统 PATH。首次还原与自包含发布需要网络，应用运行离线。

## 构建、验证与发布

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1 -Editor
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1 -Pin
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1 -Edge
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1 -Settings
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1 -Annotations
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1 -Integration
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1
```

集成测试需要交互式 Windows 桌面，会短暂打开本工具测试窗口，验证实际 GDI 采集、快捷键、剪贴板、OLE 文件拖放和热区原生滚轮控制；将测试图片写到剪贴板，并将视觉证据及结果放入 `artifacts/verification`。集成测试使用独立缓存，不使用真实用户截图库。

`-Editor` 单独验证截图编辑器：实际工具栏按钮、标注合成、五种箭头样式与折线路径、中文文字、撤销重做、屏幕边缘布局、保存失败恢复，以及真实 GDI 截图到缓存、文件和剪贴板的完整流程。使用合成画布和专用测试窗口，不移动桌面鼠标，结果位于 `artifacts/editor-verification`。保存路径选择器在测试中注入，系统保存对话框仍需手动验收。

发布生成 x64 自包含 EXE 与 ZIP，图标内嵌，无外部资源文件依赖。后续重新发布前通过托盘退出旧进程。

界面统一采用蓝色主题，按钮、挂绳卡片和设置选中态保持一致。截图选区采用鲜蓝色（#176BFF）3 DIP 边框，角点同步加大；界面装饰不进入导出图片。Logo 采用蓝色渐变圆角底、框选角、倾斜图片卡片和浅蓝色夹子，表达“截图即收纳”。矢量源文件为 `assets/logo.svg`，透明 1024 px PNG 为 `assets/logo.png`，浅色与深色背景预览为 `assets/logo-preview.png`。`assets/app.ico` 包含 16、20、24、32、40、48、64、128、256 px 九档图像，供程序与托盘使用；`scripts/create-icon.ps1` 生成 PNG/ICO，`scripts/preview-logo.ps1` 检查每档原生加载、尺寸和透明边角并生成预览。ICO 内采用带透明度的 PNG 帧；原生加载方式参见 [Microsoft 图标资源说明](https://devblogs.microsoft.com/oldnewthing/20231025-00/?p=108925)。

## 结构

- `src/ScreenshotDock.Core`：数据模型、标注历史、原子 JSON、截图去重、恢复和保留策略。
- `src/ScreenshotDock.Windows`：Win32 GDI、物理像素与显示器、全局快捷键、剪贴板和托盘。
- `src/ScreenshotDock.App`：截图选区与标注工具栏、图片合成、顶部栏、设置与预览，以及应用生命周期。
- `tests`：存储回归测试和 Windows 集成测试。
- `docs`：[使用说明](docs/使用说明.md)、验收与限制。

全局快捷键替换先注册新组合，冲突不会拆掉旧组合。截图前隐藏自身窗口并等待 DWM 完成更新；每个显示器独立呈现选区，但选区和最终裁剪始终使用虚拟桌面的物理像素。暂存原图与缩略图通过 GUID 定位，JSON 不控制删除路径。

选区右下方提供矩形、椭圆、箭头、画笔、框选马赛克、文字、移除标注、撤销、重做、保存、复制和完成按钮，靠近屏幕边缘时自动调整位置。所有显示器共用 `CaptureEditSession` 和 `AnnotationDocument`，标注坐标相对于选区，移动选区时保持位置，调整尺寸时裁剪超出的标注。`AnnotationRenderer` 将原始截图与标注合成到物理像素尺寸的图片，选区边框、工具栏和遮罩不进入导出结果。保存使用同目录临时文件后替换目标，失败或取消时继续编辑；暂存目录受到保护。

马赛克工具使用蓝色四格图标，填充和外框绑定工具按钮的前景色，与其他工具一致，空白格透出按钮背景。标注保留相对矩形和物理颗粒大小，支持框选、移动、调整大小、移除及撤销重做；活动区域由 `SelectionCanvas` 绘制细短白色虚线和 8 个带白色轮廓的小方形控制点，不进入导出结果。在马赛克或选择工具下拖动已有区域时，`CaptureEditSession` 保存移动预览，限制区域到选区边缘，保持尺寸和颗粒大小；绘制预览替换原图层，松开时通过 `ReplaceAt` 一次提交历史，Esc 或失去鼠标捕获时恢复原位置。控制点通过 `MosaicSelectionGeometry` 共用物理坐标与命中测试，四角调整两条边，边中点只调整对应边；缩放使用同一预览与历史事务，限制范围并保留颗粒大小和原始端点方向。截图选区改变或文档修改会清除过期的拖动状态。`MosaicRenderer` 将原始选区按块求平均，缓存小尺寸网格，以弱引用关联源图；绘制使用显式最近邻采样的 `DrawingGroup`，并裁切到矩形内，预览与导出共用逻辑。位图生成和缩放机制参见 [Microsoft BitmapSource.Create](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.imaging.bitmapsource.create?view=windowsdesktop-10.0)、[BitmapScalingMode](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapscalingmode?view=windowsdesktop-10.0)。

文字输入框根据内容和字号动态调整宽度，并在选区边缘换行。在文字或调整选区工具下点击已有文字会打开原标注进行修改；编辑期间隐藏原图层的文字，确认时用 `AnnotationDocument.ReplaceAt` 替换同一图层并保留历史，Esc 则恢复原内容。删除全部文字可移除标注，无变化的确认不会增加撤销步骤。`-Annotations` 验证马赛克像素和裁切、文字动态布局与重复编辑、PNG 保存及完整保存/贴图流程；测试使用独立缓存和专用图像，证据位于 `artifacts/annotation-verification`。复制检查验证工具栏确认文字后发出合成请求，该组不访问系统剪贴板。

箭头工具提供细线、实心、渐尖、空心和折线五种样式。空心样式使用渐尖轮廓，尾部细、靠近箭头逐渐变宽，内部保持透明。每个 `ScreenshotAnnotation` 保留独立的 `ArrowStyle` 和折线路径，`ArrowGeometry` 共用于界面绘制、导出和移除命中检测，实心箭头可点击内部移除，空心箭头按外轮廓命中。折线路径保留转折，去除细小鼠标抖动；工具切换不会修改已绘制的箭头。

截图工具栏的图钉按钮将带标注的图片贴到屏幕，并保留在暂存栏。`PinnedImageWindow` 为独立的置顶窗口，支持同时显示多张、拖动摆放、未选中时外侧显示淡白色阴影，点击激活后变为蓝色阴影，Esc 仅关闭收到键盘输入的贴图，不删除暂存图片。再次截图前隐藏已有贴图，结束或取消后恢复；退出时关闭所有贴图。`scripts/test.ps1 -Pin` 验证渐尖空心箭头像素、完整贴图流程、多个窗口的选择与 Esc 移除，证据位于 `artifacts/pin-verification`。

截图光标使用嵌入的双层 CUR 资源，深色外沿配白色内芯，覆盖十字、移动、四种缩放方向及文字模式。`scripts/create-cursors.ps1` 可重新生成资源；通过 [WPF Cursor(Stream, Boolean)](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.cursor.-ctor?view=windowsdesktop-10.0#system-windows-input-cursor-ctor(system-io-stream-system-boolean)) 加载并启用 DPI 缩放，发布程序无需旁置光标文件。

系统托盘直接使用 `Shell_NotifyIconW` 和原生弹出菜单，程序本身不依赖 WinForms。集成测试中的外部接收器使用 WinForms，以验证跨 UI 框架的 OLE 文件拖放和独立进程的滚轮接收。

透明截图栏使用 `WH_MOUSE_LL` 监听展开热区中的滚轮，隐藏后释放监听。原生回调只判断物理范围并投递输入，滚动和布局由 WPF Dispatcher 执行；不拦截点击，拖放、菜单和其他工具窗口保留自身输入。卡片隐藏滚动条但保留横向滚动范围。

顶部渐变阴影由单独的 `WS_EX_TRANSPARENT` 分层窗口绘制，放在截图卡片下方；较深的宽椭圆径向渐变形成两侧略高、中间下沉的柔和弧形底边，整层允许点击穿透。显示、隐藏、关闭与截图栏同步，淡入淡出沿用当前动画透明度，截图和全屏避让时一起隐藏。原生分层窗口的透明输入规则参见 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows)。

## 参考

使用的系统行为参见 Microsoft 文档：[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)、[Per-monitor DPI](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)、[BitBlt](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-bitblt)、[WPF Drag and Drop](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/drag-and-drop-overview)。

收起时顶部没有可见触发块。`EdgeWindow` 是主屏顶部居中、宽度约为屏幕三分之一、4 DIP 高的透明点击穿透窗口；仅可见时轮询物理鼠标坐标，在这段区域停留约 180 ms 后请求展开，离开或按住鼠标按钮时重置。顶部两侧不触发展开，方便操作浏览器标签和窗口按钮；展开后的截图栏仍覆盖完整屏幕宽度。展开、截图和全屏避让时暂停，重新显示时恢复；关闭时停止定时器。透明窗口不依赖鼠标进入事件，不拦截浏览器顶部点击或拖动。

展开后的截屏、锁定、设置位于右上方，清空暂存位于右下方；提示文字居中显示在挂绳上方。右上角收起按钮已移除，解锁后移出热区自动收起。清空暂存使用带数量的模态确认窗口，取消、关闭或 Esc 均保留图片；弹窗期间暂停自动收起并阻止截图快捷键进入采集。

设置页采用自适应高度的双列布局，移除滚动容器和左下角常驻提示。开机启动开关立即写入或删除当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 中本应用的值，不随取消其他设置而回退；引用带空格的 EXE 路径并附带 `--startup`，登录启动时不展示首次截图栏，也不会因重复启动弹提示。运行机制参见 [Microsoft Run / RunOnce 文档](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)。`-Settings` 在独立测试注册表键验证持久化、移除、失败恢复和应用设置页，并以独立缓存验证清空确认；不会修改真实开机启动项或用户截图库，证据位于 `artifacts/settings-verification`。
