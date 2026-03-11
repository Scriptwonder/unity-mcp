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
    internal static class InputAssetOps
    {
        internal static object GetInputActionsInfo(JObject @params)
        {
            // Find all .inputactions files in the project
            string[] guids = AssetDatabase.FindAssets("t:TextAsset");
            var assets = new List<object>();

            // Also search for InputActionAsset type if available
            string[] inputGuids = InputHelpers.HasInputSystem
                ? AssetDatabase.FindAssets($"t:{InputHelpers.InputActionAssetType.Name}")
                : Array.Empty<string>();

            var processedPaths = new HashSet<string>();

            // First, process typed assets if Input System is available
            foreach (string guid in inputGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (processedPaths.Contains(path)) continue;
                processedPaths.Add(path);

                try
                {
                    var asset = AssetDatabase.LoadAssetAtPath(path, InputHelpers.InputActionAssetType);
                    if (asset != null)
                        assets.Add(DescribeAssetViaReflection(asset, path));
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[InputAssetOps] Failed to load {path}: {ex.Message}");
                }
            }

            // Fallback: find .inputactions files by extension (works without package)
            string[] allAssetPaths = AssetDatabase.GetAllAssetPaths();
            foreach (string path in allAssetPaths)
            {
                if (!path.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (processedPaths.Contains(path)) continue;
                processedPaths.Add(path);

                try
                {
                    // Parse the JSON directly
                    string json = File.ReadAllText(path);
                    var parsed = JObject.Parse(json);
                    assets.Add(DescribeAssetFromJson(parsed, path));
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[InputAssetOps] Failed to parse {path}: {ex.Message}");
                }
            }

            return new SuccessResponse($"Found {assets.Count} input action asset(s).", assets);
        }

        private static object DescribeAssetViaReflection(UnityEngine.Object asset, string path)
        {
            var type = asset.GetType();
            var maps = new List<object>();

            // Get actionMaps property (ReadOnlyArray<InputActionMap>)
            var actionMapsProperty = type.GetProperty("actionMaps", BindingFlags.Public | BindingFlags.Instance);
            if (actionMapsProperty != null)
            {
                var actionMaps = actionMapsProperty.GetValue(asset);
                if (actionMaps is System.Collections.IEnumerable enumerable)
                {
                    foreach (var map in enumerable)
                    {
                        maps.Add(DescribeActionMap(map));
                    }
                }
            }

            // Get controlSchemes
            var controlSchemes = new List<object>();
            var schemesProperty = type.GetProperty("controlSchemes", BindingFlags.Public | BindingFlags.Instance);
            if (schemesProperty != null)
            {
                var schemes = schemesProperty.GetValue(asset);
                if (schemes is System.Collections.IEnumerable schemeEnum)
                {
                    foreach (var scheme in schemeEnum)
                    {
                        controlSchemes.Add(DescribeControlScheme(scheme));
                    }
                }
            }

            return new
            {
                path,
                name = asset.name,
                maps,
                controlSchemes
            };
        }

        private static object DescribeActionMap(object map)
        {
            var mapType = map.GetType();
            string mapName = mapType.GetProperty("name")?.GetValue(map) as string;

            var actions = new List<object>();
            var actionsProperty = mapType.GetProperty("actions", BindingFlags.Public | BindingFlags.Instance);
            if (actionsProperty != null)
            {
                var actionList = actionsProperty.GetValue(map);
                if (actionList is System.Collections.IEnumerable enumerable)
                {
                    foreach (var act in enumerable)
                    {
                        actions.Add(DescribeAction(act));
                    }
                }
            }

            return new { name = mapName, actions };
        }

        private static object DescribeAction(object action)
        {
            var actionType = action.GetType();
            string name = actionType.GetProperty("name")?.GetValue(action) as string;
            var typeVal = actionType.GetProperty("type")?.GetValue(action);
            string expectedControlType = actionType.GetProperty("expectedControlType")?.GetValue(action) as string;

            var bindings = new List<object>();
            var bindingsProperty = actionType.GetProperty("bindings", BindingFlags.Public | BindingFlags.Instance);
            if (bindingsProperty != null)
            {
                var bindingList = bindingsProperty.GetValue(action);
                if (bindingList is System.Collections.IEnumerable enumerable)
                {
                    foreach (var binding in enumerable)
                    {
                        bindings.Add(DescribeBinding(binding));
                    }
                }
            }

            return new
            {
                name,
                type = typeVal?.ToString(),
                expectedControlType,
                bindings
            };
        }

        private static object DescribeBinding(object binding)
        {
            var bType = binding.GetType();
            string path = bType.GetProperty("path")?.GetValue(binding) as string;
            string groups = bType.GetProperty("groups")?.GetValue(binding) as string;
            string interactions = bType.GetProperty("interactions")?.GetValue(binding) as string;
            string processors = bType.GetProperty("processors")?.GetValue(binding) as string;
            string name = bType.GetProperty("name")?.GetValue(binding) as string;
            bool isComposite = (bool)(bType.GetProperty("isComposite")?.GetValue(binding) ?? false);
            bool isPartOfComposite = (bool)(bType.GetProperty("isPartOfComposite")?.GetValue(binding) ?? false);

            return new
            {
                path,
                name,
                groups,
                interactions,
                processors,
                isComposite,
                isPartOfComposite
            };
        }

        private static object DescribeControlScheme(object scheme)
        {
            var sType = scheme.GetType();
            string name = sType.GetProperty("name")?.GetValue(scheme) as string;

            var devices = new List<object>();
            var devReqsProperty = sType.GetProperty("deviceRequirements", BindingFlags.Public | BindingFlags.Instance);
            if (devReqsProperty != null)
            {
                var devReqs = devReqsProperty.GetValue(scheme);
                if (devReqs is System.Collections.IEnumerable enumerable)
                {
                    foreach (var req in enumerable)
                    {
                        var reqType = req.GetType();
                        string controlPath = reqType.GetProperty("controlPath")?.GetValue(req) as string;
                        bool isOptional = (bool)(reqType.GetProperty("isOptional")?.GetValue(req) ?? false);
                        devices.Add(new { controlPath, isOptional });
                    }
                }
            }

            return new { name, devices };
        }

        private static object DescribeAssetFromJson(JObject parsed, string path)
        {
            var maps = new List<object>();
            var mapsArray = parsed["maps"] as JArray;
            if (mapsArray != null)
            {
                foreach (var mapToken in mapsArray)
                {
                    var mapObj = mapToken as JObject;
                    if (mapObj == null) continue;
                    string mapName = mapObj["name"]?.ToString();

                    var actions = new List<object>();
                    var actionsArray = mapObj["actions"] as JArray;
                    if (actionsArray != null)
                    {
                        foreach (var actionToken in actionsArray)
                        {
                            var actObj = actionToken as JObject;
                            if (actObj == null) continue;
                            actions.Add(new
                            {
                                name = actObj["name"]?.ToString(),
                                type = actObj["type"]?.ToString(),
                                expectedControlType = actObj["expectedControlType"]?.ToString(),
                            });
                        }
                    }

                    var bindings = new List<object>();
                    var bindingsArray = mapObj["bindings"] as JArray;
                    if (bindingsArray != null)
                    {
                        foreach (var bToken in bindingsArray)
                        {
                            var bObj = bToken as JObject;
                            if (bObj == null) continue;
                            bindings.Add(new
                            {
                                path = bObj["path"]?.ToString(),
                                name = bObj["name"]?.ToString(),
                                groups = bObj["groups"]?.ToString(),
                                isComposite = bObj["isComposite"]?.Value<bool>() ?? false,
                                isPartOfComposite = bObj["isPartOfComposite"]?.Value<bool>() ?? false,
                            });
                        }
                    }

                    maps.Add(new { name = mapName, actions, bindings });
                }
            }

            var controlSchemes = new List<object>();
            var schemesArray = parsed["controlSchemes"] as JArray;
            if (schemesArray != null)
            {
                foreach (var sToken in schemesArray)
                {
                    var sObj = sToken as JObject;
                    if (sObj == null) continue;
                    string sName = sObj["name"]?.ToString();
                    var devices = new List<object>();
                    var devReqs = sObj["deviceRequirements"] as JArray;
                    if (devReqs != null)
                    {
                        foreach (var dToken in devReqs)
                        {
                            var dObj = dToken as JObject;
                            if (dObj == null) continue;
                            devices.Add(new
                            {
                                controlPath = dObj["controlPath"]?.ToString(),
                                isOptional = dObj["isOptional"]?.Value<bool>() ?? false,
                            });
                        }
                    }
                    controlSchemes.Add(new { name = sName, devices });
                }
            }

            return new
            {
                path,
                name = parsed["name"]?.ToString() ?? Path.GetFileNameWithoutExtension(path),
                maps,
                controlSchemes,
                parsedFromJson = true
            };
        }

        internal static object CreateInputActions(JObject @params)
        {
            string assetPath = InputHelpers.GetAssetPath(@params);
            if (string.IsNullOrEmpty(assetPath))
                return new ErrorResponse("'assetPath' is required.");

            if (!assetPath.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase))
                assetPath += ".inputactions";

            // Ensure directory exists
            string dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Create via ScriptableObject.CreateInstance
            var asset = ScriptableObject.CreateInstance(InputHelpers.InputActionAssetType);
            var props = InputHelpers.ExtractProperties(@params);
            if (props != null)
            {
                string name = props["name"]?.ToString();
                if (!string.IsNullOrEmpty(name))
                    asset.name = name;
            }

            // Save the asset
            // InputActionAsset has a ToJson() method, write it
            var toJsonMethod = InputHelpers.InputActionAssetType.GetMethod("ToJson", BindingFlags.Public | BindingFlags.Instance);
            if (toJsonMethod != null)
            {
                string json = toJsonMethod.Invoke(asset, null) as string;
                File.WriteAllText(assetPath, json);
            }
            else
            {
                // Fallback: write minimal JSON
                string json = "{\"name\": \"" + (asset.name ?? "InputActions") + "\", \"maps\": [], \"controlSchemes\": []}";
                File.WriteAllText(assetPath, json);
            }

            AssetDatabase.Refresh();

            // Re-load the asset
            var loaded = AssetDatabase.LoadAssetAtPath(assetPath, InputHelpers.InputActionAssetType);

            return new SuccessResponse($"Created input actions asset at '{assetPath}'.",
                new { path = assetPath, name = loaded?.name ?? asset.name });
        }

        internal static object AddActionMap(JObject @params)
        {
            string assetPath = InputHelpers.GetAssetPath(@params);
            string mapName = InputHelpers.GetMapName(@params);
            if (string.IsNullOrEmpty(assetPath))
                return new ErrorResponse("'assetPath' is required.");
            if (string.IsNullOrEmpty(mapName))
                return new ErrorResponse("'mapName' is required.");

            var asset = InputHelpers.LoadInputActionAsset(@params);
            if (asset == null)
                return new ErrorResponse($"Input action asset not found at '{assetPath}'.");

            // Use InputActionSetupExtensions.AddActionMap(asset, name)
            var addMapMethod = InputHelpers.SetupExtensionsType?.GetMethod("AddActionMap",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { InputHelpers.InputActionAssetType, typeof(string) }, null);

            if (addMapMethod == null)
                return new ErrorResponse("Could not find AddActionMap method via reflection.");

            var newMap = addMapMethod.Invoke(null, new object[] { asset, mapName });
            InputHelpers.SaveAsset(asset);

            return new SuccessResponse($"Added action map '{mapName}' to '{assetPath}'.",
                new { assetPath, mapName });
        }

        internal static object RemoveActionMap(JObject @params)
        {
            string assetPath = InputHelpers.GetAssetPath(@params);
            string mapName = InputHelpers.GetMapName(@params);
            if (string.IsNullOrEmpty(assetPath))
                return new ErrorResponse("'assetPath' is required.");
            if (string.IsNullOrEmpty(mapName))
                return new ErrorResponse("'mapName' is required.");

            var asset = InputHelpers.LoadInputActionAsset(@params);
            if (asset == null)
                return new ErrorResponse($"Input action asset not found at '{assetPath}'.");

            var map = InputHelpers.FindActionMap(asset, mapName);
            if (map == null)
                return new ErrorResponse($"Action map '{mapName}' not found in '{assetPath}'.");

            // Use InputActionSetupExtensions.RemoveActionMap(asset, map)
            var removeMethod = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "RemoveActionMap" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionAssetType);

            if (removeMethod == null)
                return new ErrorResponse("Could not find RemoveActionMap method via reflection.");

            removeMethod.Invoke(null, new object[] { asset, map });
            InputHelpers.SaveAsset(asset);

            return new SuccessResponse($"Removed action map '{mapName}' from '{assetPath}'.",
                new { assetPath, mapName });
        }

        internal static object AddAction(JObject @params)
        {
            string assetPath = InputHelpers.GetAssetPath(@params);
            string mapName = InputHelpers.GetMapName(@params);
            string actionName = InputHelpers.GetActionName(@params);
            if (string.IsNullOrEmpty(assetPath))
                return new ErrorResponse("'assetPath' is required.");
            if (string.IsNullOrEmpty(mapName))
                return new ErrorResponse("'mapName' is required.");
            if (string.IsNullOrEmpty(actionName))
                return new ErrorResponse("'actionName' is required.");

            var asset = InputHelpers.LoadInputActionAsset(@params);
            if (asset == null)
                return new ErrorResponse($"Input action asset not found at '{assetPath}'.");

            var map = InputHelpers.FindActionMap(asset, mapName);
            if (map == null)
                return new ErrorResponse($"Action map '{mapName}' not found in '{assetPath}'.");

            // InputActionSetupExtensions.AddAction(map, name, type, binding, interactions, processors, groups, expectedControlType)
            var props = InputHelpers.ExtractProperties(@params);
            string typeStr = props?["type"]?.ToString();
            string expectedControlType = props?["expectedControlType"]?.ToString();
            string binding = props?["binding"]?.ToString();
            string interactions = props?["interactions"]?.ToString();
            string processors = props?["processors"]?.ToString();
            string groups = props?["groups"]?.ToString();

            // Resolve InputActionType enum
            object actionTypeEnum = null;
            if (!string.IsNullOrEmpty(typeStr) && InputHelpers.InputActionTypeEnum != null)
            {
                try { actionTypeEnum = Enum.Parse(InputHelpers.InputActionTypeEnum, typeStr, true); }
                catch { /* default to Button */ }
            }

            // Find the AddAction overload
            var addActionMethods = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "AddAction")
                .ToArray();

            // Use the overload: AddAction(InputActionMap, string, InputActionType, string, string, string, string, string)
            var addMethod = addActionMethods?.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 2 && p[0].ParameterType == InputHelpers.InputActionMapType;
            });

            if (addMethod == null)
                return new ErrorResponse("Could not find AddAction method via reflection.");

            // Build parameter array based on method signature
            var methodParams = addMethod.GetParameters();
            var args = new object[methodParams.Length];
            args[0] = map;
            args[1] = actionName;
            for (int i = 2; i < methodParams.Length; i++)
            {
                var param = methodParams[i];
                if (param.ParameterType == InputHelpers.InputActionTypeEnum)
                    args[i] = actionTypeEnum ?? param.DefaultValue;
                else if (param.Name == "binding")
                    args[i] = binding ?? (param.HasDefaultValue ? param.DefaultValue : null);
                else if (param.Name == "interactions")
                    args[i] = interactions ?? (param.HasDefaultValue ? param.DefaultValue : null);
                else if (param.Name == "processors")
                    args[i] = processors ?? (param.HasDefaultValue ? param.DefaultValue : null);
                else if (param.Name == "groups")
                    args[i] = groups ?? (param.HasDefaultValue ? param.DefaultValue : null);
                else if (param.Name == "expectedControlType")
                    args[i] = expectedControlType ?? (param.HasDefaultValue ? param.DefaultValue : null);
                else
                    args[i] = param.HasDefaultValue ? param.DefaultValue : null;
            }

            var newAction = addMethod.Invoke(null, args);
            InputHelpers.SaveAsset(asset);

            return new SuccessResponse($"Added action '{actionName}' to map '{mapName}' in '{assetPath}'.",
                new { assetPath, mapName, actionName, type = typeStr ?? "Button", expectedControlType });
        }

        internal static object RemoveAction(JObject @params)
        {
            string assetPath = InputHelpers.GetAssetPath(@params);
            string mapName = InputHelpers.GetMapName(@params);
            string actionName = InputHelpers.GetActionName(@params);
            if (string.IsNullOrEmpty(assetPath))
                return new ErrorResponse("'assetPath' is required.");
            if (string.IsNullOrEmpty(mapName))
                return new ErrorResponse("'mapName' is required.");
            if (string.IsNullOrEmpty(actionName))
                return new ErrorResponse("'actionName' is required.");

            var asset = InputHelpers.LoadInputActionAsset(@params);
            if (asset == null)
                return new ErrorResponse($"Input action asset not found at '{assetPath}'.");

            var map = InputHelpers.FindActionMap(asset, mapName);
            if (map == null)
                return new ErrorResponse($"Action map '{mapName}' not found in '{assetPath}'.");

            var action = InputHelpers.FindAction(map, actionName);
            if (action == null)
                return new ErrorResponse($"Action '{actionName}' not found in map '{mapName}'.");

            // InputActionSetupExtensions.RemoveAction(InputAction)
            var removeMethod = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "RemoveAction" &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionType);

            if (removeMethod == null)
                return new ErrorResponse("Could not find RemoveAction method via reflection.");

            removeMethod.Invoke(null, new[] { action });
            InputHelpers.SaveAsset(asset);

            return new SuccessResponse($"Removed action '{actionName}' from map '{mapName}' in '{assetPath}'.",
                new { assetPath, mapName, actionName });
        }

        internal static object SetActionProperties(JObject @params)
        {
            string assetPath = InputHelpers.GetAssetPath(@params);
            string mapName = InputHelpers.GetMapName(@params);
            string actionName = InputHelpers.GetActionName(@params);
            if (string.IsNullOrEmpty(assetPath))
                return new ErrorResponse("'assetPath' is required.");
            if (string.IsNullOrEmpty(mapName))
                return new ErrorResponse("'mapName' is required.");
            if (string.IsNullOrEmpty(actionName))
                return new ErrorResponse("'actionName' is required.");

            var asset = InputHelpers.LoadInputActionAsset(@params);
            if (asset == null)
                return new ErrorResponse($"Input action asset not found at '{assetPath}'.");

            var map = InputHelpers.FindActionMap(asset, mapName);
            if (map == null)
                return new ErrorResponse($"Action map '{mapName}' not found in '{assetPath}'.");

            var action = InputHelpers.FindAction(map, actionName);
            if (action == null)
                return new ErrorResponse($"Action '{actionName}' not found in map '{mapName}'.");

            var props = InputHelpers.ExtractProperties(@params);
            if (props == null)
                return new ErrorResponse("'properties' is required for set_action_properties.");

            var actionType = action.GetType();
            var changed = new List<string>();

            // Set expectedControlType
            string expectedControlType = props["expectedControlType"]?.ToString();
            if (expectedControlType != null)
            {
                var ectProp = actionType.GetProperty("expectedControlType", BindingFlags.Public | BindingFlags.Instance);
                if (ectProp != null && ectProp.CanWrite)
                {
                    ectProp.SetValue(action, expectedControlType);
                    changed.Add("expectedControlType");
                }
            }

            // Set type (InputActionType enum)
            string typeStr = props["type"]?.ToString();
            if (typeStr != null && InputHelpers.InputActionTypeEnum != null)
            {
                try
                {
                    var enumVal = Enum.Parse(InputHelpers.InputActionTypeEnum, typeStr, true);
                    var typeProp = actionType.GetProperty("type", BindingFlags.Public | BindingFlags.Instance);
                    if (typeProp != null && typeProp.CanWrite)
                    {
                        typeProp.SetValue(action, enumVal);
                        changed.Add("type");
                    }
                }
                catch { /* ignore invalid type */ }
            }

            // Set interactions
            string interactions = props["interactions"]?.ToString();
            if (interactions != null)
            {
                var intProp = actionType.GetProperty("interactions", BindingFlags.Public | BindingFlags.Instance);
                if (intProp != null && intProp.CanWrite)
                {
                    intProp.SetValue(action, interactions);
                    changed.Add("interactions");
                }
            }

            // Set processors
            string processors = props["processors"]?.ToString();
            if (processors != null)
            {
                var procProp = actionType.GetProperty("processors", BindingFlags.Public | BindingFlags.Instance);
                if (procProp != null && procProp.CanWrite)
                {
                    procProp.SetValue(action, processors);
                    changed.Add("processors");
                }
            }

            InputHelpers.SaveAsset(asset);

            return new SuccessResponse($"Updated properties on action '{actionName}': {string.Join(", ", changed)}.",
                new { assetPath, mapName, actionName, changed });
        }
    }
}
