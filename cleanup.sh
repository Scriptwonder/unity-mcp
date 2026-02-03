#!/bin/bash
# Unity MCP Repository Cleanup Script
# This script removes legacy code, build artifacts, and Python cache files

set -e  # Exit on error

echo "=== Unity MCP Cleanup Script ==="
echo ""

# Phase 1: Delete untracked directories
echo "Phase 1: Deleting untracked directories..."

if [ -d "Server/legacy_mcp" ]; then
    echo "  - Removing Server/legacy_mcp/"
    rm -rf Server/legacy_mcp/
    echo "    ✓ Removed"
else
    echo "  - Server/legacy_mcp/ not found (already removed)"
fi

if [ -d "TestProjects/AssetStoreUploads" ]; then
    echo "  - Removing TestProjects/AssetStoreUploads/"
    rm -rf TestProjects/AssetStoreUploads/
    echo "    ✓ Removed"
else
    echo "  - TestProjects/AssetStoreUploads/ not found (already removed)"
fi

if [ -d "build" ]; then
    echo "  - Removing build/"
    rm -rf build/
    echo "    ✓ Removed"
else
    echo "  - build/ not found (already removed)"
fi

echo ""

# Phase 2: Clean Python cache files
echo "Phase 2: Cleaning Python cache files..."

echo "  - Removing __pycache__ directories..."
find . -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
echo "    ✓ Done"

echo "  - Removing .pyc files..."
find . -type f -name "*.pyc" -delete 2>/dev/null || true
echo "    ✓ Done"

echo ""
echo "=== Cleanup Complete ==="
echo ""
echo "Next steps:"
echo "1. Run: git status"
echo "2. Review the changes"
echo "3. Proceed with git commits as guided"
