# 安全说明

## 报告问题

请**不要**开公开 issue。用 GitHub 的
[Security Advisory](https://github.com/FASUZO/VideoCompare/security/advisories/new)
私下报告，或发邮件到 <fasuzo@qq.com>。

## 范围

这个程序是本地桌面工具，不联网、不上传任何数据。与新版本相关的问题包括：

- 处理恶意构造的视频文件导致崩溃或任意代码执行（大部分解码工作由系统 Media Foundation 与 `ffmpeg` 完成，
  与其相关的漏洞请一并向上游报告）
- 文件操作（重命名 / 移动 / 删除）越权或误删（当前设计：本机磁盘走回收站，网络共享直接删除）
- 加载 `ffmpeg` 时的搜索路径劫持（程序按 `数据目录\ffmpeg\bin` → `数据目录\ffmpeg` → `PATH` 顺序查找）

## 不在范围

- 需要本机已被入侵或需要管理员权限才能触发的问题
- SmartScreen / 杀软误报（未签名程序的正常现象）
