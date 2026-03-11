using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.Input;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Resources.Project
{
    [McpForUnityResource("get_input_actions")]
    public static class InputActionsResource
    {
        public static object HandleCommand(JObject @params)
        {
            try
            {
                return InputAssetOps.GetInputActionsInfo(@params ?? new JObject());
            }
            catch (Exception e)
            {
                McpLog.Error($"[InputActionsResource] Error listing input actions: {e}");
                return new ErrorResponse($"Error listing input actions: {e.Message}");
            }
        }
    }
}
