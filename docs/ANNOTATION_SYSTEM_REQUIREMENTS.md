# FujiyNotepad 小说阅读与批注系统——核心需求与开发状态

> 本文档是本项目唯一维护型需求清单。保留产品边界、核心交互、数据约束、关键架构和当前进度；不再维护逐条完整验收表、历史 BUG 清单或每轮修复流水账。
>
> 最后整理：2026-11-06
> 仓库：linglin-911/FujiyNotepad
> 当前开发分支：feature/reader-settings-phase4
> 本次文档整理对应的功能代码基准：eb5343546163e89dd0da4e0241abdbd838b89810
> 详细阅读布局架构：docs/reader-text-layout-architecture.md
> 项目数据架构补充：docs/novel-project-architecture.md

## 1. 项目目标与开发约定

FujiyNotepad 小说阅读与批注系统是 Windows 桌面软件，面向长篇 TXT 小说的连续阅读、剧情整理、人物研究和写作素材积累。

开发约定：

1. 开发前先拉取当前分支，阅读本文档并检查实际代码；不要根据旧聊天记录推测实现。
2. 区分 FujiyNotepad 原项目继承功能与本项目新增功能，不得混为一谈。
3. 已有功能经过用户基本编译和冒烟测试、没有明显问题，即视为完成；不要求每轮进行全量回归或逐项完整验收。后续发现具体问题再针对性修复。
4. 优先局部修改。不得随意重做已确认的交互逻辑，不得擅自删除原项目功能。
5. 不随意修改 SQLite 表结构、批注锚点模型、UTF-16 偏移语义或大文件读取架构；确需修改时先说明原因和影响。
6. 阅读器只按需读取和布局可视区域，不得为方便 UI 实现而一次性加载整本小说。
7. 每轮聚焦一个明确目标，完成后至少进行 Windows x64 Release 编译和必要的基本冒烟测试，并更新本文档。
8. 普通开发和修复继续使用当前分支；未经用户同意，不新建分支、不合并分支、不修改 PR。

## 2. 功能归属

### 2.1 FujiyNotepad 原项目继承功能

以下属于原项目已有能力，不计为本项目新增成果：

- 大文件按需读取、字节源、行索引、LineProvider、有界缓存和文本画布。
- 基础文本显示、滚动、复制和键盘导航。
- 原项目曾提供基础选区状态/命令框架，以及基于固定字符列宽的可选自动换行（LineWrapper）。这些只是当前实现的历史技术起点；当前阅读器的鼠标选字交互、统一文字矩形体系和比例字体自动换行已经由本项目重新设计/实现，不能再归为原项目继承功能。
- 文件打开/重载、最近文件、启动恢复、拖放打开。
- 查找、正则查找、过滤/grep、匹配高亮和持久高亮规则。
- 行号、书签、跳转、编码识别与切换、Tab 宽度设置。
- 原有菜单、状态栏、编辑器相关能力及原有应用主题。

原项目本质上是文本编辑器。后续改造成阅读器时，需要逐项判断编辑器功能是保留、改造还是移除；不得因为它不适合阅读器就直接删除，先检查使用情况并与用户确认。

### 2.2 本项目新增并已实现的功能

- 小说项目导入、独立项目目录、原文工作副本及项目恢复。
- 阅读模式、比例字体阅读布局、全局阅读设置和 Paper/Warm/Night 阅读主题。
- **阅读器文本选择交互重做：** 不沿用原编辑器的普通点击/双击选词交互；采用拖动选字、拖动阈值、字符/字形簇命中、UTF-16 边界映射，以及从文字矩形外拖入时记录首次进入矩形交点 A 的规则。
- **TextLayoutRect 统一文字矩形体系：** 以实际文字范围矩形作为选区背景、鼠标命中、小批注原文命中和批注标记定位的共同几何基础；这是本项目阅读器的底层设计，不是原编辑器功能。
- **比例字体自动换行重做：** 使用本项目的 ITextLayoutProvider / Win2D / DirectWrite 排版路径，根据实际字体度量生成视觉行并保留原文 UTF-16 映射；原项目固定字符列宽的 LineWrapper 仅作为旧布局兼容路径保留。
- 章节识别、持久化章节索引、章节目录、章节变化检测与归档保护。
- 小批注、章节批注、大批注三套独立数据和界面。
- 阅读器原文批注标记、选区菜单、小批注输入浮层、统一右侧批注面板。
- 大批注多章节辅助写作工作区。
- 阅读器启动、项目恢复、文件打开、索引、渲染和异常诊断日志。

