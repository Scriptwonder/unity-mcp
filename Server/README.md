# Unity MCP CLI Bridge

[![python](https://img.shields.io/badge/Python-3.10+-3776AB.svg?style=flat&logo=python&logoColor=white)](https://www.python.org)
[![License](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

Lightweight HTTP bridge for the Unity MCP plugin. This build is **CLI-only** and does **not** expose MCP client endpoints.

**Required:** Install the [Unity MCP Plugin](https://github.com/CoplayDev/unity-mcp?tab=readme-ov-file#-step-1-install-the-unity-package) to connect Unity Editor with this bridge. You also need `uvx` (requires [uv](https://docs.astral.sh/uv/)) to run the server.

---

## Installation

### Option 1: PyPI

Install and run directly from PyPI using `uvx`.

```bash
uvx --from mcpforunityserver mcp-for-unity --transport http --http-url http://localhost:8080
```

### Option 2: From GitHub Source

Use this to run the latest released version from the repository. Change the version to `main` to run the latest unreleased changes from the repository.

```bash
uvx --from git+https://github.com/CoplayDev/unity-mcp@v9.2.0 mcp-for-unity --transport http --http-url http://localhost:8080
```

### Option 3: Docker

**Use Pre-built Image:**

```bash
docker run -p 8080:8080 msanatan/mcp-for-unity-server:latest --transport http --http-url http://0.0.0.0:8080
```

**Build Locally:**

```bash
docker build -t unity-mcp-server .
docker run -p 8080:8080 unity-mcp-server --transport http --http-url http://0.0.0.0:8080
```

---

## Requirements

- **Python:** 3.10 or newer
- **Unity Editor:** 2021.3 LTS or newer
- **uv:** Python package manager ([Installation Guide](https://docs.astral.sh/uv/getting-started/installation/))

---

## License

MIT License - See [LICENSE](https://github.com/CoplayDev/unity-mcp/blob/main/LICENSE)
