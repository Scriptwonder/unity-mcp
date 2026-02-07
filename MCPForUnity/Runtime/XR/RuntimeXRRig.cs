using MCPForUnity.Runtime.Helpers;
using MCPForUnity.Runtime.Tools;
using Newtonsoft.Json.Linq;
using UnityEngine;

#if UNITY_XR_MANAGEMENT
using UnityEngine.XR.Management;
#endif

#if UNITY_XR_INTERACTION_TOOLKIT
using UnityEngine.XR.Interaction.Toolkit;
#endif

#if UNITY_XR_CORE_UTILS
using Unity.XR.CoreUtils;
#endif

namespace MCPForUnity.Runtime.XR
{
    /// <summary>
    /// Runtime tool for XR rig/origin operations: create, configure, query.
    /// </summary>
    [RuntimeMcpTool("manage_xr_rig")]
    public static class RuntimeXRRig
    {
        public static object HandleCommand(JObject @params)
        {
#if !UNITY_XR_MANAGEMENT && !UNITY_XR_INTERACTION_TOOLKIT
            return new RuntimeErrorResponse("XR packages not installed. Install com.unity.xr.management and com.unity.xr.interaction.toolkit");
#else
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "get_info");

            return action switch
            {
                "create" => CreateXRRig(p),
                "get_info" => GetXRInfo(p),
                "configure" => ConfigureXRRig(p),
                "set_tracking_origin" => SetTrackingOrigin(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
#endif
        }

#if UNITY_XR_MANAGEMENT || UNITY_XR_INTERACTION_TOOLKIT
        private static object CreateXRRig(RuntimeToolParams p)
        {
            string rigName = p.Get("name", "XR Origin");

#if UNITY_XR_CORE_UTILS && UNITY_XR_INTERACTION_TOOLKIT
            // Check if XR Origin already exists
            var existingRig = Object.FindObjectOfType<XROrigin>();
            if (existingRig != null && !p.GetBool("force", false))
            {
                return new RuntimeErrorResponse($"XR Origin already exists: '{existingRig.gameObject.name}'. Use force=true to create another.");
            }

            // Create XR Origin
            var rigGo = new GameObject(rigName);
            var xrOrigin = rigGo.AddComponent<XROrigin>();

            // Create Camera Offset
            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(rigGo.transform, false);

            // Create Main Camera
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            cameraGo.transform.SetParent(cameraOffset.transform, false);

            var camera = cameraGo.AddComponent<Camera>();
            camera.nearClipPlane = 0.01f;
            camera.clearFlags = CameraClearFlags.Skybox;

            cameraGo.AddComponent<AudioListener>();

            // Add TrackedPoseDriver for head tracking
#if UNITY_INPUT_SYSTEM
            var poseDriver = cameraGo.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
            poseDriver.trackingType = UnityEngine.InputSystem.XR.TrackedPoseDriver.TrackingType.RotationAndPosition;
#endif

            // Configure XR Origin
            xrOrigin.Camera = camera;
            xrOrigin.CameraFloorOffsetObject = cameraOffset;

            // Set tracking origin mode
            string trackingMode = p.Get("tracking_origin", "floor");
            xrOrigin.RequestedTrackingOriginMode = trackingMode.ToLower() switch
            {
                "floor" => XROrigin.TrackingOriginMode.Floor,
                "device" => XROrigin.TrackingOriginMode.Device,
                _ => XROrigin.TrackingOriginMode.NotSpecified
            };

            // Optionally add controller GameObjects
            if (p.GetBool("include_controllers", true))
            {
                CreateController(cameraOffset.transform, "Left Controller", true);
                CreateController(cameraOffset.transform, "Right Controller", false);
            }

            // Set position if provided
            var posToken = p.GetRaw("position");
            if (posToken != null)
            {
                var pos = Tools.Implementations.RuntimeGameObject.ParseVector3(posToken);
                if (pos.HasValue) rigGo.transform.position = pos.Value;
            }

            return new RuntimeSuccessResponse($"Created XR Origin '{rigName}'", new
            {
                name = rigName,
                instanceID = rigGo.GetInstanceID(),
                cameraInstanceID = cameraGo.GetInstanceID(),
                trackingOriginMode = xrOrigin.RequestedTrackingOriginMode.ToString(),
                hasControllers = p.GetBool("include_controllers", true)
            });
#else
            return new RuntimeErrorResponse("XR Core Utils and XR Interaction Toolkit required. Install com.unity.xr.core-utils and com.unity.xr.interaction.toolkit");
#endif
        }

#if UNITY_XR_CORE_UTILS && UNITY_XR_INTERACTION_TOOLKIT
        private static void CreateController(Transform parent, string name, bool isLeft)
        {
            var controllerGo = new GameObject(name);
            controllerGo.transform.SetParent(parent, false);

#if UNITY_INPUT_SYSTEM
            var poseDriver = controllerGo.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
            poseDriver.trackingType = UnityEngine.InputSystem.XR.TrackedPoseDriver.TrackingType.RotationAndPosition;
#endif

            // Add XR Controller
            var controller = controllerGo.AddComponent<ActionBasedController>();

            // Note: Input actions need to be assigned via script or inspector
            // The user will need to configure InputActionReferences for their XR setup
        }
#endif

        private static object GetXRInfo(RuntimeToolParams p)
        {
#if UNITY_XR_MANAGEMENT
            var xrSettings = XRGeneralSettings.Instance;
            bool xrInitialized = xrSettings != null && xrSettings.Manager != null && xrSettings.Manager.isInitializationComplete;
            string activeLoader = xrSettings?.Manager?.activeLoader?.name ?? "None";
#else
            bool xrInitialized = false;
            string activeLoader = "XR Management not installed";
#endif

#if UNITY_XR_CORE_UTILS
            var xrOrigin = Object.FindObjectOfType<XROrigin>();
            object originInfo = null;

            if (xrOrigin != null)
            {
                originInfo = new
                {
                    name = xrOrigin.gameObject.name,
                    instanceID = xrOrigin.gameObject.GetInstanceID(),
                    cameraPosition = xrOrigin.Camera != null ?
                        RuntimeGameObjectSerializer.SerializeVector3(xrOrigin.Camera.transform.position) : null,
                    trackingOriginMode = xrOrigin.RequestedTrackingOriginMode.ToString(),
                    cameraYOffset = xrOrigin.CameraYOffset
                };
            }

            return new RuntimeSuccessResponse("XR Info retrieved", new
            {
                xrInitialized,
                activeLoader,
                hasXROrigin = xrOrigin != null,
                xrOrigin = originInfo,
                inputDevices = GetInputDeviceInfo()
            });
#else
            return new RuntimeSuccessResponse("XR Info retrieved", new
            {
                xrInitialized,
                activeLoader,
                hasXROrigin = false,
                message = "XR Core Utils not installed"
            });
#endif
        }

        private static object[] GetInputDeviceInfo()
        {
            var devices = new System.Collections.Generic.List<object>();

            var inputDevices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
            UnityEngine.XR.InputDevices.GetDevices(inputDevices);

            foreach (var device in inputDevices)
            {
                devices.Add(new
                {
                    name = device.name,
                    characteristics = device.characteristics.ToString(),
                    isValid = device.isValid
                });
            }

            return devices.ToArray();
        }

        private static object ConfigureXRRig(RuntimeToolParams p)
        {
#if UNITY_XR_CORE_UTILS
            var xrOrigin = Object.FindObjectOfType<XROrigin>();
            if (xrOrigin == null)
                return new RuntimeErrorResponse("No XR Origin found in scene");

            // Configure camera Y offset
            float? cameraYOffset = p.GetFloat("camera_y_offset");
            if (cameraYOffset.HasValue)
            {
                xrOrigin.CameraYOffset = cameraYOffset.Value;
            }

            // Configure tracking origin mode
            string trackingMode = p.Get("tracking_origin");
            if (!string.IsNullOrEmpty(trackingMode))
            {
                xrOrigin.RequestedTrackingOriginMode = trackingMode.ToLower() switch
                {
                    "floor" => XROrigin.TrackingOriginMode.Floor,
                    "device" => XROrigin.TrackingOriginMode.Device,
                    _ => XROrigin.TrackingOriginMode.NotSpecified
                };
            }

            return new RuntimeSuccessResponse($"Configured XR Origin '{xrOrigin.gameObject.name}'", new
            {
                name = xrOrigin.gameObject.name,
                cameraYOffset = xrOrigin.CameraYOffset,
                trackingOriginMode = xrOrigin.RequestedTrackingOriginMode.ToString()
            });
#else
            return new RuntimeErrorResponse("XR Core Utils not installed");
#endif
        }

        private static object SetTrackingOrigin(RuntimeToolParams p)
        {
#if UNITY_XR_CORE_UTILS
            var xrOrigin = Object.FindObjectOfType<XROrigin>();
            if (xrOrigin == null)
                return new RuntimeErrorResponse("No XR Origin found in scene");

            string mode = p.Get("mode", "floor");

            xrOrigin.RequestedTrackingOriginMode = mode.ToLower() switch
            {
                "floor" => XROrigin.TrackingOriginMode.Floor,
                "device" => XROrigin.TrackingOriginMode.Device,
                _ => XROrigin.TrackingOriginMode.NotSpecified
            };

            return new RuntimeSuccessResponse($"Set tracking origin to '{mode}'", new
            {
                trackingOriginMode = xrOrigin.RequestedTrackingOriginMode.ToString()
            });
#else
            return new RuntimeErrorResponse("XR Core Utils not installed");
#endif
        }
#endif
    }
}