以上已有功能均按用户此前的基本测试结果视为完成。后续界面现代化不得重新开发这些功能；只在实际出现问题时修复。

## 3. 小说项目与章节索引

### 3.1 项目文件组织

每部小说是独立项目，典型内容包括：

- novel.txt：项目内原文工作副本。
- project.json：项目元数据。
- annotations.sqlite：章节索引、批注及相关数据。
- 备份目录和外部编辑会话目录。

导入 TXT 时创建项目目录并复制原文。批注数据不得写入或污染小说正文。章节是 SQLite 中的逻辑范围，不把一本小说物理拆成多个章节文件。

### 3.2 章节索引

- 后台逐行识别章节，不加载全文字符串副本。
- 章节以稳定 ChapterId 作为身份；章节序号仅用于显示顺序。
- 保存标题、全文起止偏移、正文起始偏移、正文 SHA-256、文档/解析器版本和活动/归档状态。
- 项目打开时先比较原文文件指纹；未变化则直接复用 SQLite 索引。
- 原文正文变化且章节结构不变时可更新对应索引；章节新增、删除、改名或结构不确定时生成变化预览，用户确认后再更新。
- 删除章节时归档，不级联删除章节批注或小批注锚点；不得静默把旧批注迁移到其他章节。
- 章节目录按原文顺序显示标题，支持搜索、点击跳转和定位；使用虚拟化列表适应上千章小说。

## 4. 三种独立批注

三种批注必须是独立实体、独立正文和独立分类。不得把一种批注自动变成另一种批注的子项；修改或删除一种批注不得级联改变其他类型的内容。

### 4.1 小批注

- 绑定原文中的一个或多个文本片段，用于零散想法、剧情细节、人物表现和素材提取。
- 一条小批注可关联多个章节；每个原文锚点必须完整位于且只属于一个章节。跨章节内容必须拆成多个锚点。
- 不同批注允许引用相同原文；允许重叠、嵌套。
- 支持新建、追加原文位置、编辑、删除、分类管理、搜索、列表详情和逐处跳转。
- 阅读器在关联原文下方显示虚线标记；点击原文范围可显示对应批注。重叠或嵌套批注应能一并查看。
- 右侧列表按当前章节筛选；一个多锚点批注可以出现在其关联的多个章节中。
- 锚点保存全文 UTF-16 起止偏移、摘录、ChapterId、章节内相对偏移、前后文、章节正文指纹、文档版本和失效状态。
- 锚点恢复优先使用稳定 ChapterId 和章节内相对偏移；正文变化时只在原章节内结合摘录、上下文和相对位置查找。无法唯一确认时保留失效状态并交由用户处理，禁止跨章节猜测。

### 4.2 章节批注

- 绑定一个具体章节，不绑定某个文字区间；一个章节允许多条章节批注。
- 使用独立实体和独立分类，支持新建、编辑、删除、搜索、分类筛选和章节筛选。
- 新建入口包括右侧面板按钮和阅读器正文右键；右键入口按点击位置所属章节创建。
- 编辑时允许修改所属章节，但只改变该条批注的 ChapterId。
- 章节删除或归档时保留历史章节批注，不得级联删除或静默改绑。

### 4.3 大批注

- 绑定至少两个连续的活动章节，用于阶段剧情总结、人物成长线、势力变化和伏笔整理。
- 拥有独立标题、正文、分类、管理列表和编辑工作区。
- 采用左右分栏：左侧参考资料，右侧编辑大批注正文；两侧宽度可调整。
- 左侧按章节显示小批注和章节批注，支持分类筛选、搜索、展开/折叠、查看内容及原文/章节跳转。
- 参考资料目录严格只有两层：章节标题 → 批注。类型、分类、原文数量、摘要等是批注自身的平级信息，不得成为额外树层级。
- 参考资料只读，不复制正文、不建立从属关系；修改或删除参考批注不得级联修改大批注。

