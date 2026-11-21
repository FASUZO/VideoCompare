# 贡献指南

感谢关注。这个项目很小，改动请尽量保持「小、可验证、不引入新依赖」。

## 环境要求

| 项 | 要求 |
| --- | --- |
| 系统 | Windows 10 / 11（x64） |
| 编译 | .NET Framework 4.x 的 `csc.exe`（系统自带），不需要 Visual Studio |
| 可选 | `ffmpeg` / `ffprobe`（只有转码兜底、网络大文件片段播放需要；不装也能跑） |
| 可选 | PowerShell 5.1+（构建脚本、测试脚本） |

## 语言约束（重要）

源码由系统自带的 `csc.exe`（.NET Framework 4.x 版本）编译，**只支持 C# 5**。请不要使用：

- 字符串插值 `$"{x}"`
- 空条件运算符 `?.` / `?[`、`nameof`、表达式体成员 `=>`（方法体）、`out var`、元组、模式匹配
- 局部函数、`async`/`await` 之外的现代语法糖（本项目内部的异步一律用 `Task` + 回调或同步等待）

需要 C# 6+ 之前就能写的写法；`var`、`delegate {}`、匿名类型是安全的。

## 构建与测试

```bat
build.bat            :: 编译到 dist\VideoCompare.exe
build.bat -Zip       :: 再打一个 dist\video-compare-<ver>-win-x64.zip
```

```powershell
./_test/make_media.ps1                                    # 生成测试样片（需要 ffmpeg 在 PATH）
./dist/VideoCompare.exe --selftest "_test/media/01_h264_25fps.mp4" "_test/media/02_hevc_aac.mkv"
./dist/VideoCompare.exe --uicheck  "_test/media/01_h264_25fps.mp4" "_test/media/03_prores422.mov"
./dist/VideoCompare.exe --seektest "_test/media/07_long_90s.mp4" "0.2,0.85,0.05,0.6"
./dist/VideoCompare.exe --clicktest "_test/media/07_long_90s.mp4" "0.9,0.1,0.5" 350
```

四个开关都会把结果写到 `dist\*.txt`，末行为 `RESULT_OK` 才算通过，CI 就是这么判的。
提交 PR 前请至少跑通 `--selftest` 与 `--uicheck`。

## 提交信息

用一句话说清「改了什么/为什么」，首行不超过 72 字符，需要时正文补充细节：

```
修复进度条点击后刻度回弹

Slider 的类处理器会把 PreviewMouseDown 标记为 Handled，
普通订阅收不到，导致 draggingSeek 一直是 false……
```

## Pull Request

1. 一个 PR 只做一件事
2. 说明动机与验证方式（贴 `RESULT_OK` 那几行即可）
3. 涉及界面改动的，附一张截图（`_test/shot.ps1` 可直接抓窗口）
4. `build.bat` 必须 0 警告

## 目录说明

| 路径 | 内容 |
| --- | --- |
| `src/VideoCompare/` | 全部源码（C# 5） |
| `build/` | 图标等构建资源 |
| `dist/` | 构建产物（不进版本库） |
| `_test/` | 测试样片生成与自动化测试脚本 |
| `legacy/` | 早期网页版实现与打包尝试，仅作历史参考，不再维护 |
