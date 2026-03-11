using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Input
{
    internal static class InputBindingOps
    {
        internal static object AddBinding(JObject @params)
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
            string bindingPath = props?["path"]?.ToString();
            if (string.IsNullOrEmpty(bindingPath))
                return new ErrorResponse("'properties.path' is required for add_binding.");

            string groups = props?["groups"]?.ToString();
            string interactions = props?["interactions"]?.ToString();
            string processors = props?["processors"]?.ToString();

            // Find AddBinding overload: AddBinding(InputAction, string path, string interactions, string processors, string groups)
            var addBindingMethod = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddBinding" &&
                    m.GetParameters().Length >= 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionType &&
                    m.GetParameters()[1].ParameterType == typeof(string));

            if (addBindingMethod == null)
                return new ErrorResponse("Could not find AddBinding method via reflection.");

            var methodParams = addBindingMethod.GetParameters();
            var args = new object[methodParams.Length];
            args[0] = action;
            args[1] = bindingPath;
            for (int i = 2; i < methodParams.Length; i++)
            {
                var param = methodParams[i];
                if (param.Name == "interactions")
                    args[i] = interactions ?? (param.HasDefaultValue ? param.DefaultValue : null);
                else if (param.Name == "processors")
                    args[i] = processors ?? (param.HasDefaultValue ? param.DefaultValue : null);
                else if (param.Name == "groups")
                    args[i] = groups ?? (param.HasDefaultValue ? param.DefaultValue : null);
                else
                    args[i] = param.HasDefaultValue ? param.DefaultValue : null;
            }

            addBindingMethod.Invoke(null, args);
            InputHelpers.SaveAsset(asset);

            return new SuccessResponse(
                $"Added binding '{bindingPath}' to action '{actionName}' in map '{mapName}'.",
                new { assetPath, mapName, actionName, path = bindingPath, groups, interactions, processors });
        }

        internal static object AddCompositeBinding(JObject @params)
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
            string compositeType = props?["compositeType"]?.ToString();
            if (string.IsNullOrEmpty(compositeType))
                return new ErrorResponse("'properties.compositeType' is required (e.g., '2DVector', '1DAxis').");

            var parts = props?["parts"] as JObject;
            if (parts == null || !parts.HasValues)
                return new ErrorResponse("'properties.parts' is required — dict of part names to binding paths.");

            string groups = props?["groups"]?.ToString();

            // InputActionSetupExtensions.AddCompositeBinding(InputAction, string composite, string interactions, string processors)
            var addCompositeMethod = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddCompositeBinding" &&
                    m.GetParameters().Length >= 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionType);

            if (addCompositeMethod == null)
                return new ErrorResponse("Could not find AddCompositeBinding method via reflection.");

            // Call AddCompositeBinding — returns a CompositeSyntax
            var compositeMethodParams = addCompositeMethod.GetParameters();
            var compositeArgs = new object[compositeMethodParams.Length];
            compositeArgs[0] = action;
            compositeArgs[1] = compositeType;
            for (int i = 2; i < compositeMethodParams.Length; i++)
                compositeArgs[i] = compositeMethodParams[i].HasDefaultValue ? compositeMethodParams[i].DefaultValue : null;

            var syntax = addCompositeMethod.Invoke(null, compositeArgs);

            // Now call .With(name, path, groups, processors) for each part
            if (syntax != null)
            {
                var syntaxType = syntax.GetType();
                var withMethod = syntaxType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "With" && m.GetParameters().Length >= 2);

                if (withMethod != null)
                {
                    foreach (var prop in parts.Properties())
                    {
                        string partName = prop.Name;
                        string partPath = prop.Value.ToString();

                        var withParams = withMethod.GetParameters();
                        var withArgs = new object[withParams.Length];
                        withArgs[0] = partName;
                        withArgs[1] = partPath;
                        for (int i = 2; i < withParams.Length; i++)
                        {
                            if (withParams[i].Name == "groups")
                                withArgs[i] = groups ?? (withParams[i].HasDefaultValue ? withParams[i].DefaultValue : null);
                            else
                                withArgs[i] = withParams[i].HasDefaultValue ? withParams[i].DefaultValue : null;
                        }

                        syntax = withMethod.Invoke(syntax, withArgs);
                    }
                }
            }

            InputHelpers.SaveAsset(asset);

            var partNames = parts.Properties().Select(p => p.Name).ToList();
            return new SuccessResponse(
                $"Added composite binding '{compositeType}' with parts [{string.Join(", ", partNames)}] to action '{actionName}'.",
                new { assetPath, mapName, actionName, compositeType, parts = partNames });
        }

        internal static object RemoveBinding(JObject @params)
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
            int? index = props?["index"]?.Value<int>();
            if (index == null)
                return new ErrorResponse("'properties.index' is required for remove_binding.");

            // Get bindings count
            var bindingsProperty = action.GetType().GetProperty("bindings", BindingFlags.Public | BindingFlags.Instance);
            if (bindingsProperty == null)
                return new ErrorResponse("Could not access bindings on action.");

            var bindings = bindingsProperty.GetValue(action);
            int count = 0;
            if (bindings is System.Collections.ICollection collection)
                count = collection.Count;
            else
            {
                // ReadOnlyArray has Count property
                var countProp = bindings?.GetType().GetProperty("Count");
                if (countProp != null)
                    count = (int)countProp.GetValue(bindings);
            }

            if (index.Value < 0 || index.Value >= count)
                return new ErrorResponse($"Binding index {index.Value} out of range (0-{count - 1}).");

            // Use ChangeBinding(action, index).Erase()
            var changeBindingMethod = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ChangeBinding" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionType &&
                    m.GetParameters()[1].ParameterType == typeof(int));

            if (changeBindingMethod == null)
                return new ErrorResponse("Could not find ChangeBinding method via reflection.");

            var accessor = changeBindingMethod.Invoke(null, new object[] { action, index.Value });
            if (accessor != null)
            {
                var eraseMethod = accessor.GetType().GetMethod("Erase", BindingFlags.Public | BindingFlags.Instance);
                if (eraseMethod != null)
                    eraseMethod.Invoke(accessor, null);
                else
                    return new ErrorResponse("Could not find Erase method on binding accessor.");
            }

            InputHelpers.SaveAsset(asset);

            return new SuccessResponse(
                $"Removed binding at index {index.Value} from action '{actionName}' in map '{mapName}'.",
                new { assetPath, mapName, actionName, removedIndex = index.Value });
        }
    }
}