## 5. 阅读器布局、自动换行与文字矩形——核心底层设计

本节定义本项目重做的阅读器排版和文本选择体系。原编辑器的基础选区状态、固定字符列自动换行只能视为历史技术起点；当前阅读器的鼠标选字、比例字体排版和文字矩形几何由本项目重新设计实现。文字矩形不是某个 UI 的装饰效果，而是选区、命中、批注定位共同依赖的底层模型。任何 UI 优化都不得绕过或破坏它。

### 5.1 三种坐标必须分离

1. 文件字节偏移：只用于文件读取和索引。
2. 原文 UTF-16 偏移：章节、批注锚点和文档定位的持久化坐标。
3. 布局/屏幕坐标：只用于当前排版、绘制、命中和浮层定位，不得持久化为批注位置。

布局接口返回的字符索引必须对应原始逻辑行的 UTF-16 偏移。字体、字号、行距、正文宽度、窗口大小和阅读主题变化，只影响临时布局，不得改变原文偏移或 SQLite 锚点。

### 5.2 TextLayoutRect 统一文字矩形（最重要）

**这是本项目阅读器文本选择的基础设计。** 当前选字不是把原编辑器的选择功能换一种颜色或外观，而是围绕实际文字矩形重新设计鼠标交互和几何计算。

- 文字矩形按原文字符/字形簇形成，是实际文字占用的布局范围，不要求按字形像素轮廓裁切；因此点击矩形内部即可命中对应文字，不要求鼠标落在有颜色的字形像素上。
- 文字矩形是实际文字带，不是整行布局框；必须排除行间距、段间距和额外留白。
- 水平范围来自实际字符/字形簇的 LayoutBounds advance；垂直范围根据视觉行 Baseline 与字号比例计算，不得直接把包含 leading/行距的 LayoutRowHeight 当作文字高度。
- 同一组无额外 padding 的 TextLayoutRect，必须同时用于：
  1. 文字选中背景绘制；
  2. 普通鼠标文字命中；
  3. 小批注原文点击命中；
  4. 拖动选字进入文字区域的检测。
- 批注虚线相对于同一文字矩形的下边缘定位。连续字符的水平范围应连续，不得出现额外间隙。
- 不允许为了方便点击而单独扩大命中矩形，也不允许选中背景向行间空白扩张。**文字矩形、鼠标交互命中范围、选中背景范围必须完全一致。**
- 点击位置按字符/字形簇的前后半边决定 UTF-16 边界；必须保留完整 shaping cluster，不得把代理对或多 UTF-16 单元字符拆开。
- 从文字矩形外开始拖动时，记录鼠标移动线段首次进入文字矩形的交点 A；即使 A 出现在 4 DIP 拖动阈值之前也必须保留。超过拖动阈值后，以 A 对应的字符边界作为选区起点。
- 阅读器只通过鼠标拖动选字；单击不产生选区，不执行双击选词，不显示阅读器文本插入光标。

### 5.3 比例字体自动换行、布局实现和性能

- 上游原有的 WordWrap / LineWrapper 是按固定字符显示列数换行，适用于旧固定宽度布局；它不是当前比例字体阅读器的核心换行实现。
- 当前阅读器比例字体路径由本项目重做：TextLayoutEngine.Proportional.cs 调用 ITextLayoutProvider 创建单行原文布局，Win2DTextLayoutProvider 使用 DirectWrite/CanvasTextLayout 的真实字体度量和 WordWrapping.Wrap 生成视觉行；同时保留视觉行到原文 UTF-16 范围的映射。
- 旧 LineWrapper 只作为旧固定字符单元布局的兼容/回退实现保留，不应把它描述成当前比例字体自动换行的实现。
- 使用现有 Win2D/DirectWrite 比例字体布局后端和 TextLayoutEngine，不另建第二套布局引擎。
- TextCanvas 是绘制和指针交互入口；Presentation 层只依赖 UI 无关的布局契约。
- 只为可视行和少量相邻行创建布局对象；布局缓存必须有界。字体/字号/行距/宽度变化时按需重排可视区域，不全书同步重排。
- 保留大文件按需读取、LineProvider、LineIndexer、字节源和现有缓存策略。
- 阅读区采用连续滚轮滚动；不提供水平、垂直滚动条。主要导航通过滚轮、章节目录和批注跳转。

