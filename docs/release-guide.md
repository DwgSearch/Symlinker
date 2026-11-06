# Symlinker 发布操作指南

本文档记录从源码编译、打包到发布到 GitHub 和 Gitee 的完整流程，供维护者参考。

---

## 1. 环境要求

| 工具 | 最低版本 | 说明 |
|------|---------|------|
| .NET 10 SDK | 10.0.x | 编译 WPF 项目，命令 `dotnet --version` 查看 |
| Git | 任意 | 版本管理与推送 |
| GitHub CLI (gh) | 任意 | 创建 GitHub Release，需先 `gh auth login` 登录 |
| Gitee 私人令牌 | — | 在 [Gitee 设置](https://gitee.com/profile/personal_access_tokens) 创建，权限勾选 `projects` |

### 一次性配置

**GitHub CLI 登录：**

```
gh auth login
```

按提示选择 GitHub.com → HTTPS → 浏览器认证。登录后用 `gh auth status` 确认。

**Gitee 令牌：**

1. 打开 https://gitee.com/profile/personal_accessTokens
2. 创建令牌，权限勾选 **projects**
3. 发布时通过环境变量传入：

```powershell
$env:GITEE_TOKEN = "你的令牌"
```

**Git 远程地址（已在仓库中配置好）：**

```
origin  → https://github.com/DwgSearch/Symlinker.git
gitee   → https://gitee.com/h1985/Symlinker.git
```

如需重新添加：

```
git remote add origin https://github.com/DwgSearch/Symlinker.git
git remote add gitee  https://gitee.com/h1985/Symlinker.git
```

---

## 2. 版本号规则

版本号格式为 `主.次.修订`（如 `0.1.0`、`0.2.0`），定义在 `Symlinker.csproj` 的 `<Version>` 标签中。

发布脚本会自动修改版本号、提交、打标签（`v0.1.0` 格式），无需手动改。

---

## 3. 编译与打包

### 方式一：用发布脚本（推荐）

```powershell
Set-Location "G:\Script\Symlinker"

# DryRun 模式：只编译和打包，不改 git、不发布
.\scripts\release.ps1 -Version 0.2.0 -DryRun -Yes
```

产物在 `dist/` 目录：

| 文件 | 说明 |
|------|------|
| `dist/publish-win-x64/` | dotnet publish 输出目录（exe + dll + 多语言资源） |
| `dist/Symlinker-v0.2.0-win-x64.zip` | 打包好的发布压缩包 |
| `dist/Symlinker-v0.2.0-win-x64.zip.sha256` | SHA-256 校验文件 |

DryRun 结束后 `Symlinker.csproj` 的版本号会自动还原，git 工作区保持干净。

### 方式二：手动编译

```powershell
# 普通编译
dotnet build Symlinker.csproj -c Release

# 发布（框架依赖，体积小，需要目标机器装 .NET 10 运行时）
dotnet publish Symlinker.csproj -c Release -r win-x64 --self-contained false -o dist\publish-win-x64

# 发布（自包含，体积大约 70 MB，无需装运行时）
dotnet publish Symlinker.csproj -c Release -r win-x64 --self-contained true -o dist\publish-win-x64
```

手动打包：

```powershell
Compress-Archive -Path dist\publish-win-x64\* -DestinationPath dist\Symlinker-v0.2.0-win-x64.zip
$hash = (Get-FileHash -Algorithm SHA256 dist\Symlinker-v0.2.0-win-x64.zip).Hash.ToLower()
Set-Content -Encoding ASCII -Path dist\Symlinker-v0.2.0-win-x64.zip.sha256 -Value "$hash  Symlinker-v0.2.0-win-x64.zip"
```

---

## 4. 正式发布

### 前提检查

```powershell
# 1. 确认在项目根目录
Set-Location "G:\Script\Symlinker"

# 2. 确认 git 工作区干净
git status

# 3. 确认 GitHub CLI 已登录
gh auth status

# 4. 设置 Gitee 令牌
$env:GITEE_TOKEN = "你的令牌"
```

### 一键发布

```powershell
.\scripts\release.ps1 -Version 0.2.0
```

脚本会显示发布摘要并等待确认（输入 `y`），然后自动执行：

1. 修改 `Symlinker.csproj` 中的 `<Version>`
2. `dotnet publish` 编译
3. 打包 zip + 生成 SHA-256
4. `git commit` + `git tag v0.2.0`
5. `git push` 到 GitHub 和 Gitee（代码 + 标签）
6. 用 `gh release create` 创建 GitHub Release 并上传附件
7. 用 Gitee API 创建 Gitee Release 并上传附件

加 `-Yes` 跳过确认提示。

### 发布参数一览

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `-Version` | （必填） | 版本号，如 `0.2.0` |
| `-Notes` | 自动生成 | 发行说明文本 |
| `-NotesFile` | — | 从文件读取发行说明 |
| `-GitHubRemote` | `origin` | GitHub 远程名称 |
| `-GiteeRemote` | `gitee` | Gitee 远程名称 |
| `-SkipGitHub` | — | 跳过 GitHub 发布 |
| `-SkipGitee` | — | 跳过 Gitee 发布 |
| `-SelfContained` | — | 打包 .NET 运行时（体积大，免安装运行时） |
| `-DryRun` | — | 只编译打包，不发布 |
| `-Yes` | — | 跳过确认 |

### 示例

```powershell
# 只测试编译
.\scripts\release.ps1 -Version 0.2.0 -DryRun -Yes

# 只发 GitHub
.\scripts\release.ps1 -Version 0.2.0 -SkipGitee

# 发布自包含版本
.\scripts\release.ps1 -Version 0.2.0 -SelfContained

# 指定发行说明
.\scripts\release.ps1 -Version 0.2.0 -Notes "修复了拖放问题和界面翻译"
```

---

## 5. 手动发布（脚本不可用时）

### 5.1 修改版本号

编辑 `Symlinker.csproj`，将 `<Version>0.1.0</Version>` 改为新版本号。

### 5.2 编译打包

参考第 3 节。

### 5.3 提交并打标签

```powershell
git add Symlinker.csproj
git commit -m "Release v0.2.0"
git tag -a v0.2.0 -m "Symlinker v0.2.0"
git push origin main
git push origin v0.2.0
git push gitee main
git push gitee v0.2.0
```

### 5.4 创建 GitHub Release

```powershell
gh release create v0.2.0 `
    dist\Symlinker-v0.2.0-win-x64.zip `
    dist\Symlinker-v0.2.0-win-x64.zip.sha256 `
    --repo DwgSearch/Symlinker `
    --title "Symlinker v0.2.0" `
    --notes "发行说明"
```

### 5.5 创建 Gitee Release

```powershell
# 创建 Release
$release = Invoke-RestMethod -Method Post `
    -Uri "https://gitee.com/api/v5/repos/h1985/Symlinker/releases" `
    -Body @{
        access_token     = $env:GITEE_TOKEN
        tag_name         = "v0.2.0"
        name             = "Symlinker v0.2.0"
        body             = "发行说明"
        target_commitish = "main"
        prerelease       = "false"
    }

# 上传附件
foreach ($file in "dist\Symlinker-v0.2.0-win-x64.zip", "dist\Symlinker-v0.2.0-win-x64.zip.sha256") {
    curl.exe -sS --fail -o NUL `
        -F "access_token=$($env:GITEE_TOKEN)" `
        -F "file=@$file" `
        "https://gitee.com/api/v5/repos/h1985/Symlinker/releases/$($release.id)/attach_files"
}
```

---

## 6. 发布后验证

```powershell
# GitHub
gh release view v0.2.0 --repo DwgSearch/Symlinker

# Gitee
curl -sS "https://gitee.com/api/v5/repos/h1985/Symlinker/releases/latest?access_token=$env:GITEE_TOKEN"
```

或直接访问：

- GitHub: https://github.com/DwgSearch/Symlinker/releases
- Gitee: https://gitee.com/h1985/Symlinker/releases

---

## 7. 常见问题

### push 报仓库不存在

GitHub 或 Gitee 上还没建仓库。GitHub 用 `gh repo create DwgSearch/Symlinker --public --description "..."` 创建；Gitee 通过 API 创建（见第 5.5 节的 API 调用，改用 `/user/repos` 端点）。

### Gitee 仓库默认私有

Gitee 个人免费版只能创建私有仓库。如需公开，到仓库 **管理 → 基本信息** 里修改。

### 符号链接创建失败

程序本身不自动提权。在 Windows 设置中开启 **开发者模式**（For developers），或以管理员身份运行 Symlinker，或改用「联接 + 硬链接」模式。

### DryRun 后 csproj 被改了

DryRun 模式会在结束时自动 `git checkout -- Symlinker.csproj` 还原。如果没有还原，手动执行 `git checkout -- Symlinker.csproj`。

### 脚本提示 git 工作区不干净

发布前必须先提交或暂存所有改动。用 `git status` 检查，`git stash` 暂存，或 `git commit` 提交。

---

## 8. 快速操作速查

```powershell
# === 完整流程（复制改版本号即可） ===
Set-Location "G:\Script\Symlinker"
$env:GITEE_TOKEN = "你的令牌"

# 第一步：DryRun 验证编译
.\scripts\release.ps1 -Version 0.2.0 -DryRun -Yes

# 第二步：正式发布
.\scripts\release.ps1 -Version 0.2.0

# 第三步：验证
gh release view v0.2.0 --repo DwgSearch/Symlinker
```
