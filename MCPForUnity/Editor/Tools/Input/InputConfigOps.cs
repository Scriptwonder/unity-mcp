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
    internal static class InputConfigOps
    {
        internal static object AddControlScheme(JObject @params)
        {
            string assetPath = InputHelpers.GetAssetPath(@params);
            if (string.IsNullOrEmpty(assetPath))
                return new ErrorResponse("'assetPath' is required.");

            var asset = InputHelpers.LoadInputActionAsset(@params);
            if (asset == null)
                return new ErrorResponse($"Input action asset not found at '{assetPath}'.");

            var props = InputHelpers.ExtractProperties(@params);
            string schemeName = props?["schemeName"]?.ToString();
            if (string.IsNullOrEmpty(schemeName))
                return new ErrorResponse("'properties.schemeName' is required.");

            var devicesToken = props?["devices"] as JArray;

            // InputActionSetupExtensions.AddControlScheme(InputActionAsset, string name)
            var addSchemeMethod = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddControlScheme" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionAssetType &&
                    m.GetParameters()[1].ParameterType == typeof(string));

            if (addSchemeMethod == null)
                return new ErrorResponse("Could not find AddControlScheme method via reflection.");

            var schemeSyntax = addSchemeMethod.Invoke(null, new object[] { asset, schemeName });

            // Add device requirements via .WithRequiredDevice / .WithOptionalDevice or .OrWithRequiredDevice / .OrWithOptionalDevice
            if (schemeSyntax != null && devicesToken != null)
            {
                var syntaxType = schemeSyntax.GetType();

                bool isFirst = true;
                foreach (var deviceToken in devicesToken)
                {
                    var deviceObj = deviceToken as JObject;
                    if (deviceObj == null) continue;

                    string devicePath = deviceObj["devicePath"]?.ToString() ?? deviceObj["controlPath"]?.ToString();
                    if (string.IsNullOrEmpty(devicePath)) continue;

                    bool required = deviceObj["required"]?.Value<bool>() ?? true;

                    string methodName;
                    if (isFirst)
                        methodName = required ? "WithRequiredDevice" : "WithOptionalDevice";
                    else
                        methodName = required ? "OrWithRequiredDevice" : "OrWithOptionalDevice";

                    var deviceMethod = syntaxType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(string) }, null);

                    if (deviceMethod != null)
                        schemeSyntax = deviceMethod.Invoke(schemeSyntax, new object[] { devicePath });

                    isFirst = false;
                }
            }

            InputHelpers.SaveAsset(asset);

            return new SuccessResponse($"Added control scheme '{schemeName}' to '{assetPath}'.",
                new { assetPath, schemeName });
        }

        internal static object RemoveControlScheme(JObject @params)
        {
            string assetPath = InputHelpers.GetAssetPath(@params);
            if (string.IsNullOrEmpty(assetPath))
                return new ErrorResponse("'assetPath' is required.");

            var asset = InputHelpers.LoadInputActionAsset(@params);
            if (asset == null)
                return new ErrorResponse($"Input action asset not found at '{assetPath}'.");

            var props = InputHelpers.ExtractProperties(@params);
            string schemeName = props?["schemeName"]?.ToString();
            if (string.IsNullOrEmpty(schemeName))
                return new ErrorResponse("'properties.schemeName' is required.");

            // InputActionSetupExtensions.RemoveControlScheme(InputActionAsset, string name)
            var removeMethod = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "RemoveControlScheme" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionAssetType &&
                    m.GetParameters()[1].ParameterType == typeof(string));

            if (removeMethod == null)
                return new ErrorResponse("Could not find RemoveControlScheme method via reflection.");

            removeMethod.Invoke(null, new object[] { asset, schemeName });
            InputHelpers.SaveAsset(asset);

            return new SuccessResponse($"Removed control scheme '{schemeName}' from '{assetPath}'.",
                new { assetPath, schemeName });
        }

        internal static object SetupPlayerInput(JObject @params)
        {
            var go = InputHelpers.FindTargetGameObject(@params);
            if (go == null)
                return new ErrorResponse("Target GameObject not found. Provide 'target' parameter.");

            string assetPath = InputHelpers.GetAssetPath(@params);
            if (string.IsNullOrEmpty(assetPath))
                return new ErrorResponse("'assetPath' is required to wire PlayerInput.");

            if (InputHelpers.PlayerInputType == null)
                return new ErrorResponse("PlayerInput component type not found. Is the Input System package installed?");

            var asset = InputHelpers.LoadInputActionAsset(@params);
            if (asset == null)
                return new ErrorResponse($"Input action asset not found at '{assetPath}'.");

            // Add or get PlayerInput component
            var playerInput = go.GetComponent(InputHelpers.PlayerInputType);
            if (playerInput == null)
            {
                playerInput = Undo.AddComponent(go, InputHelpers.PlayerInputType);
            }

            // Set actions property (InputActionAsset)
            var actionsProp = InputHelpers.PlayerInputType.GetProperty("actions", BindingFlags.Public | BindingFlags.Instance);
            if (actionsProp != null && actionsProp.CanWrite)
                actionsProp.SetValue(playerInput, asset);

            // Set default map
            var props = InputHelpers.ExtractProperties(@params);
            string defaultMap = props?["defaultMap"]?.ToString() ?? props?["defaultActionMap"]?.ToString();
            if (!string.IsNullOrEmpty(defaultMap))
            {
                var defaultMapProp = InputHelpers.PlayerInputType.GetProperty("defaultActionMap", BindingFlags.Public | BindingFlags.Instance);
                if (defaultMapProp != null && defaultMapProp.CanWrite)
                    defaultMapProp.SetValue(playerInput, defaultMap);
            }

            EditorUtility.SetDirty(go);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

            return new SuccessResponse(
                $"Set up PlayerInput on '{go.name}' with asset '{assetPath}'.",
                new { gameObject = go.name, assetPath, defaultMap });
        }

        internal static object CreatePreset(JObject @params)
        {
            var props = InputHelpers.ExtractProperties(@params);
            string preset = props?["preset"]?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(preset))
                return new ErrorResponse("'properties.preset' is required (fps, third_person, platformer, vehicle, ui, rts).");

            string assetPath = InputHelpers.GetAssetPath(@params);
            if (string.IsNullOrEmpty(assetPath))
                assetPath = $"Assets/{PresetDisplayName(preset)}_InputActions.inputactions";

            // Create the asset first
            var createParams = new JObject { ["assetPath"] = assetPath };
            var createResult = InputAssetOps.CreateInputActions(createParams);

            // Verify creation succeeded
            if (createResult is ErrorResponse)
                return createResult;

            // Load the freshly created asset
            var asset = InputHelpers.LoadInputActionAsset(createParams);
            if (asset == null)
                return new ErrorResponse($"Failed to load newly created asset at '{assetPath}'.");

            // Build preset using helper methods that add maps/actions/bindings
            switch (preset)
            {
                case "fps":
                    BuildFpsPreset(asset, assetPath);
                    break;
                case "third_person":
                    BuildThirdPersonPreset(asset, assetPath);
                    break;
                case "platformer":
                    BuildPlatformerPreset(asset, assetPath);
                    break;
                case "vehicle":
                    BuildVehiclePreset(asset, assetPath);
                    break;
                case "ui":
                    BuildUiPreset(asset, assetPath);
                    break;
                case "rts":
                    BuildRtsPreset(asset, assetPath);
                    break;
                default:
                    return new ErrorResponse($"Unknown preset '{preset}'. Valid: fps, third_person, platformer, vehicle, ui, rts.");
            }

            InputHelpers.SaveAsset(asset);

            return new SuccessResponse($"Created '{preset}' preset at '{assetPath}'.",
                new { assetPath, preset });
        }

        private static string PresetDisplayName(string preset) => preset switch
        {
            "fps" => "FPS",
            "third_person" => "ThirdPerson",
            "platformer" => "Platformer",
            "vehicle" => "Vehicle",
            "ui" => "UI",
            "rts" => "RTS",
            _ => preset
        };

        // --- Preset building helpers ---

        private static object AddMap(UnityEngine.Object asset, string mapName)
        {
            var method = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddActionMap" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionAssetType &&
                    m.GetParameters()[1].ParameterType == typeof(string));
            return method?.Invoke(null, new object[] { asset, mapName });
        }

        private static object AddAct(object map, string name, string type = "Button", string expectedControlType = null)
        {
            var methods = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "AddAction" && m.GetParameters().Length >= 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionMapType)
                .ToArray();

            var method = methods?.FirstOrDefault();
            if (method == null) return null;

            object actionTypeEnum = null;
            if (InputHelpers.InputActionTypeEnum != null)
            {
                try { actionTypeEnum = Enum.Parse(InputHelpers.InputActionTypeEnum, type, true); }
                catch { }
            }

            var methodParams = method.GetParameters();
            var args = new object[methodParams.Length];
            args[0] = map;
            args[1] = name;
            for (int i = 2; i < methodParams.Length; i++)
            {
                var param = methodParams[i];
                if (param.ParameterType == InputHelpers.InputActionTypeEnum)
                    args[i] = actionTypeEnum ?? param.DefaultValue;
                else if (param.Name == "expectedControlType")
                    args[i] = expectedControlType ?? (param.HasDefaultValue ? param.DefaultValue : null);
                else
                    args[i] = param.HasDefaultValue ? param.DefaultValue : null;
            }

            return method.Invoke(null, args);
        }

        private static void AddBind(object action, string path, string groups = null)
        {
            var method = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddBinding" &&
                    m.GetParameters().Length >= 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionType &&
                    m.GetParameters()[1].ParameterType == typeof(string));
            if (method == null) return;

            var methodParams = method.GetParameters();
            var args = new object[methodParams.Length];
            args[0] = action;
            args[1] = path;
            for (int i = 2; i < methodParams.Length; i++)
            {
                if (methodParams[i].Name == "groups")
                    args[i] = groups ?? (methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : null);
                else
                    args[i] = methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : null;
            }

            method.Invoke(null, args);
        }

        private static void AddComposite(object action, string composite, Dictionary<string, string> parts, string groups = null)
        {
            var method = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddCompositeBinding" &&
                    m.GetParameters().Length >= 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionType);
            if (method == null) return;

            var compositeParams = method.GetParameters();
            var compositeArgs = new object[compositeParams.Length];
            compositeArgs[0] = action;
            compositeArgs[1] = composite;
            for (int i = 2; i < compositeParams.Length; i++)
                compositeArgs[i] = compositeParams[i].HasDefaultValue ? compositeParams[i].DefaultValue : null;

            var syntax = method.Invoke(null, compositeArgs);
            if (syntax == null) return;

            var syntaxType = syntax.GetType();
            var withMethod = syntaxType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "With" && m.GetParameters().Length >= 2);
            if (withMethod == null) return;

            foreach (var kvp in parts)
            {
                var withParams = withMethod.GetParameters();
                var withArgs = new object[withParams.Length];
                withArgs[0] = kvp.Key;
                withArgs[1] = kvp.Value;
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

        private static void AddScheme(UnityEngine.Object asset, string name, string[] requiredDevices, string[] optionalDevices = null)
        {
            var method = InputHelpers.SetupExtensionsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "AddControlScheme" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == InputHelpers.InputActionAssetType &&
                    m.GetParameters()[1].ParameterType == typeof(string));
            if (method == null) return;

            var syntax = method.Invoke(null, new object[] { asset, name });
            if (syntax == null) return;

            var syntaxType = syntax.GetType();
            bool isFirst = true;

            foreach (string device in requiredDevices)
            {
                string methodName = isFirst ? "WithRequiredDevice" : "OrWithRequiredDevice";
                var devMethod = syntaxType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);
                if (devMethod != null)
                    syntax = devMethod.Invoke(syntax, new object[] { device });
                isFirst = false;
            }

            if (optionalDevices != null)
            {
                foreach (string device in optionalDevices)
                {
                    string methodName = isFirst ? "WithOptionalDevice" : "OrWithOptionalDevice";
                    var devMethod = syntaxType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(string) }, null);
                    if (devMethod != null)
                        syntax = devMethod.Invoke(syntax, new object[] { device });
                    isFirst = false;
                }
            }
        }

        // --- Preset builders ---

        private static void BuildFpsPreset(UnityEngine.Object asset, string assetPath)
        {
            var gameplay = AddMap(asset, "Gameplay");
            if (gameplay == null) return;

            var move = AddAct(gameplay, "Move", "Value", "Vector2");
            AddComposite(move, "2DVector", new Dictionary<string, string>
            {
                {"up", "<Keyboard>/w"}, {"down", "<Keyboard>/s"},
                {"left", "<Keyboard>/a"}, {"right", "<Keyboard>/d"}
            }, "Keyboard&Mouse");
            AddBind(move, "<Gamepad>/leftStick", "Gamepad");

            var look = AddAct(gameplay, "Look", "Value", "Vector2");
            AddBind(look, "<Mouse>/delta", "Keyboard&Mouse");
            AddBind(look, "<Gamepad>/rightStick", "Gamepad");

            var fire = AddAct(gameplay, "Fire", "Button");
            AddBind(fire, "<Mouse>/leftButton", "Keyboard&Mouse");
            AddBind(fire, "<Gamepad>/rightTrigger", "Gamepad");

            var aim = AddAct(gameplay, "Aim", "Button");
            AddBind(aim, "<Mouse>/rightButton", "Keyboard&Mouse");
            AddBind(aim, "<Gamepad>/leftTrigger", "Gamepad");

            var jump = AddAct(gameplay, "Jump", "Button");
            AddBind(jump, "<Keyboard>/space", "Keyboard&Mouse");
            AddBind(jump, "<Gamepad>/buttonSouth", "Gamepad");

            var reload = AddAct(gameplay, "Reload", "Button");
            AddBind(reload, "<Keyboard>/r", "Keyboard&Mouse");
            AddBind(reload, "<Gamepad>/buttonWest", "Gamepad");

            var sprint = AddAct(gameplay, "Sprint", "Button");
            AddBind(sprint, "<Keyboard>/leftShift", "Keyboard&Mouse");
            AddBind(sprint, "<Gamepad>/leftStickPress", "Gamepad");

            var interact = AddAct(gameplay, "Interact", "Button");
            AddBind(interact, "<Keyboard>/e", "Keyboard&Mouse");
            AddBind(interact, "<Gamepad>/buttonNorth", "Gamepad");

            // UI map
            var ui = AddMap(asset, "UI");
            BuildUiActions(ui);

            AddScheme(asset, "Keyboard&Mouse", new[] { "<Keyboard>", "<Mouse>" });
            AddScheme(asset, "Gamepad", new[] { "<Gamepad>" });
        }

        private static void BuildThirdPersonPreset(UnityEngine.Object asset, string assetPath)
        {
            var gameplay = AddMap(asset, "Gameplay");
            if (gameplay == null) return;

            var move = AddAct(gameplay, "Move", "Value", "Vector2");
            AddComposite(move, "2DVector", new Dictionary<string, string>
            {
                {"up", "<Keyboard>/w"}, {"down", "<Keyboard>/s"},
                {"left", "<Keyboard>/a"}, {"right", "<Keyboard>/d"}
            }, "Keyboard&Mouse");
            AddBind(move, "<Gamepad>/leftStick", "Gamepad");

            var look = AddAct(gameplay, "Look", "Value", "Vector2");
            AddBind(look, "<Mouse>/delta", "Keyboard&Mouse");
            AddBind(look, "<Gamepad>/rightStick", "Gamepad");

            var jump = AddAct(gameplay, "Jump", "Button");
            AddBind(jump, "<Keyboard>/space", "Keyboard&Mouse");
            AddBind(jump, "<Gamepad>/buttonSouth", "Gamepad");

            var sprint = AddAct(gameplay, "Sprint", "Button");
            AddBind(sprint, "<Keyboard>/leftShift", "Keyboard&Mouse");
            AddBind(sprint, "<Gamepad>/leftStickPress", "Gamepad");

            var attack = AddAct(gameplay, "Attack", "Button");
            AddBind(attack, "<Mouse>/leftButton", "Keyboard&Mouse");
            AddBind(attack, "<Gamepad>/rightTrigger", "Gamepad");

            var block = AddAct(gameplay, "Block", "Button");
            AddBind(block, "<Mouse>/rightButton", "Keyboard&Mouse");
            AddBind(block, "<Gamepad>/leftTrigger", "Gamepad");

            var dodge = AddAct(gameplay, "Dodge", "Button");
            AddBind(dodge, "<Keyboard>/leftCtrl", "Keyboard&Mouse");
            AddBind(dodge, "<Gamepad>/buttonEast", "Gamepad");

            var lockOn = AddAct(gameplay, "LockOn", "Button");
            AddBind(lockOn, "<Keyboard>/q", "Keyboard&Mouse");
            AddBind(lockOn, "<Gamepad>/rightStickPress", "Gamepad");

            var interact = AddAct(gameplay, "Interact", "Button");
            AddBind(interact, "<Keyboard>/e", "Keyboard&Mouse");
            AddBind(interact, "<Gamepad>/buttonNorth", "Gamepad");

            AddScheme(asset, "Keyboard&Mouse", new[] { "<Keyboard>", "<Mouse>" });
            AddScheme(asset, "Gamepad", new[] { "<Gamepad>" });
        }

        private static void BuildPlatformerPreset(UnityEngine.Object asset, string assetPath)
        {
            var gameplay = AddMap(asset, "Gameplay");
            if (gameplay == null) return;

            var move = AddAct(gameplay, "Move", "Value", "Vector2");
            AddComposite(move, "2DVector", new Dictionary<string, string>
            {
                {"up", "<Keyboard>/w"}, {"down", "<Keyboard>/s"},
                {"left", "<Keyboard>/a"}, {"right", "<Keyboard>/d"}
            }, "Keyboard&Mouse");
            AddBind(move, "<Gamepad>/leftStick", "Gamepad");

            var jump = AddAct(gameplay, "Jump", "Button");
            AddBind(jump, "<Keyboard>/space", "Keyboard&Mouse");
            AddBind(jump, "<Gamepad>/buttonSouth", "Gamepad");

            var attack = AddAct(gameplay, "Attack", "Button");
            AddBind(attack, "<Keyboard>/j", "Keyboard&Mouse");
            AddBind(attack, "<Gamepad>/buttonWest", "Gamepad");

            var dash = AddAct(gameplay, "Dash", "Button");
            AddBind(dash, "<Keyboard>/leftShift", "Keyboard&Mouse");
            AddBind(dash, "<Gamepad>/rightTrigger", "Gamepad");

            var interact = AddAct(gameplay, "Interact", "Button");
            AddBind(interact, "<Keyboard>/e", "Keyboard&Mouse");
            AddBind(interact, "<Gamepad>/buttonNorth", "Gamepad");

            var pause = AddAct(gameplay, "Pause", "Button");
            AddBind(pause, "<Keyboard>/escape", "Keyboard&Mouse");
            AddBind(pause, "<Gamepad>/start", "Gamepad");

            AddScheme(asset, "Keyboard&Mouse", new[] { "<Keyboard>", "<Mouse>" });
            AddScheme(asset, "Gamepad", new[] { "<Gamepad>" });
        }

        private static void BuildVehiclePreset(UnityEngine.Object asset, string assetPath)
        {
            var driving = AddMap(asset, "Driving");
            if (driving == null) return;

            var steer = AddAct(driving, "Steer", "Value", "float");
            AddComposite(steer, "1DAxis", new Dictionary<string, string>
            {
                {"negative", "<Keyboard>/a"}, {"positive", "<Keyboard>/d"}
            }, "Keyboard&Mouse");
            AddBind(steer, "<Gamepad>/leftStick/x", "Gamepad");

            var throttle = AddAct(driving, "Throttle", "Value", "float");
            AddBind(throttle, "<Keyboard>/w", "Keyboard&Mouse");
            AddBind(throttle, "<Gamepad>/rightTrigger", "Gamepad");

            var brake = AddAct(driving, "Brake", "Value", "float");
            AddBind(brake, "<Keyboard>/s", "Keyboard&Mouse");
            AddBind(brake, "<Gamepad>/leftTrigger", "Gamepad");

            var handbrake = AddAct(driving, "Handbrake", "Button");
            AddBind(handbrake, "<Keyboard>/space", "Keyboard&Mouse");
            AddBind(handbrake, "<Gamepad>/buttonSouth", "Gamepad");

            var nitro = AddAct(driving, "NitroBoost", "Button");
            AddBind(nitro, "<Keyboard>/leftShift", "Keyboard&Mouse");
            AddBind(nitro, "<Gamepad>/buttonEast", "Gamepad");

            var camSwitch = AddAct(driving, "CameraSwitch", "Button");
            AddBind(camSwitch, "<Keyboard>/c", "Keyboard&Mouse");
            AddBind(camSwitch, "<Gamepad>/buttonNorth", "Gamepad");

            AddScheme(asset, "Keyboard&Mouse", new[] { "<Keyboard>", "<Mouse>" });
            AddScheme(asset, "Gamepad", new[] { "<Gamepad>" });
        }

        private static void BuildUiPreset(UnityEngine.Object asset, string assetPath)
        {
            var ui = AddMap(asset, "UI");
            if (ui == null) return;
            BuildUiActions(ui);

            AddScheme(asset, "Keyboard&Mouse", new[] { "<Keyboard>", "<Mouse>" });
            AddScheme(asset, "Gamepad", new[] { "<Gamepad>" });
        }

        private static void BuildUiActions(object ui)
        {
            if (ui == null) return;

            var navigate = AddAct(ui, "Navigate", "PassThrough", "Vector2");
            AddComposite(navigate, "2DVector", new Dictionary<string, string>
            {
                {"up", "<Keyboard>/upArrow"}, {"down", "<Keyboard>/downArrow"},
                {"left", "<Keyboard>/leftArrow"}, {"right", "<Keyboard>/rightArrow"}
            }, "Keyboard&Mouse");
            AddBind(navigate, "<Gamepad>/dpad", "Gamepad");

            var submit = AddAct(ui, "Submit", "Button");
            AddBind(submit, "<Keyboard>/enter", "Keyboard&Mouse");
            AddBind(submit, "<Gamepad>/buttonSouth", "Gamepad");

            var cancel = AddAct(ui, "Cancel", "Button");
            AddBind(cancel, "<Keyboard>/escape", "Keyboard&Mouse");
            AddBind(cancel, "<Gamepad>/buttonEast", "Gamepad");

            var point = AddAct(ui, "Point", "PassThrough", "Vector2");
            AddBind(point, "<Mouse>/position", "Keyboard&Mouse");

            var click = AddAct(ui, "Click", "PassThrough", "");
            AddBind(click, "<Mouse>/leftButton", "Keyboard&Mouse");

            var scrollWheel = AddAct(ui, "ScrollWheel", "PassThrough", "Vector2");
            AddBind(scrollWheel, "<Mouse>/scroll", "Keyboard&Mouse");
        }

        private static void BuildRtsPreset(UnityEngine.Object asset, string assetPath)
        {
            var gameplay = AddMap(asset, "Gameplay");
            if (gameplay == null) return;

            var cameraMove = AddAct(gameplay, "CameraMove", "Value", "Vector2");
            AddComposite(cameraMove, "2DVector", new Dictionary<string, string>
            {
                {"up", "<Keyboard>/w"}, {"down", "<Keyboard>/s"},
                {"left", "<Keyboard>/a"}, {"right", "<Keyboard>/d"}
            }, "Keyboard&Mouse");

            var cameraZoom = AddAct(gameplay, "CameraZoom", "Value", "float");
            AddBind(cameraZoom, "<Mouse>/scroll/y", "Keyboard&Mouse");

            var cameraRotate = AddAct(gameplay, "CameraRotate", "Value", "float");
            AddComposite(cameraRotate, "1DAxis", new Dictionary<string, string>
            {
                {"negative", "<Keyboard>/q"}, {"positive", "<Keyboard>/e"}
            }, "Keyboard&Mouse");

            var select = AddAct(gameplay, "Select", "Button");
            AddBind(select, "<Mouse>/leftButton", "Keyboard&Mouse");

            var command = AddAct(gameplay, "Command", "Button");
            AddBind(command, "<Mouse>/rightButton", "Keyboard&Mouse");

            var pause = AddAct(gameplay, "Pause", "Button");
            AddBind(pause, "<Keyboard>/escape", "Keyboard&Mouse");

            AddScheme(asset, "Keyboard&Mouse", new[] { "<Keyboard>", "<Mouse>" });
        }
    }
}