详细布局契约、坐标转换和引擎职责见 docs/reader-text-layout-architecture.md。

## 6. 全局阅读设置

阅读设置全局生效，不属于某个小说项目，保存在用户 settings.json 中，不写入 project.json 或 SQLite。

- 字体：系统已安装字体，默认 Microsoft YaHei。
- 字号：DIP。
- 行距：倍数。
- 正文宽度：固定最大宽度或自适应宽度。
- 阅读主题：Paper、Warm、Night。
- Paper/Warm 只影响阅读表面；Night 阅读主题临时联动应用 Dark 外观，但不得覆盖用户保存的应用主题偏好。
- Windows High Contrast 优先于普通阅读主题。
- 正文区域整体居中，内部文本左对齐；不擅自添加首行缩进或删除原文空白。
- 阅读设置变化后保持当前原文视口锚点，避免跳回文件开头。

## 7. 阅读区快捷菜单与小批注输入浮层

### 7.1 选区菜单

- 鼠标拖动选中文字并松开后，显示紧凑 Popup 工具条，只包含“复制”和“小批”，图标在上、文字在下。
- 菜单颜色依据阅读画布背景明暗切换，不依据应用主题。
- 菜单正常显示在第一条可见选中矩形上方，锚点取第一条实际 TextLayoutRect 的顶部；顶部空间不足时显示在最后一条可见选中矩形下方，箭头朝上指向最后一条矩形。
- 菜单定位必须使用实际绘制的选区矩形及同一套坐标变换，不能使用鼠标释放点或整行高度估算。保持紧凑约 3 DIP 偏移，不得通过放大固定间距掩盖坐标错误。
- 复制后清除选区；打开小批注编辑器时保留选区用于锚点校验，关闭或保存成功后清除选区。

### 7.2 小批注输入浮层

