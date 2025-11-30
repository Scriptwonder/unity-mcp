using MCPForUnity.Editor.Setup;
using MCPForUnity.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.MenuItems
{
    /// <summary>
    /// Centralized menu items for MCP For Unity
    /// </summary>
    public static class MCPForUnityMenu
    {
        // ========================================
        // Main Menu Items
        // ========================================

        /// <summary>
        /// Show the Setup Window
        /// </summary>
        [MenuItem("Window/MCP For Unity/Setup Window", priority = 1)]
        public static void ShowSetupWindow()
        {
            SetupWindowService.ShowSetupWindow();
        }

        /// <summary>
        /// Toggle the main MCP For Unity window
        /// </summary>
        [MenuItem("Window/MCP For Unity/Toggle MCP Window %#m", priority = 2)]
        public static void ToggleMCPWindow()
        {
            if (EditorWindow.HasOpenInstances<MCPForUnityEditorWindow>())
            {
                foreach (var window in UnityEngine.Resources.FindObjectsOfTypeAll<MCPForUnityEditorWindow>())
                {
                    window.Close();
                }
            }
            else
            {
                MCPForUnityEditorWindow.ShowWindow();
            }
        }

    }
}
