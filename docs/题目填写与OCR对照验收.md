# 题目填写与 OCR 对照验收

日期：2026-10-22。范围为题目识别、答案生成、填写读回、保存/提交和分页，不更改原脚本或真实模型密钥。

## 原脚本对照矩阵

原文件：`学习通刷课助手 meikendesu with codex 破解版.user.js`。原始 SHA-256 为 `97A5FC1F324E1887A8C8D08C650856AD5BE2B47340CBC924E4DCDD46D6F5EB23`。

| 原脚本路径 | 当前实现和核验 |
| --- | --- |
| Solver.readFromDOM / 各模块容器查询 | 补齐 examQ、examQuestion、subject-item、exam-paper-question、question-item、quesItem、quesBody、mark_table 等根容器。拆分包含多道题的包装层，只保留同一道题的一层表示；隐藏模板排除。 |
| _findNearestMarkName / _findNearestTypeTit / _detectType | checkbox 优先；mark_name、aria-label 和 type_tit 分组标题识别多选。singleQuesId 不再强行判为单选；邻接标题有界查找，不跨过另一题借用标题。 |
| _extractQuestion / _cleanQuestionText | 按原脚本题干选择器优先级取可读文字，而非 CSS 逗号列表中最先出现的附注。保留原始可读题号和分值，不将选项拼成题干；无有效题干则阻止提交。 |
| _extractOptionPairs / _extractOptions / _cleanOptionText | 补齐 textDIV、eidtDiv、label.fl、answerBg label、answerList li、Zy_ulBottom li、ul li .after 等布局；选项文字与点击元素共用一个按 DOM 排序的列表，去掉字母前缀。支持选项父容器/input 的无障碍文字。 |
| 原选项文字去重 | 改为按实际选项行去重。两个选项文字相同仍是两个位置，不能按文字合并。隐藏模板不占索引；CSS 隐藏 input 的可见 label 仍可填写。 |
| 字体路径 / recognize | 继续保留 Typr、字形路径、MD5 后 8 位及 Table.json 映射；优先可访问 aria-label，再字体映射，最后本地 OCR 回退。不执行原 user.js。 |
| recognizeByOCR / _ocrImage / 字体截图回退 | 本地 Tesseract 5，chi_sim+eng；按题干图片、各选项图片、未映射字体文本节点分别识别。结果按原资源指纹绑定，图片或文字变化即失效；不把全部图片文本混入一道题干。 |
| AnswerModule.askAI / prompt / _parseAIAnswer | 保持项目已有严格 JSON 协议，模型返回从 0 开始的选项索引及按空数排序的 blanks；不把解释中的字母误当答案，不使用原脚本“无法确定也必须猜测”的提示。 |
| matchAnswer / smart matching / 判断映射 | 明确索引与原选项文字一同发送模型，判断题按“对/错”等实际选项索引填写；不使用可能串题的模糊文字匹配。 |
| _isPairSelected / _selectOptions | checked、aria-checked、is-checked、checked、selected、active、cur 读回；支持实际内层 after/onclick 控件。已有目标状态不重复点击，多选中多余选项取消后核验；不强改 checked 伪造网页已接受。 |
| _fillBlanks | 普通 input/textarea 按空顺序写入并触发 input/change；已相同则不重复写。延续 UEditor 多空 iframe 的实例、正文和提交表单三方同步读回，空数不符或只读时拒绝。 |
| _clickSubmit（题内） | 核验当前页全部题目后逐个执行题内保存/提交；按题 ID 和答案去重。确认授权复用原生流程。保存或提交后下一轮重新读回，不能连续点击多题后忽略页面重绘。 |
| _clickFinalSubmit（整份作业/试卷） | 逐题控件与题目容器外的交卷控件分开；各页全部核验后才点整份提交，整组提交去重。支持原脚本自定义按钮类、保存并提交等文字。 |
| _confirmSubmitDialogs | 保留本次提交后才出现的弹窗及祖先框架确认；未答/漏答提示不确认。逐题提交被拒绝后不继续翻页或交卷，未知新弹窗等待超时后也阻断。 |
| _trySwitchToFullPaperView | 自动尝试整卷预览、整卷浏览、查看整卷、整卷模式；等待正文变化，不在模式切换时处理旧题。每个文档只尝试一次。 |
| _processExamPaginated / 题号导航 | 有完整题号栏的同文档分页，从中间页进入也补齐其他页；相同题干的不同平台题 ID 不合并。题号先变化、正文异步加载时等待；全部题号完成才允许交卷。 |
| _processExamList / 作业循环 / AutoRunner 缓存 | 每个文档的稳定题目 ID 对应一个生成答案缓存；页面重绘丢失勾选时复用答案恢复，不重复请求模型；保持填写、读回、保存、翻页、交卷各阶段分离。 |
| stop / 导航取消 | 继续使用原生取消令牌、文档身份及版本校验。导航或停止取消 OCR 与模型流程，拒绝将旧图片或旧答案写到新页面。 |

## 已复现的缺陷与回归

新增测试均使用本地虚构题目，不提交真实课程：