- 使用 WinUI Popup，非模态，不启用 LightDismiss；外部点击不会关闭弹窗。
- 深灰色卡片背景 #303030，圆角外层；内部编辑区与卡片使用同一个不透明背景画刷，零边框、无蓝色焦点底线，视觉上融为一体。
- 编辑器最大约 660×328 DIP，按阅读视口收缩；正文区可纵向滚动，卡片外层不得出现横向滚动条；X 和保存按钮始终可见。
- 右上角为小型圆形关闭图标；保存为紧凑药丸按钮。只保留多行正文、保存和 X，不显示标题输入框或底部取消按钮。
- 编辑器位置依据选区最后一条可见实际文字矩形的底边，不使用鼠标释放点；优先放在选区下方，超出阅读视口时贴近可视范围底部，不自动滚动阅读区域。
- 打开后自动获取输入焦点；点击编辑区外部时失去焦点但不关闭弹窗；滚轮翻页不改变焦点；重新点击编辑区恢复焦点。
- **当前已知问题及排查状态：** 小批注编辑区和章节批注编辑区都出现光标约闪烁 5 次后停止、并停留在最后可见状态的现象。章节批注编辑区 `ChapterAnnotationEditorContent` 是 MainWindow.xaml 中未套用自定义模板的普通 WinUI TextBox；小批注编辑区则由 MainWindow.AnnotationPane.cs 动态创建并使用 `AnnotationComposerTextBoxStyle`。两个不同模板/创建路径出现相同现象，说明不能继续把根因归结为小批注自定义模板。
- **Windows 环境确认（2026-11-06）：** 用户只读查询 `HKCU\Control Panel\Desktop\CaretTimeout`，结果为 `REG_DWORD 0x1388`，即十进制 5000 ms。该值与 Windows 原生插入光标约 5 秒后停止闪烁的已知行为吻合；Raymond Chen 的 Windows 开发说明指出系统 caret 默认会在一段时间后停止闪烁以减少持续定时器/功耗，Mozilla Bugzilla 也明确记录该注册表值默认 5000 ms。该注册表项不是 WinUI TextBox 的独立属性，也没有找到可供单个 WinUI TextBox 设置 timeout 的公开 API。
- **为何浏览器可能不同：** Chromium Blink 的 `FrameCaret` 自己维护 `HeapTaskRunnerTimer`，并通过 `PaintCaret` 绘制 caret；这与直接依赖 Windows 系统 caret 的控件实现不同。因此浏览器网页输入框持续闪烁并不能证明 Windows 系统 caret 没有 5 秒限制，也不能据此认定 WinUI 模板有缺陷。若要精确比较，应区分浏览器网页输入框与浏览器地址栏。
- **产品决策：** 不要求用户修改全局 Windows 注册表。`SetCaretBlinkTime` 只调整系统 caret 的闪烁间隔，不是 per-control timeout 开关；微软文档明确建议普通应用尊重用户设置，不应擅自调用该 API 改写全局闪烁速度。
- **应用内方向（WPF 式双层光标，原型已加入、待 Windows 验证）：** 保留 WinUI TextBox 负责文本存储、键盘导航、选区、中文 IME 和输入事件；上层 XAML 视觉元素负责可见光标和独立闪烁。参考 WPF `CaretElement`，不只调用 `HideCaret`：创建 1-bit 空白位图，通过 `CreateCaret`/`ShowCaret` 建立透明 Win32 系统 caret，再用 `SetCaretPos` 同步系统辅助光标位置；可见层使用 `SelectionStart` 和 `GetRectFromCharacterIndex` 定位，使用 `GetCaretBlinkTime` 作为自绘定时器的闪烁间隔。不得调用 `SetCaretBlinkTime`，不得修改用户注册表。
- **独立测试窗口：** 新增 `CaretTestWindow.xaml` / `.xaml.cs`，从 View →「光标技术测试…」打开。提供“原生光标（对照）”和“WPF 式双层光标（自绘）”两种模式；显示当前 SelectionStart/SelectionLength、Text.Length、光标矩形 DIP、窗口客户区像素坐标和 Win32 caret 创建/定位状态；提供清空与填充长文本操作。该窗口是临时技术验证工具，不接入项目/批注数据库，不替换或修改正式小批注、章节批注编辑器。
- **待验证项目：** Windows x64 Release 编译；自绘光标连续超过 5 秒仍闪烁；Win32 空白 caret 不出现可见重影；中文 IME 组合输入/候选窗；方向键、鼠标定位、选区与选区折叠；空文本、文本末尾、末尾换行、混合中英文；滚动、窗口缩放和 DPI；失焦/重新聚焦/关闭窗口后的系统 caret 释放；放大镜/UI Automation caret 位置。若 WinUI 内部重建系统 caret 导致可见重影或位置不同步，先在原型中定位，不直接推广到正式编辑器。
- **第二版原型改进（2026-11-06，待 Windows 验证）：** 参考 WinUI `GetCaretRect` 社区实现与 AvalonEdit 的坐标/滚动处理，光标索引在无选区时直接使用 `SelectionStart`；文本末尾使用最后字符 trailing edge，末尾 CR/LF 单独处理；TextBox 返回的字符边缘先通过 `TransformToVisual(CaretOverlay)` 转换到自绘层，再通过同一字符边缘转换到 RootGrid 计算 Win32 坐标，避免两条路径各自假定坐标原点。诊断增加相邻字符 leading/trailing edge、Overlay/RootGrid 坐标、DPI 和滚动偏移。
- **刷新队列修正：** 原 `updateQueued` 会直接丢弃队列期间到来的更新请求。现在合并 `resetBlink` / `forceRecreate` 标志，并使用低优先级 DispatcherQueue 更新，尽量等当前文本输入与布局过程结束后再读取字符矩形。滚动、尺寸变化和选择变化仍触发刷新。该改进仍需实测确认 WinUI 内部排版完成时机是否足够。
- **参考源码：** AvalonEdit `Caret.cs`（VisualLine/TextLine 坐标、VisualLinesChanged、ScrollOffsetChanged）；WinUI `TextBoxExtensions.GetCaretRect`（字符边缘和末尾换行处理）；Avalonia `TextPresenter`（TextLayout/CharacterHit）。这些作为实现参考，不直接引入依赖或移植完整编辑器。
- **第二版实测反馈（2026-11-06）：** 用户反馈与第一版相比没有可感知改善，中文/英文/数字的相对偏移、长文本中光标难定位等问题仍存在。截图诊断显示 `SelectionStart=84`、TextBox Rect=(62,22,h=42 DIP)、Overlay=(62,22)、RootGrid=(84.3,160.3 DIP)、Win32=(147,281 px)、DPI=1.75。蓝色光标实际像素与换算坐标基本一致，说明当前 Transform/Canvas 路径内部自洽，但不能证明 GetRect 返回的矩形等于 WinUI 实际可见插入点；42 DIP 高度也需与实际文字行高对照。
- **重要技术发现：** 微软 `microsoft-ui-xaml` Issue #8207 中 WinUI 控件团队成员说明 TextBox/RichEditBox 的 caret 使用 `DestinationInvert` 绘制。WinUI TextBox 公共 API 属性列表没有 WPF `CaretBrush` 这样的公开光标画刷属性。因此，WPF `CaretElement` + 透明 Win32 caret 的方案不能直接假设能够接管/隐藏 WinUI 内部绘制的可见 caret；`CreateCaret` 成功只证明 Win32 caret 创建成功，不证明 WinUI compositor caret 已隐藏。参见 Issue #8207 和 Windows App SDK TextBox API 文档。
- **第三版仅增加诊断，不预设修复：** 在创建透明 Win32 caret 之前采集一次 `GetGUIThreadInfo`（hwndFocus、hwndCaret、flags、rcCaret）和 `GetCaretPos`，并在可行时将 rcCaret 从 hwndCaret 客户区映射到主窗口客户区。采样会在失焦后重置。注意微软文档警告：传统 edit control 的 `rcCaret` 可能包含方向和 padding 信息，不能单独视为精确插入点；它只用于判断 WinUI TextBox 是否存在 USER caret，以及与 `GetRectFromCharacterIndex` 结果进行交叉诊断。
- **2026-11-06 外观调整：** 默认 TextBox 模板在小批注 Popup 中显示为白色编辑区和蓝色焦点底线，不符合已确认的深灰色一体化卡片设计。因此小批注编辑器恢复紧凑自定义模板，但不再使用透明背景：TextBox、内部 ScrollViewer 和 Popup 卡片共用同一个不透明深灰色 Background；模板仍省略默认焦点下划线视觉状态。该外观调整不宣称能修复 Windows caret timeout。
- **验证状态：** 上述外观调整已提交，尚未完成 Windows x64 编译及视觉实测。焦点获取/失去、Popup 生命周期、保存流程和批注数据均未改变。

