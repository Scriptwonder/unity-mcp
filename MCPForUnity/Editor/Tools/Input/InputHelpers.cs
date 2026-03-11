using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Input
{
    internal static class InputHelpers
    {
        private static bool? _hasInputSystem;
        private static Type _inputActionAssetType;
        private static Type _inputActionMapType;
        private static Type _inputActionType;
        private static Type _inputActionTypeEnum;
        private static Type _inputBindingType;
        private static Type _setupExtensionsType;
        private static Type _playerInputType;
        private static Type _inputControlSchemeType;

        internal static bool HasInputSystem
        {
            get
            {
                if (_hasInputSystem == null)
                    DetectInputSystem();
                return _hasInputSystem.Value;
            }
        }

        internal static Type InputActionAssetType
        {
            get
            {
                if (_hasInputSystem == null) DetectInputSystem();
                return _inputActionAssetType;
            }
        }

        internal static Type InputActionMapType
        {
            get
            {
                if (_hasInputSystem == null) DetectInputSystem();
                return _inputActionMapType;
            }
        }

        internal static Type InputActionType
        {
            get
            {
                if (_hasInputSystem == null) DetectInputSystem();
                return _inputActionType;
            }
        }

        internal static Type InputActionTypeEnum
        {
            get
            {
                if (_hasInputSystem == null) DetectInputSystem();
                return _inputActionTypeEnum;
            }
        }

        internal static Type InputBindingType
        {
            get
            {
                if (_hasInputSystem == null) DetectInputSystem();
                return _inputBindingType;
            }
        }

        internal static Type SetupExtensionsType
        {
            get
            {
                if (_hasInputSystem == null) DetectInputSystem();
                return _setupExtensionsType;
            }
        }

        internal static Type PlayerInputType
        {
            get
            {
                if (_hasInputSystem == null) DetectInputSystem();
                return _playerInputType;
            }
        }

        internal static Type InputControlSchemeType
        {
            get
            {
                if (_hasInputSystem == null) DetectInputSystem();
                return _inputControlSchemeType;
            }
        }

        private static void DetectInputSystem()
        {
            _inputActionAssetType = Type.GetType("UnityEngine.InputSystem.InputActionAsset, Unity.InputSystem");
            _inputActionMapType = Type.GetType("UnityEngine.InputSystem.InputActionMap, Unity.InputSystem");
            _inputActionType = Type.GetType("UnityEngine.InputSystem.InputAction, Unity.InputSystem");
            _inputActionTypeEnum = Type.GetType("UnityEngine.InputSystem.InputActionType, Unity.InputSystem");
            _inputBindingType = Type.GetType("UnityEngine.InputSystem.InputBinding, Unity.InputSystem");
            _setupExtensionsType = Type.GetType("UnityEngine.InputSystem.InputActionSetupExtensions, Unity.InputSystem");
            _playerInputType = UnityTypeResolver.ResolveComponent("PlayerInput");
            _inputControlSchemeType = Type.GetType("UnityEngine.InputSystem.InputControlScheme, Unity.InputSystem");
            _hasInputSystem = _inputActionAssetType != null && _inputActionMapType != null;
        }

        internal static string GetInputSystemVersion()
        {
            if (!HasInputSystem || _inputActionAssetType == null)
                return null;

            try
            {
                var assembly = _inputActionAssetType.Assembly;
                var version = assembly.GetName().Version;
                return version?.ToString();
            }
            catch
            {
                return "unknown";
            }
        }

        internal static string DetectInputMode()
        {
            try
            {
                var prop = typeof(PlayerSettings).GetProperty(
                    "activeInputHandler",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null) return "legacy";
                int value = (int)prop.GetValue(null);
                return value switch
                {
                    0 => "legacy",
                    1 => "new",
                    2 => "both",
                    _ => "legacy"
                };
            }
            catch
            {
                return HasInputSystem ? "new_pending_restart" : "legacy";
            }
        }

        internal static object Ping()
        {
            string mode = DetectInputMode();
            var warnings = new List<string>();

            if (mode == "both")
                warnings.Add("Both input systems active — may cause performance issues. Consider switching to 'new' only.");
            if (HasInputSystem && mode == "legacy")
                warnings.Add("Input System package installed but legacy input is active. Change Player Settings > Active Input Handling to 'Input System Package (New)'. Editor restart required.");
            if (!HasInputSystem && mode != "legacy")
                warnings.Add("Input System package not installed but new input handler is selected. Install the package or switch to legacy.");

            return new
            {
                success = true,
                message = HasInputSystem
                    ? "Input System is available."
                    : "Input System not installed. Only detect actions available.",
                data = new
                {
                    inputSystemInstalled = HasInputSystem,
                    inputSystemVersion = GetInputSystemVersion(),
                    activeInputHandler = mode,
                    legacyInputEnabled = mode == "legacy" || mode == "both",
                    newInputEnabled = mode == "new" || mode == "both",
                    warnings = warnings.Count > 0 ? warnings.ToArray() : null
                }
            };
        }

        internal static UnityEngine.Object LoadInputActionAsset(JObject @params)
        {
            var p = new ToolParams(@params);
            string assetPath = p.Get("assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return null;
            return AssetDatabase.LoadAssetAtPath(assetPath, InputActionAssetType);
        }

        internal static string GetAssetPath(JObject @params)
        {
            var p = new ToolParams(@params);
            return p.Get("assetPath");
        }

        internal static string GetMapName(JObject @params)
        {
            var p = new ToolParams(@params);
            return p.Get("mapName");
        }

        internal static string GetActionName(JObject @params)
        {
            var p = new ToolParams(@params);
            return p.Get("actionName");
        }

        internal static JObject ExtractProperties(JObject @params)
        {
            var props = @params["properties"] as JObject;
            if (props != null) return props;

            var propsStr = ParamCoercion.CoerceString(@params["properties"], null);
            if (propsStr != null)
            {
                try { return JObject.Parse(propsStr); }
                catch { return null; }
            }

            return null;
        }

        internal static object FindActionMap(object asset, string mapName)
        {
            if (asset == null || string.IsNullOrEmpty(mapName))
                return null;

            var method = asset.GetType().GetMethod("FindActionMap",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(bool) }, null);
            if (method == null) return null;

            return method.Invoke(asset, new object[] { mapName, false });
        }

        internal static object FindAction(object actionMap, string actionName)
        {
            if (actionMap == null || string.IsNullOrEmpty(actionName))
                return null;

            var method = actionMap.GetType().GetMethod("FindAction",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(bool) }, null);
            if (method == null) return null;

            return method.Invoke(actionMap, new object[] { actionName, false });
        }

        internal static void SaveAsset(UnityEngine.Object asset)
        {
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
        }

        internal static GameObject FindTargetGameObject(JObject @params)
        {
            var targetToken = @params["target"];
            if (targetToken == null) return null;

            string searchMethod = ParamCoercion.CoerceString(
                @params["searchMethod"] ?? @params["search_method"], "by_name");

            if (targetToken.Type == JTokenType.Integer)
            {
                int instanceId = targetToken.Value<int>();
                return GameObjectLookup.FindById(instanceId);
            }

            string targetStr = targetToken.ToString();
            if (int.TryParse(targetStr, out int parsedId))
            {
                var byId = GameObjectLookup.FindById(parsedId);
                if (byId != null) return byId;
            }

            return GameObjectLookup.FindByTarget(targetToken, searchMethod, true);
        }
    }
}
