# 图片文字 OCR

新增依赖为本地 Tesseract 5，原因是原脚本的图片识别需要独立 OCR 引擎。应用通过独立进程读取文字，不把图片发给在线 OCR 服务。识别后的题干和选项会作为题目文本发送给已配置的 AI。

本次便携包继续不携带 .NET、WebView2 或 Tesseract 引擎。在另一台电脑使用 OCR 时，需额外安装 Tesseract 和两种语言数据；普通文字题不依赖 OCR。

## 安装

Windows 安装入口：[Tesseract 官方列出的 Windows 构建来源](https://tesseract-ocr.github.io/tessdoc/Downloads.html)、[UB Mannheim Windows 构建](https://github.com/UB-Mannheim/tesseract/wiki)。安装在新的专用目录，不要将整个项目文件夹作为安装目录。

也可通过已核对的 winget 包安装（安装器版本以源返回为准）：

```powershell
winget install --id UB-Mannheim.TesseractOCR --exact --source winget
```

安装完成后确保 `tesseract.exe` 同目录的 `tessdata` 下有 `chi_sim.traineddata`、`eng.traineddata` 和 `configs/tsv`。语言数据来源：[官方 tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast)，仅使用 LSTM 模式。

本机此次使用官方链接下载的 5.5.3.20260724 构建，由已有 7z 解包到 `%LOCALAPPDATA%\Tesseract-OCR`；语言数据为官方 tessdata_fast，未运行安装器或改变 PATH。实际准备命令形式为：

```powershell
# 在新的空目录准备独立引擎；安装包来自上方 Windows 构建页面。
7z x .\tesseract-setup.exe "-o$env:LOCALAPPDATA\Tesseract-OCR" tesseract.exe '*.dll' 'tessdata/configs/tsv' '-x!$PLUGINSDIR/*'
# 将 chi_sim.traineddata 和 eng.traineddata 放入该目录的 tessdata。
& "$env:LOCALAPPDATA\Tesseract-OCR\tesseract.exe" --list-langs
```

若使用安装向导，选择相同的专用引擎目录并安装简体中文、英文语言包即可；不要求用户另行安装 7z。

## 配置与验证

程序依次检测 EXE 旁 `ocr/tesseract.exe`、当前用户的 `Tesseract-OCR`、Program Files 中的 `Tesseract-OCR`、PATH。若自行安装到其他目录，在正在使用的 `.env` 中增加：

```dotenv
CX_OCR_PATH="D:/OCR/Tesseract-OCR/tesseract.exe"
```

相对路径以 `.env` 所在目录为基准。每次答题流程重新检测配置，修改路径不需重启；设置页保存 AI 参数会保留该项。固定调用 `chi_sim+eng --oem 1 --psm 6 tsv`，不把配置值作为 shell 命令执行。[CLI 说明](https://tesseract-ocr.github.io/tessdoc/Command-Line-Usage.html)。

```powershell
& 'D:/OCR/Tesseract-OCR/tesseract.exe' --version
& 'D:/OCR/Tesseract-OCR/tesseract.exe' --list-langs
```

需能看到 `chi_sim`、`eng`。开发验收 `scripts/dev.ps1 smoke` 现在包含真实 OCR 测试，需要先准备该引擎及语言包。

## 运行约束

- 对可见题目图片按 DOM 位置识别，题干和各选项不混在一起；未识别图片不会被忽略。
- 浏览器 Canvas 读取受跨域限制时，原生端仅下载允许的学习通/超星 HTTPS 图片，校验每次重定向、8 MB 限制；不携带 Cookie 或模型 Key。需要登录授权的图片可能无法下载，届时暂停该题。
- 字体映射优先；失败时逐文本节点按实际计算字体渲染图片，最多 1000 字/节点，超限保持待处理。
- 每次最多处理 16 项，余项下轮继续。引擎单次限时 25 秒，停止/导航时取消；仅结束本次创建的 OCR 进程。
- 只缓存通过置信度门槛的图片结果，内存最多 128 项。临时图片位于资料目录内独立 `ocr-work/GUID`，调用结束即删除；不访问或清理 Cookie 文件。
- 置信度只是 OCR 引擎估计，不能保证题意或答案正确。公式、示意图、复杂图文题仍应人工核对。

本机可执行文件由以下安装包准备：SHA-256 `BEE9E3434BD94FD65387D9BE28CD467A41F61B1275383B55B0F59A1331270AE4`。语言数据 SHA-256：`chi_sim=A5FCB6F0DB1E1D6D8522F39DB4E848F05984669172E584E8D76B6B3141E1F730`；`eng=7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2`。该构建安装包的签名时间验证在本机返回证书过期，未宣称签名校验成功；实际从上述官方仓库发布链接取得并解包，版本命令及真实样图验收通过。