## 8. 统一批注面板和界面边界

- 小批注与章节批注共用右侧 AnnotationPane，通过章节选择器和类型切换显示；同一时刻显示所选章节的一种批注列表。
- 默认章节跟随当前阅读视口中占比最大的有效章节；用户主动选择其他章节后暂时固定，关闭并重新打开面板后恢复跟随。
- 章节目录、小批注面板、章节批注面板共用右侧区域，不并排占用多个侧栏。
- 大批注工作区替换阅读器主内容区域，采用独立左右分栏。
- 小批注/章节批注标题在当前 UI 中隐藏；数据库字段保留，编辑旧记录时不得清空原有标题。
- 阅读区快捷菜单和小批注 Popup 的视觉规范仅适用于这两个组件，不自动扩展到章节批注、大批注或主窗口其他控件。

## 9. 当前进度、已知问题与下一步

### 已完成并经用户基本测试

- 小说项目导入、打开、关闭和项目数据独立保存。
- 章节索引、章节目录、章节变化保护。
- 小批注、章节批注、大批注主体功能。
- 比例字体阅读器、全局阅读设置和 Paper/Warm/Night。
- 本项目重做的阅读器拖动选字交互、TextLayoutRect 统一文字矩形、比例字体自动换行、批注标记与矩形范围点击。
- 阅读区选区 Popup 和小批注输入 Popup 的基本视觉与交互。
- **此前生产功能版本** Windows x64 Release 编译通过；用户实测确认选区菜单位置已解决，小批注编辑区焦点获取与失去正常。此后新增的光标测试窗口尚未进行 Windows x64 编译验证。

