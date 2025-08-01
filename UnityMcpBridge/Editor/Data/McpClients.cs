using System;
using System.Collections.Generic;
using System.IO;
using UnityMcpBridge.Editor.Models;

namespace UnityMcpBridge.Editor.Data
{
    public class McpClients
    {
        public List<McpClient> clients = new()
        {
            new()
            {
                name = "Claude Desktop",
                windowsConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Claude",
                    "claude_desktop_config.json"
                ),
                linuxConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Application Support",
                    "Claude",
                    "claude_desktop_config.json"
                ),
                mcpType = McpTypes.ClaudeDesktop,
                configStatus = "Not Configured",
            },
            new()
            {
                name = "Claude Code",
                windowsConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude.json"
                ),
                linuxConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude.json"
                ),
                mcpType = McpTypes.ClaudeCode,
                configStatus = "Not Configured",
            },
            new()
            {
                name = "Cursor",
                windowsConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cursor",
                    "mcp.json"
                ),
                linuxConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cursor",
                    "mcp.json"
                ),
                mcpType = McpTypes.Cursor,
                configStatus = "Not Configured",
            },
            new()
            {
                name = "VSCode GitHub Copilot",
                windowsConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Code",
                    "User",
                    "settings.json"
                ),
                linuxConfigPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Application Support",
                    "Code",
                    "User",
                    "settings.json"
                ),
                mcpType = McpTypes.VSCode,
                configStatus = "Not Configured",
            },
        };

        // Initialize status enums after construction
        public McpClients()
        {
            foreach (var client in clients)
            {
                if (client.configStatus == "Not Configured")
                {
                    client.status = McpStatus.NotConfigured;
                }
            }
        }
    }
}

