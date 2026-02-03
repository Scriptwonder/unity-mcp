# Unity MCP CLI

This directory contains the CLI implementation for Unity MCP.

## Documentation

For complete CLI usage documentation, see:
- **[CLI Usage Guide](../../../docs/guides/CLI_USAGE.md)** - Main user documentation
- **[CLI Examples](../../../docs/guides/CLI_EXAMPLE.md)** - Practical examples and workflows

## Quick Start

```bash
# Install the CLI
pip install -e .

# Start the HTTP bridge server
mcp-for-unity --http-url http://localhost:8080

# Check connection
unity-mcp status

# Get help
unity-mcp --help
```

## Development

The CLI is built with Click and provides:
- Command-line interface to Unity Editor via HTTP bridge
- JSON/text/table output formats
- Multi-instance support
- Comprehensive tool coverage

See the main documentation for detailed usage information.
