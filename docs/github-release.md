# 推送 GitHub 与发布 Pankin 0.3.2

仓库：`demgkp1/Pankin`。当前本地分支为 `main`，已有 `v0.3.0` 和 `v0.3.1` 标签。本次创建新标签和新 Release `v0.3.2`，不要移动、覆盖或删除旧的 `v0.3.0` / `v0.3.1`。

## 1. 构建与打包

安装包已在本工作区生成；以后重建可以运行：

```bash
cd /home/dkp/projects/Pankin
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
dotnet build Pankin.sln -c Release
dotnet test Pankin.sln -c Release --no-build
bash scripts/package-linux.sh
(cd artifacts && sha256sum -c SHA256SUMS.txt)
```

本次上传的三个文件：

- `artifacts/Pankin-0.3.2-x86_64.AppImage`
- `artifacts/pankin_0.3.2_amd64.deb`
- `artifacts/SHA256SUMS.txt`

`artifacts/`、`.tools/`、安装包和 Windows 参考区均被 `.gitignore` 忽略。**Git push 只推送源码，不会上传这些安装包，也不会自动创建 Release。** 当前仓库没有发布 Actions 工作流，资产需要在 GitHub Releases 页面或通过 GitHub CLI 上传。

## 2. 提交并推送源码与标签

先检查 `git status` / `git diff`，只提交此次修复和版本更新；不要包含其他尚未完成的改动。

```bash
cd /home/dkp/projects/Pankin

git status
git diff

git add -A
git commit -m "release: Pankin v0.3.2 with focus dismissal fixes"

# 在已提交、工作区干净后同步远端；有冲突时解决并完成 rebase，再继续。
git pull --rebase origin main

# 如果同步带来了源码变更，先重新执行第 1 节构建、测试、打包与校验。
# 确保本次上传的安装包对应即将打标签的源码提交。
git push origin main

# 标签必须指向已包含此次修复和版本更新的提交。
git tag -a v0.3.2 -m "Pankin v0.3.2"
git push origin v0.3.2
```

如果远端 `main` 有保护规则，就按仓库要求通过分支和 PR 合入后再标记最终提交。不要用 `--force` 绕过冲突、保护规则或现有标签。

HTTPS 推送需要已配置的 GitHub 凭据，不支持使用账号密码作为 Git 密码。若已安装 GitHub CLI，可运行 `gh auth login` 和 `gh auth setup-git`；不要把访问令牌写入仓库或明文放在命令中。

## 3. 使用网页发布（推荐，无需安装 gh）

1. 打开 `demgkp1/Pankin` 仓库，进入 **Releases → Draft a new release**。
2. 选择已推送的标签 **v0.3.2**，目标分支为 `main`。
3. 标题填写 **Pankin v0.3.2**，将 `docs/releases/v0.3.2.md` 内容复制到说明区域。
4. 上传两个 0.3.2 安装包和 **SHA256SUMS.txt**，等待全部上传完成。
5. 确认为正式版，不勾选 **Set as a pre-release**，勾选 **Set as the latest release**。
6. 确认内容和附件后点击 **Publish release**；仅保存 Draft 不会向普通用户发布更新。

不要将新包放进旧的 `v0.3.0` / `v0.3.1` Release；0.3.2 必须有对应的新标签和版本号。

## 4. 可选：使用 GitHub CLI 发布

以下命令需要已经安装并登录 `gh`；等第 2 步推送标签完成后再执行。

```bash
cd /home/dkp/projects/Pankin

gh auth status

# 先创建草稿并上传所有附件，防止用户看到缺少安装包的正式 Release。
gh release create v0.3.2 \
  artifacts/Pankin-0.3.2-x86_64.AppImage \
  artifacts/pankin_0.3.2_amd64.deb \
  artifacts/SHA256SUMS.txt \
  --repo demgkp1/Pankin \
  --verify-tag \
  --draft \
  --title "Pankin v0.3.2" \
  --notes-file docs/releases/v0.3.2.md

# 在浏览器检查标题、说明、标签和 3 个附件。
gh release view v0.3.2 --repo demgkp1/Pankin --web

# 检查完成后公开发布，并将其标记为 Latest。
gh release edit v0.3.2 --repo demgkp1/Pankin --draft=false --latest
```

`--verify-tag` 要求标签已存在于远端，避免 CLI 自动用错误提交创建标签。

如果 **v0.3.2 草稿已存在**，不要再次创建；查看后使用 `gh release upload v0.3.2 <文件路径> --repo demgkp1/Pankin` 补齐附件。只有明确要替换同名附件、且该 Release 允许修改时才加 `--clobber`。已启用不可变发布（immutable releases）的正式 Release 不能替换已上传资产或移动标签；应在草稿阶段准备好文件，或另发更高版本。

## 5. 发布后检查

```bash
gh release view v0.3.2 --repo demgkp1/Pankin
gh api repos/demgkp1/Pankin/releases/latest --jq '.tag_name'
```

预期 Latest 为 `v0.3.2`，附件完整，下载后的校验码一致。应用更新源为 `https://api.github.com/repos/demgkp1/Pankin/releases/latest`；更新检查会忽略 Draft 和 Pre-release，只提示比当前版本更新的版本。新版程序报告当前版本 0.3.2，不应再次提示更新到自己。

## 官方参考

- GitHub 管理 Releases：<https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository>
- 创建 Release：<https://cli.github.com/manual/gh_release_create>
- 编辑 Release：<https://cli.github.com/manual/gh_release_edit>
- 上传资产：<https://cli.github.com/manual/gh_release_upload>
- HTTPS Git 凭据配置：<https://cli.github.com/manual/gh_auth_setup-git>
