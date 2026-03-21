#!/usr/bin/env bash

set -e

echo "===> Step 1: 检查是否在 Git 仓库中"
git rev-parse --is-inside-work-tree > /dev/null 2>&1 || {
    echo "❌ 当前目录不是 Git 仓库"
    exit 1
}

echo "===> Step 2: 设置 Git 行尾策略（core.autocrlf=true）"
git config core.autocrlf true

echo "===> Step 3: 生成 .gitattributes（如不存在）"

if [ ! -f ".gitattributes" ]; then
cat > .gitattributes << 'EOF'
* text=auto

# C/C++
*.cpp text eol=lf
*.h   text eol=lf
*.c   text eol=lf

# Windows 脚本
*.bat text eol=crlf
*.ps1 text eol=crlf

# 文本文件
*.txt text eol=lf
EOF
    echo "✔ 已创建 .gitattributes"
else
    echo "⚠ 已存在 .gitattributes，跳过创建（请自行确认内容）"
fi

echo "===> Step 4: 添加 .gitattributes"
git add .gitattributes

echo "===> Step 5: 执行 renormalize（关键步骤）"
git add --renormalize .

echo "===> Step 6: 提交规范化变更"
git commit -m "chore: normalize line endings"

echo "===> Step 7: 强制刷新工作区（重新 checkout）"
git rm --cached -r . > /dev/null 2>&1
git reset --hard

echo "===> Step 8: 完成"

echo ""
echo "🎉 行尾规范化完成！"
echo "👉 现在 Visual Studio 不会再提示"
echo "👉 git diff / commit 将保持干净"