### 当前唯一明确的交互遗留

- 用户已确认 Windows 用户级 `CaretTimeout` 为 `0x1388`（5000 ms）；产品决策是不修改该全局注册表。已新增独立 `CaretTestWindow`，以 WPF `CaretElement` 为参考，测试“WinUI TextBox + XAML 自绘光标 + 空白 Win32 caret + DispatcherQueueTimer”的双层方案；View 菜单增加临时入口。该测试窗口不接入批注数据，不修改正式小批注/章节批注编辑器。
- 小批注 Popup 的编辑区已恢复紧凑自定义模板，并改为与深灰色卡片共用不透明背景；该外观修正尚待 Windows 编译和实测。

### 后续开发方向

1. 先在 Windows x64 环境编译并实测独立光标测试窗口，按 7.2 节逐项验证；原型通过后再讨论是否抽象为正式输入组件。当前不能把光标问题视为已修复。
2. 光标方案验证完成后，再继续阅读器整体界面现代化：先检查现有主窗口和控件，再讨论信息架构、视觉层级和布局方案；分阶段实现，不要一次性大改。
3. 外部编辑的完整差异预览、变更应用、锚点重映射和失败恢复仍属于后续功能，不应误写成已经完成。
4. 启动后的项目列表首页、批注关系、批注导出、统计/时间线/人物专题视图、章节内段落级差异定位等按需另行规划，不属于当前 UI 优化的前置条件。

## 10. 关键代码位置与常用命令

### 代码位置

- 主窗口布局、控件样式：src/FujiyNotepad.WinUI/MainWindow.xaml
- 主窗口生命周期、菜单、阅读器布局：src/FujiyNotepad.WinUI/MainWindow.xaml.cs
- 选区菜单、小批注 Popup、输入焦点：src/FujiyNotepad.WinUI/MainWindow.AnnotationPane.cs
- 小批注列表和数据交互：src/FujiyNotepad.WinUI/MainWindow.SmallAnnotations.cs
- 章节批注：src/FujiyNotepad.WinUI/MainWindow.ChapterAnnotations.cs
- 章节目录：src/FujiyNotepad.WinUI/MainWindow.ChapterDirectory.cs
- 大批注：src/FujiyNotepad.WinUI/MainWindow.BigAnnotations.cs
- 阅读画布、拖动选字交互、矩形绘制/命中：src/FujiyNotepad.WinUI/Controls/TextCanvas.cs
- 布局引擎与比例字体视觉行/选区映射：src/FujiyNotepad.Presentation/TextLayoutEngine.cs、TextLayoutEngine.Proportional.cs、TextLayoutContracts.cs
- 比例字体自动换行、TextLayoutRect 和文字带几何：src/FujiyNotepad.WinUI/Rendering/Win2DTextLayoutProvider.cs
- 旧固定字符列自动换行兼容路径：src/FujiyNotepad.Presentation/LineWrapper.cs
- 项目和批注数据：src/FujiyNotepad.Core/NovelProject.cs、NovelAnnotationStore.cs、NovelAnnotations.cs
- 章节索引：NovelChapterIndexBuilder.cs、NovelChapterIndexReconciler.cs
- 锚点与偏移：AnnotationAnchorMetadata.cs、DocumentTextOffsetMap.cs
- 外部编辑：ExternalEditSession.cs、ExternalEditMarkerCodec.cs

### Windows x64 Release 构建

\`\`\`powershell
git fetch origin
git pull --ff-only origin feature/reader-settings-phase4
dotnet build src/FujiyNotepad.WinUI/FujiyNotepad.WinUI.csproj -c Release -p:Platform=x64
\`\`\`

自动化测试按当前改动涉及的 Core.Tests 或 Presentation.Tests 运行；用户不要求每轮完整回归。涉及跨层或布局契约变更时，再补充相关测试。

---

文档维护原则：只保留仍然有效的需求、设计约束、当前状态和明确的下一步。已经完成的历史修复不再逐轮堆叠；出现新的实际问题时，记录问题现象、真实组件/代码位置和当前处理结论即可。
