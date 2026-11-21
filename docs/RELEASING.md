# 发布流程

## 版本号

`MAJOR.MINOR.PATCH`，与 `src/VideoCompare/Properties/AssemblyInfo.cs` 里的
`AssemblyInformationalVersion` 一致（构建脚本从这里读版本号）：

- `MAJOR`：界面/交互大改，或数据（设置文件）不兼容
- `MINOR`：新功能
- `PATCH`：修 bug

## 步骤

1. 改 `AssemblyInfo.cs` 的 `AssemblyVersion` / `AssemblyFileVersion` / `AssemblyInformationalVersion`
2. 在 `CHANGELOG.md` 顶部加一节（`## [x.y.z] - YYYY-MM-DD`，分类：新增 / 修复 / 变更）
3. 本地跑一遍：`build.bat -Zip` + 四个自检开关（见 `CONTRIBUTING.md`）
4. 提交后打标签并推送：

   ```bat
   git commit -am "发布 x.y.z"
   git tag -a vx.y.z -m "vx.y.z"
   git push origin main
   git push origin vx.y.z
   ```

5. 标签推送后 CI 会自动：编译 → 生成样片 → 跑四个测试 → 产出
   `video-compare-x.y.z-win-x64.zip` → 创建 GitHub Release（附 zip，自动生成 release notes）。
   若 CI 失败，Release 不会创建，先修 CI。
6. 人工补一句「升级注意」到 Release 说明（例如需要新版 ffmpeg、设置文件位置变化）。

## 产物命名

`video-compare-<version>-win-x64.zip`，内含 `VideoCompare.exe`、`README.md`、`LICENSE`、`CHANGELOG.md`。
包内 exe 文件名用 ASCII（`VideoCompare.exe`），窗口标题仍是中文「视频对比工具」。
