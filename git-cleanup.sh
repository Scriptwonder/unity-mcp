#!/bin/bash
# Git Cleanup Script for Unity MCP Repository
# Commits staged deletions and adds new files

set -e  # Exit on error

echo "=== Git Cleanup Script ==="
echo ""

# Phase 1: Review current status
echo "Phase 1: Current git status"
echo "----------------------------"
git status
echo ""

read -p "Continue with commits? (y/n) " -n 1 -r
echo ""
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 1
fi

# Phase 2: Remove the CLI_USAGE_GUIDE.md that we're replacing with README
echo ""
echo "Phase 2: Removing duplicate CLI documentation..."
if [ -f "Server/src/cli/CLI_USAGE_GUIDE.md" ]; then
    git rm Server/src/cli/CLI_USAGE_GUIDE.md
    echo "  ✓ Removed Server/src/cli/CLI_USAGE_GUIDE.md"
else
    echo "  - CLI_USAGE_GUIDE.md already removed"
fi

# Phase 3: Stage all deletions and modifications
echo ""
echo "Phase 3: Staging all changes..."
git add -A
echo "  ✓ Staged all changes"

# Phase 4: Commit the major cleanup (staged deletions + cleanup)
echo ""
echo "Phase 4: Committing cleanup..."
git commit -m "chore: remove legacy client configurators, stdio transport, and telemetry infrastructure

- Remove all MCP client configurators (Cursor, VSCode, Windsurf, etc.)
- Remove stdio transport implementation
- Remove client configuration services
- Remove telemetry infrastructure
- Remove legacy migrations and models
- Remove outdated documentation (CURSOR_HELP, MCP_CLIENT_CONFIGURATORS, TELEMETRY)
- Consolidate CLI documentation (remove duplicate CLI_USAGE_GUIDE.md)
- Clean up Python build artifacts and legacy code backup

This completes the transition to CLI-only HTTP server architecture."

echo "  ✓ Committed cleanup"

# Phase 5: Add new files
echo ""
echo "Phase 5: Adding new files..."

# Check if new files exist and add them
if [ -f "MCPForUnity/Editor/Services/HttpBridgeAutoConnect.cs" ]; then
    git add MCPForUnity/Editor/Services/HttpBridgeAutoConnect.cs
    git add MCPForUnity/Editor/Services/HttpBridgeAutoConnect.cs.meta
    echo "  ✓ Added HttpBridgeAutoConnect"
fi

if [ -f "Server/src/utils/__init__.py" ]; then
    git add Server/src/utils/__init__.py
    git add Server/src/utils/console.py
    echo "  ✓ Added utils module"
fi

if [ -f "docs/VFX_TOKEN_OPTIMIZATION.md" ]; then
    git add docs/VFX_TOKEN_OPTIMIZATION.md
    echo "  ✓ Added VFX_TOKEN_OPTIMIZATION.md"
fi

if [ -f "pyproject.toml" ]; then
    git add pyproject.toml
    echo "  ✓ Added root pyproject.toml"
fi

# Add the new README we created
if [ -f "Server/src/cli/README.md" ]; then
    git add Server/src/cli/README.md
    echo "  ✓ Added CLI README.md"
fi

# Add the cleanup scripts
if [ -f "cleanup.sh" ]; then
    git add cleanup.sh
    echo "  ✓ Added cleanup.sh"
fi

if [ -f "git-cleanup.sh" ]; then
    git add git-cleanup.sh
    echo "  ✓ Added git-cleanup.sh"
fi

# Commit new files if any were added
echo ""
if git diff --cached --quiet; then
    echo "  - No new files to commit"
else
    git commit -m "feat: add HTTP bridge auto-connect and utilities

- Add HttpBridgeAutoConnect service for automatic connection management
- Add console utilities for CLI output
- Add VFX token optimization documentation
- Add root pyproject.toml for unified project configuration
- Add CLI README pointing to main documentation
- Add cleanup scripts for repository maintenance"
    echo "  ✓ Committed new files"
fi

echo ""
echo "=== Git Cleanup Complete ==="
echo ""
echo "Summary:"
git log --oneline -3
echo ""
echo "Current status:"
git status
echo ""
echo "Next steps:"
echo "1. Run: ./cleanup.sh (to delete directories)"
echo "2. Run: git gc --aggressive --prune=now (for final optimization)"
echo "3. Verify the changes are correct"
echo "4. Push to remote if satisfied"
