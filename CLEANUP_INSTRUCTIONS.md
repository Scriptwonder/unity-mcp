# Unity MCP Repository Cleanup Instructions

This guide walks you through the space optimization cleanup for the Unity MCP repository.

## Summary

This cleanup removes:
- **Server/legacy_mcp/** - Legacy MCP code backup (~5-15 MB)
- **TestProjects/AssetStoreUploads/** - Asset Store upload project (~10-30 MB)
- **build/** - Python build artifacts (~2-5 MB)
- **Python cache files** - __pycache__ and .pyc files (~1-3 MB)
- **Duplicate documentation** - CLI_USAGE_GUIDE.md (replaced with README)

**Estimated space savings: 20-50 MB**

---

## Step 1: Make Scripts Executable

```bash
cd /Users/scriptwonder/Documents/GitHub/unity-mcp
chmod +x cleanup.sh
chmod +x git-cleanup.sh
```

---

## Step 2: Run File Cleanup

This removes untracked directories and Python cache files:

```bash
./cleanup.sh
```

**What it does:**
- Removes `Server/legacy_mcp/` directory
- Removes `TestProjects/AssetStoreUploads/` directory
- Removes `build/` directory
- Removes all `__pycache__/` directories
- Removes all `.pyc` files

---

## Step 3: Run Git Cleanup

This commits all staged deletions and adds new files:

```bash
./git-cleanup.sh
```

**What it does:**
- Reviews current git status
- Removes duplicate `Server/src/cli/CLI_USAGE_GUIDE.md`
- Commits all staged deletions (client configurators, stdio, telemetry)
- Adds new files (HttpBridgeAutoConnect, utils, docs)
- Creates two commits with descriptive messages

**Review the changes before proceeding!** The script will show you what's being committed.

---

## Step 4: Run Git Garbage Collection

Optimize the git repository to reclaim space:

```bash
git gc --aggressive --prune=now
```

**What it does:**
- Compresses git objects
- Removes unreachable objects
- Prunes old references
- Can take a few minutes

**Expected result:** 10-20% reduction in `.git` directory size

---

## Step 5: Verify Everything Works

### Check Git Status
```bash
git status
```
Should show: "nothing to commit, working tree clean"

### Check Repository Size
```bash
du -sh .git
du -sh .
```
Compare before/after sizes.

### Verify Server Still Works
```bash
cd Server
mcp-for-unity --http-url http://localhost:8080
# Open Unity and verify connection
```

### Verify CLI Still Works
```bash
unity-mcp status
unity-mcp --help
```

### Check for Broken References
```bash
# Search for references to deleted code
grep -r "ClaudeCodeConfigurator\|ClientConfigurationService\|StdioBridgeHost" MCPForUnity/ Server/ 2>/dev/null
```
Should return no results (or only comments/docs).

---

## Step 6: Review Commits

```bash
# View recent commits
git log --oneline -5

# View detailed changes
git show HEAD
git show HEAD~1

# View files changed
git diff HEAD~2..HEAD --stat
```

---

## Step 7: Push to Remote (Optional)

If everything looks good, push the changes:

```bash
# Push to current branch
git push origin cli-module

# Or create a PR to beta branch
# (as indicated in git status, main branch is 'beta')
```

---

## Rollback Instructions

If something goes wrong, you can rollback:

```bash
# Undo last commit (keep changes)
git reset --soft HEAD~1

# Undo last commit (discard changes)
git reset --hard HEAD~1

# Undo both cleanup commits
git reset --hard HEAD~2

# Restore specific file from history
git checkout HEAD~2 -- path/to/file
```

---

## Files Created

These files were created during cleanup and are now part of the repository:

1. **cleanup.sh** - Shell script to delete untracked directories and Python cache
2. **git-cleanup.sh** - Shell script to commit staged deletions and add new files
3. **Server/src/cli/README.md** - New README pointing to main CLI documentation
4. **CLEANUP_INSTRUCTIONS.md** - This file

---

## What Was Kept

- **TestProjects/UnityMCPTests/** - Active test project (kept)
- **Server/.venv/** - Virtual environment (gitignored, not committed)
- **Git history** - All deleted code is still in git history

---

## Troubleshooting

### "Permission denied" when running scripts
```bash
chmod +x cleanup.sh git-cleanup.sh
```

### "No such file or directory" errors
Some directories may already be deleted. This is fine - the scripts handle missing directories gracefully.

### Git conflicts
If you have uncommitted changes, stash them first:
```bash
git stash
./cleanup.sh
./git-cleanup.sh
git stash pop
```

### Want to see what will be deleted first?
Review the scripts before running:
```bash
cat cleanup.sh
cat git-cleanup.sh
```

---

## Summary of Changes

### Deleted (committed):
- Client configurators (15+ files)
- Stdio transport implementation
- Client configuration services
- Telemetry infrastructure
- Legacy migrations and models
- Outdated documentation

### Deleted (untracked):
- Server/legacy_mcp/ directory
- TestProjects/AssetStoreUploads/ directory
- build/ directory
- All __pycache__ directories
- All .pyc files

### Added:
- HttpBridgeAutoConnect service
- Console utilities
- VFX token optimization docs
- Root pyproject.toml
- CLI README
- Cleanup scripts

---

## Questions?

If you encounter issues or have questions about the cleanup process, please review:
1. The git log to see what was changed
2. The git status to see current state
3. The original plan in your conversation history

All deleted code is preserved in git history and can be recovered if needed.
