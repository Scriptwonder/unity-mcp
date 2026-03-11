using System;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Helpers;

namespace MCPForUnity.Editor.Tools.Input
{
    [McpForUnityTool("manage_input", AutoRegister = false)]
    public static class ManageInput
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            string action = p.Get("action")?.ToLowerInvariant();

            if (string.IsNullOrEmpty(action))
                return new ErrorResponse("'action' parameter is required.");

            try
            {
                // Tier 1: Always-available actions (work without Input System package)
                switch (action)
                {
                    case "ping":
                        return InputHelpers.Ping();

                    case "get_input_actions_info":
                        return InputAssetOps.GetInputActionsInfo(@params);
                }

                // Tier 2: Requires Input System package
                if (!InputHelpers.HasInputSystem)
                {
                    return new ErrorResponse(
                        $"Action '{action}' requires the Input System package (com.unity.inputsystem). " +
                        "Use manage_packages(action='install', package_name='com.unity.inputsystem') to install. " +
                        "Editor restart required after installation.");
                }

                switch (action)
                {
                    // Asset operations
                    case "create_input_actions":
                        return InputAssetOps.CreateInputActions(@params);
                    case "add_action_map":
                        return InputAssetOps.AddActionMap(@params);
                    case "remove_action_map":
                        return InputAssetOps.RemoveActionMap(@params);
                    case "add_action":
                        return InputAssetOps.AddAction(@params);
                    case "remove_action":
                        return InputAssetOps.RemoveAction(@params);
                    case "set_action_properties":
                        return InputAssetOps.SetActionProperties(@params);

                    // Binding operations
                    case "add_binding":
                        return InputBindingOps.AddBinding(@params);
                    case "add_composite_binding":
                        return InputBindingOps.AddCompositeBinding(@params);
                    case "remove_binding":
                        return InputBindingOps.RemoveBinding(@params);

                    // Config operations
                    case "add_control_scheme":
                        return InputConfigOps.AddControlScheme(@params);
                    case "remove_control_scheme":
                        return InputConfigOps.RemoveControlScheme(@params);
                    case "setup_player_input":
                        return InputConfigOps.SetupPlayerInput(@params);
                    case "create_preset":
                        return InputConfigOps.CreatePreset(@params);

                    default:
                        return new ErrorResponse(
                            $"Unknown action: '{action}'. Valid actions: " +
                            "ping, get_input_actions_info, " +
                            "create_input_actions, add_action_map, remove_action_map, add_action, remove_action, set_action_properties, " +
                            "add_binding, add_composite_binding, remove_binding, " +
                            "add_control_scheme, remove_control_scheme, setup_player_input, create_preset.");
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"[ManageInput] Action '{action}' failed: {ex}");
                return new ErrorResponse($"Error in action '{action}': {ex.Message}");
            }
        }
    }
}
