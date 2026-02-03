# Unity MCP CLI Bridge (Docker Image)

[![License](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)
[![Discord](https://img.shields.io/badge/discord-join-red.svg?logo=discord&logoColor=white)](https://discord.gg/y4p8KfzrN4)

Lightweight HTTP bridge for the Unity MCP plugin. This image is **CLI-only** and does **not** expose MCP client endpoints.

**Required:** Install the [Unity MCP Plugin](https://github.com/CoplayDev/unity-mcp?tab=readme-ov-file#-step-1-install-the-unity-package) to connect Unity Editor with this bridge.

---

## Quick Start

### 1. Pull the image

```bash
docker pull msanatan/mcp-for-unity-server:latest
```

### 2. Run the server

```bash
docker run -p 8080:8080 msanatan/mcp-for-unity-server:latest --transport http --http-url http://0.0.0.0:8080
```

---

## Configuration

The bridge connects to the Unity Editor automatically when both are running. No additional configuration is needed.

**Environment Variables:**

- `LOG_LEVEL=DEBUG` - Enable detailed logging (default: INFO)

Example running with environment variables:

```bash
docker run -p 8080:8080 -e LOG_LEVEL=DEBUG msanatan/mcp-for-unity-server:latest --transport http --http-url http://0.0.0.0:8080
```

---

## License

MIT License - See [LICENSE](https://github.com/CoplayDev/unity-mcp/blob/main/LICENSE)
