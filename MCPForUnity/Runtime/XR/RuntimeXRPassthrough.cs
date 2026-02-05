using MCPForUnity.Runtime.Helpers;
using MCPForUnity.Runtime.Tools;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Runtime.XR
{
    /// <summary>
    /// Runtime tool for XR passthrough (MR) operations, primarily for Quest.
    /// Supports both Meta XR SDK and OpenXR passthrough approaches.
    /// </summary>
    [RuntimeMcpTool("runtime_xr_passthrough")]
    public static class RuntimeXRPassthrough
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "get_status");

            return action switch
            {
                "enable" => EnablePassthrough(p),
                "disable" => DisablePassthrough(p),
                "get_status" => GetPassthroughStatus(p),
                "configure" => ConfigurePassthrough(p),
                "set_style" => SetPassthroughStyle(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
        }

        private static object EnablePassthrough(RuntimeToolParams p)
        {
#if META_XR_SDK
            // Meta XR SDK approach
            var passthroughLayer = Object.FindObjectOfType<OVRPassthroughLayer>();

            if (passthroughLayer == null)
            {
                // Try to find or create OVRCameraRig
                var cameraRig = Object.FindObjectOfType<OVRCameraRig>();
                if (cameraRig == null)
                {
                    return new RuntimeErrorResponse("No OVRCameraRig found. Create an OVR Camera Rig first or use runtime_xr_rig create with Meta XR SDK.");
                }

                // Add passthrough layer
                passthroughLayer = cameraRig.gameObject.AddComponent<OVRPassthroughLayer>();
                passthroughLayer.overlayType = OVROverlay.OverlayType.Underlay;
            }

            passthroughLayer.hidden = false;

            // Set camera background to clear/transparent for passthrough to show
            var cameras = Object.FindObjectsOfType<Camera>();
            foreach (var cam in cameras)
            {
                if (cam.CompareTag("MainCamera") || cam.GetComponent<OVRCameraRig>() != null)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = Color.clear;
                }
            }

            return new RuntimeSuccessResponse("Passthrough enabled (Meta XR SDK)", new
            {
                enabled = true,
                method = "meta_xr_sdk",
                overlayType = passthroughLayer.overlayType.ToString()
            });
#else
            // Generic approach - set camera background for AR/MR
            var camera = Camera.main;
            if (camera == null)
            {
                var cameras = Object.FindObjectsOfType<Camera>();
                camera = cameras.Length > 0 ? cameras[0] : null;
            }

            if (camera == null)
                return new RuntimeErrorResponse("No camera found in scene");

            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;

            return new RuntimeSuccessResponse("Passthrough enabled (generic - camera set to transparent)", new
            {
                enabled = true,
                method = "generic_transparent",
                note = "For Quest passthrough, install Meta XR SDK (com.meta.xr.sdk.core)"
            });
#endif
        }

        private static object DisablePassthrough(RuntimeToolParams p)
        {
#if META_XR_SDK
            var passthroughLayer = Object.FindObjectOfType<OVRPassthroughLayer>();
            if (passthroughLayer != null)
            {
                passthroughLayer.hidden = true;
            }

            // Restore camera to skybox
            var cameras = Object.FindObjectsOfType<Camera>();
            foreach (var cam in cameras)
            {
                if (cam.CompareTag("MainCamera"))
                {
                    cam.clearFlags = CameraClearFlags.Skybox;
                }
            }

            return new RuntimeSuccessResponse("Passthrough disabled", new
            {
                enabled = false
            });
#else
            var camera = Camera.main;
            if (camera != null)
            {
                camera.clearFlags = CameraClearFlags.Skybox;
            }

            return new RuntimeSuccessResponse("Passthrough disabled (camera restored to skybox)", new
            {
                enabled = false
            });
#endif
        }

        private static object GetPassthroughStatus(RuntimeToolParams p)
        {
#if META_XR_SDK
            var passthroughLayer = Object.FindObjectOfType<OVRPassthroughLayer>();

            if (passthroughLayer != null)
            {
                return new RuntimeSuccessResponse("Passthrough status retrieved", new
                {
                    hasPassthroughLayer = true,
                    enabled = !passthroughLayer.hidden,
                    overlayType = passthroughLayer.overlayType.ToString(),
                    textureOpacity = passthroughLayer.textureOpacity_,
                    edgeRenderingEnabled = passthroughLayer.edgeRenderingEnabled,
                    method = "meta_xr_sdk"
                });
            }

            return new RuntimeSuccessResponse("Passthrough status retrieved", new
            {
                hasPassthroughLayer = false,
                enabled = false,
                method = "meta_xr_sdk",
                note = "No OVRPassthroughLayer found. Use 'enable' action to create one."
            });
#else
            var camera = Camera.main;
            bool isClearBackground = camera != null &&
                                     camera.clearFlags == CameraClearFlags.SolidColor &&
                                     camera.backgroundColor == Color.clear;

            return new RuntimeSuccessResponse("Passthrough status retrieved", new
            {
                hasPassthroughLayer = false,
                enabled = isClearBackground,
                method = "generic_transparent",
                note = "Meta XR SDK not installed. Using generic transparent camera approach."
            });
#endif
        }

        private static object ConfigurePassthrough(RuntimeToolParams p)
        {
#if META_XR_SDK
            var passthroughLayer = Object.FindObjectOfType<OVRPassthroughLayer>();
            if (passthroughLayer == null)
                return new RuntimeErrorResponse("No OVRPassthroughLayer found. Enable passthrough first.");

            // Configure opacity
            float? opacity = p.GetFloat("opacity");
            if (opacity.HasValue)
            {
                passthroughLayer.textureOpacity_ = Mathf.Clamp01(opacity.Value);
            }

            // Configure edge rendering
            if (p.Has("edge_rendering"))
            {
                passthroughLayer.edgeRenderingEnabled = p.GetBool("edge_rendering", false);
            }

            // Configure overlay type
            string overlayType = p.Get("overlay_type");
            if (!string.IsNullOrEmpty(overlayType))
            {
                passthroughLayer.overlayType = overlayType.ToLower() switch
                {
                    "underlay" => OVROverlay.OverlayType.Underlay,
                    "overlay" => OVROverlay.OverlayType.Overlay,
                    _ => passthroughLayer.overlayType
                };
            }

            return new RuntimeSuccessResponse("Passthrough configured", new
            {
                textureOpacity = passthroughLayer.textureOpacity_,
                edgeRenderingEnabled = passthroughLayer.edgeRenderingEnabled,
                overlayType = passthroughLayer.overlayType.ToString()
            });
#else
            return new RuntimeErrorResponse("Meta XR SDK required for passthrough configuration. Install com.meta.xr.sdk.core");
#endif
        }

        private static object SetPassthroughStyle(RuntimeToolParams p)
        {
#if META_XR_SDK
            var passthroughLayer = Object.FindObjectOfType<OVRPassthroughLayer>();
            if (passthroughLayer == null)
                return new RuntimeErrorResponse("No OVRPassthroughLayer found. Enable passthrough first.");

            string style = p.Get("style", "default");

            switch (style.ToLower())
            {
                case "default":
                    passthroughLayer.textureOpacity_ = 1.0f;
                    passthroughLayer.edgeRenderingEnabled = false;
                    break;

                case "stylized":
                    passthroughLayer.edgeRenderingEnabled = true;
                    break;

                case "dimmed":
                    passthroughLayer.textureOpacity_ = 0.5f;
                    break;

                case "bright":
                    passthroughLayer.textureOpacity_ = 1.0f;
                    // Brightness adjustment would need additional OVRPassthroughLayer properties
                    break;

                default:
                    return new RuntimeErrorResponse($"Unknown style: '{style}'. Use: default, stylized, dimmed, bright");
            }

            return new RuntimeSuccessResponse($"Passthrough style set to '{style}'", new
            {
                style,
                textureOpacity = passthroughLayer.textureOpacity_,
                edgeRenderingEnabled = passthroughLayer.edgeRenderingEnabled
            });
#else
            return new RuntimeErrorResponse("Meta XR SDK required for passthrough styles. Install com.meta.xr.sdk.core");
#endif
        }
    }
}