1. 嵌套包装把两题合并、重复统计；相同文字选项被错误合并。
2. 原脚本自定义选项布局漏读，分组多选被 singleQuesId 误判。
3. 普通填空重复触发 change；自定义选项状态已经正确仍被反选；内层事件未触发。
4. 同 ID 页面重绘未恢复答案或重复请求模型。
5. 分页只处理当前页就交卷；从第 2 题进入漏掉第 1 题；相同题干不同 ID 串题；异步切页时把旧正文重复发送给模型。
6. 图片题无 OCR 任务；图片选项与题干未分开；字体 OCR 回退缺失；资源变化继续复用旧识别结果。
7. 没有整卷模式切换；题干优先级、题干/选项父容器的 aria-label 漏用；邻接 mark_name 类型丢失。
8. 多个逐题“提交答案”只点第一题，或逐题未完成就点全局交卷；网页提示未答题后仍试图继续。

测试还覆盖 101 题不截断、超过 1000 题明确阻断、空 OCR/低置信度拒绝、配置重读、缺失引擎及取消不留图片。真实引擎样图测试验证中文文字、英文题干和图片选项，识别后的选项能实际填写并读回；中文样图平均置信度约 94.7%。

## 改动文件

- `src/Desktop.App/Assets/study-agent.js`：容器、题型、选项、身份、OCR 绑定、幂等填写、分页、逐题/整卷提交。
- `src/Desktop.App/MainWindow.Study.cs`：生成答案缓存与重新读回、模式切换、OCR 调用、逐题提交和分页调度。
- `src/Desktop.App/Browser/StudyBrowser.cs`、新增 `StudyBrowser.Ocr.cs`：原生 OCR、逐帧调用及提交确认阻断。
- `src/Desktop.Core/StudyModels.cs`、新增 `OcrClient.cs`：完整题数/分页状态、引擎发现、受控进程、TSV 置信度、内存缓存和临时文件清理。
- `src/Desktop.App/SmokeTest.cs`、新增 `SmokeTest.QuestionParity.cs`、新增 `tests/Desktop.Core.Tests/OcrTests.cs`：对照回归与实际 OCR 样图。
- `src/Desktop.App/Desktop.App.csproj`、`scripts/dev.ps1`、`.env.example`、README/TASKS 及本说明：分发 OCR 说明、扩大单阶段验收时间、配置模板与验收记录。

## 验证步骤

1. 安装 .NET 10 Desktop Runtime x64、WebView2、Tesseract 及中英语言包；开发构建另需 .NET 10 SDK。
2. 执行 `scripts/dev.ps1 test`，核心测试应全部通过。
3. 执行 `scripts/dev.ps1 smoke`，包含真实 Tesseract 的 seed/verify 两阶段以及隔离的假登录资料。
4. 执行 `scripts/dev.ps1 package`、`scripts/dev.ps1 smoke -Published`，核对发布包的相同回归。
5. 查看 `question-parity-*.json` 验收结果。清理前后逐一比对真实资料和 .env 的文件哈希。

最终运行结果与包校验另记于 `question-parity-verification.json`，不以中途通过记录代替最终包验收。

最终结果：41/41 核心测试通过；源码两阶段 `4d888611ca194f79b2037d85bfdc88ed`、发布程序两阶段 `d93f764700284b4fb1300cc795978ccb` 均通过。发布程序每阶段包含 31 组检查，另有真实 Tesseract 样图结果 `question-parity-release-ocr.json`；中文置信度 94.700753125%。

发布目录 `artifacts/portable/current` 为 21 文件、2.15 MB。ZIP 为 851122 字节，SHA-256 `20C3292D7CB3D0FA64A74F916825B5343F25C3F9D25E5405DDA16777A224C78F`；ZIP 的 21 项与部署文件逐一哈希一致，网页代理与源码一致。原脚本哈希不变。

清理已知旧构建和生成缓存 28 个目标、586.98 MB，失败 0；清理前基线中的 10941 个真实资料、诊断资料副本和配置文件逐一哈希不变。保留 Cookie、会话、.env 及原有资料备份；本次测试只使用隔离的虚构资料。publish 仅有最新版 ZIP，历史上曾被拒绝删除的旧 ZIP 本次检查已不存在，未重试该删除动作。本机独立 OCR 引擎与语言包在清理后仍可用。

## 仍需注意

- OCR 置信度不代表答案正确；数学公式、几何图、图像语义、低清及复杂混排不能保证识别，需人工核对。本次没有对真实课程调用模型或提交答案。
- 无完整题号栏且不能切换整卷的分页、跨文档题号链接跳转目前明确暂停自动交卷；没有照搬原脚本凭“最多 100 次下一题”推断整卷完成的方式。
- 原生回退不带 Cookie，认证图片或不在允许平台域名内的图片可能识别失败；超过大小、文字或题数上限会保留阻断状态，不静默计为完成。
- 没有平台 ID 的题目只能在当前 DOM 生命周期内稳定识别；完全销毁并重建匿名题目时无法保证复用原答案。不能通过按题干文字合并来掩盖该限制，否则同文的不同题会漏答。
- 保存/提交后网页是否真正持久化、评分或章节完成，以平台反馈为准。网页拒绝提交时暂停该组题，用户需检查并刷新后继续。
- 本机 OCR 引擎独立放在用户目录，不随便携包分发；更换电脑需要安装依赖。原脚本和 Cookie 数据保留。
