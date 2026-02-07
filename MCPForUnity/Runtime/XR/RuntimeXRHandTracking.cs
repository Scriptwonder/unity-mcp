using System.Collections.Generic;
using MCPForUnity.Runtime.Helpers;
using MCPForUnity.Runtime.Tools;
using Newtonsoft.Json.Linq;
using UnityEngine;

#if UNITY_XR_HANDS
using UnityEngine.XR.Hands;
#endif

namespace MCPForUnity.Runtime.XR
{
    /// <summary>
    /// Runtime tool for XR hand tracking operations.
    /// Requires XR Hands package (com.unity.xr.hands).
    /// </summary>
    [RuntimeMcpTool("manage_xr_hand_tracking")]
    public static class RuntimeXRHandTracking
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "get_status");

            return action switch
            {
                "get_status" => GetHandTrackingStatus(p),
                "get_hand_data" => GetHandData(p),
                "setup_hand_visuals" => SetupHandVisuals(p),
                "get_gesture" => GetGesture(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
        }

        private static object GetHandTrackingStatus(RuntimeToolParams p)
        {
#if UNITY_XR_HANDS
            var subsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);

            bool hasSubsystem = subsystems.Count > 0;
            bool isRunning = false;
            string subsystemName = "None";

            if (hasSubsystem)
            {
                var subsystem = subsystems[0];
                isRunning = subsystem.running;
                subsystemName = subsystem.GetType().Name;
            }

            return new RuntimeSuccessResponse("Hand tracking status retrieved", new
            {
                xrHandsPackageInstalled = true,
                hasHandSubsystem = hasSubsystem,
                isRunning,
                subsystemName,
                note = hasSubsystem ? null : "No XRHandSubsystem active. Ensure XR device supports hand tracking and it's enabled."
            });
#else
            return new RuntimeSuccessResponse("Hand tracking status retrieved", new
            {
                xrHandsPackageInstalled = false,
                hasHandSubsystem = false,
                isRunning = false,
                note = "XR Hands package not installed. Install com.unity.xr.hands for hand tracking support."
            });
#endif
        }

        private static object GetHandData(RuntimeToolParams p)
        {
#if UNITY_XR_HANDS
            var subsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);

            if (subsystems.Count == 0)
                return new RuntimeErrorResponse("No XRHandSubsystem available");

            var subsystem = subsystems[0];
            if (!subsystem.running)
                return new RuntimeErrorResponse("Hand subsystem not running");

            string handedness = p.Get("hand", "both").ToLower();

            var result = new Dictionary<string, object>();

            if (handedness == "left" || handedness == "both")
            {
                var leftHand = subsystem.leftHand;
                result["leftHand"] = SerializeHand(leftHand, "Left");
            }

            if (handedness == "right" || handedness == "both")
            {
                var rightHand = subsystem.rightHand;
                result["rightHand"] = SerializeHand(rightHand, "Right");
            }

            return new RuntimeSuccessResponse("Hand data retrieved", result);
#else
            return new RuntimeErrorResponse("XR Hands package not installed. Install com.unity.xr.hands");
#endif
        }

#if UNITY_XR_HANDS
        private static object SerializeHand(XRHand hand, string handName)
        {
            if (!hand.isTracked)
            {
                return new
                {
                    name = handName,
                    isTracked = false
                };
            }

            // Get key joint positions
            var joints = new Dictionary<string, object>();

            // Palm
            if (hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palmPose))
            {
                joints["palm"] = new
                {
                    position = RuntimeGameObjectSerializer.SerializeVector3(palmPose.position),
                    rotation = new { x = palmPose.rotation.x, y = palmPose.rotation.y, z = palmPose.rotation.z, w = palmPose.rotation.w }
                };
            }

            // Wrist
            if (hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var wristPose))
            {
                joints["wrist"] = new
                {
                    position = RuntimeGameObjectSerializer.SerializeVector3(wristPose.position)
                };
            }

            // Index finger tip
            if (hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var indexTipPose))
            {
                joints["indexTip"] = new
                {
                    position = RuntimeGameObjectSerializer.SerializeVector3(indexTipPose.position)
                };
            }

            // Thumb tip
            if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumbTipPose))
            {
                joints["thumbTip"] = new
                {
                    position = RuntimeGameObjectSerializer.SerializeVector3(thumbTipPose.position)
                };
            }

            // Middle finger tip
            if (hand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out var middleTipPose))
            {
                joints["middleTip"] = new
                {
                    position = RuntimeGameObjectSerializer.SerializeVector3(middleTipPose.position)
                };
            }

            return new
            {
                name = handName,
                isTracked = true,
                joints
            };
        }
#endif

        private static object SetupHandVisuals(RuntimeToolParams p)
        {
#if UNITY_XR_HANDS
            // This creates simple sphere-based hand visualization
            // For production, you'd use proper hand meshes

            var subsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);

            if (subsystems.Count == 0)
                return new RuntimeErrorResponse("No XRHandSubsystem available");

            string handedness = p.Get("hand", "both").ToLower();
            float jointSize = p.GetFloat("joint_size") ?? 0.01f;

            var created = new List<string>();

            if (handedness == "left" || handedness == "both")
            {
                CreateHandVisualization("LeftHandVisual", jointSize);
                created.Add("LeftHandVisual");
            }

            if (handedness == "right" || handedness == "both")
            {
                CreateHandVisualization("RightHandVisual", jointSize);
                created.Add("RightHandVisual");
            }

            return new RuntimeSuccessResponse("Hand visuals setup", new
            {
                createdObjects = created,
                jointSize,
                note = "Basic sphere-based visuals created. For production use, implement proper hand mesh rendering."
            });
#else
            return new RuntimeErrorResponse("XR Hands package not installed");
#endif
        }

#if UNITY_XR_HANDS
        private static void CreateHandVisualization(string name, float jointSize)
        {
            var existingVisual = GameObject.Find(name);
            if (existingVisual != null)
                Object.Destroy(existingVisual);

            var handRoot = new GameObject(name);
            Object.DontDestroyOnLoad(handRoot);

            // Create joint spheres for key joints
            var jointNames = new[] { "Palm", "Wrist", "IndexTip", "ThumbTip", "MiddleTip", "RingTip", "LittleTip" };

            foreach (var jointName in jointNames)
            {
                var jointSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                jointSphere.name = jointName;
                jointSphere.transform.SetParent(handRoot.transform, false);
                jointSphere.transform.localScale = Vector3.one * jointSize;

                // Remove collider (we don't need collision for visualization)
                var collider = jointSphere.GetComponent<Collider>();
                if (collider != null) Object.Destroy(collider);

                // Set material color
                var renderer = jointSphere.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material = new Material(Shader.Find("Standard"));
                    renderer.material.color = new Color(0.5f, 0.8f, 1f, 0.8f);
                }
            }

            // Add a simple update script to position joints
            handRoot.AddComponent<SimpleHandVisualUpdater>().Initialize(name.Contains("Left"));
        }
#endif

        private static object GetGesture(RuntimeToolParams p)
        {
#if UNITY_XR_HANDS
            var subsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);

            if (subsystems.Count == 0)
                return new RuntimeErrorResponse("No XRHandSubsystem available");

            var subsystem = subsystems[0];
            string handedness = p.Get("hand", "right").ToLower();

            var hand = handedness == "left" ? subsystem.leftHand : subsystem.rightHand;

            if (!hand.isTracked)
                return new RuntimeErrorResponse($"{handedness} hand not tracked");

            // Simple gesture detection based on finger curl
            var gesture = DetectSimpleGesture(hand);

            return new RuntimeSuccessResponse($"Gesture detected for {handedness} hand", new
            {
                hand = handedness,
                gesture = gesture.gestureName,
                confidence = gesture.confidence
            });
#else
            return new RuntimeErrorResponse("XR Hands package not installed");
#endif
        }

#if UNITY_XR_HANDS
        private static (string gestureName, float confidence) DetectSimpleGesture(XRHand hand)
        {
            // Simple gesture detection by checking distances between fingertips and palm

            if (!hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palmPose))
                return ("unknown", 0f);

            if (!hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var indexTipPose))
                return ("unknown", 0f);

            if (!hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumbTipPose))
                return ("unknown", 0f);

            if (!hand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out var middleTipPose))
                return ("unknown", 0f);

            if (!hand.GetJoint(XRHandJointID.RingTip).TryGetPose(out var ringTipPose))
                return ("unknown", 0f);

            if (!hand.GetJoint(XRHandJointID.LittleTip).TryGetPose(out var littleTipPose))
                return ("unknown", 0f);

            float indexDistance = Vector3.Distance(indexTipPose.position, palmPose.position);
            float thumbDistance = Vector3.Distance(thumbTipPose.position, palmPose.position);
            float middleDistance = Vector3.Distance(middleTipPose.position, palmPose.position);
            float ringDistance = Vector3.Distance(ringTipPose.position, palmPose.position);
            float littleDistance = Vector3.Distance(littleTipPose.position, palmPose.position);

            // Pinch detection (thumb tip close to index tip)
            float pinchDistance = Vector3.Distance(thumbTipPose.position, indexTipPose.position);
            if (pinchDistance < 0.03f)
                return ("pinch", Mathf.Clamp01(1f - (pinchDistance / 0.03f)));

            // Fist detection (all fingertips close to palm)
            float avgFingerDistance = (indexDistance + middleDistance + ringDistance + littleDistance) / 4f;
            if (avgFingerDistance < 0.06f)
                return ("fist", Mathf.Clamp01(1f - (avgFingerDistance / 0.06f)));

            // Point detection (index extended, others curled)
            if (indexDistance > 0.1f && middleDistance < 0.06f && ringDistance < 0.06f)
                return ("point", 0.8f);

            // Open hand (all fingers extended)
            if (indexDistance > 0.1f && middleDistance > 0.1f && ringDistance > 0.1f && littleDistance > 0.08f)
                return ("open", 0.9f);

            return ("neutral", 0.5f);
        }
#endif
    }

#if UNITY_XR_HANDS
    /// <summary>
    /// Simple MonoBehaviour to update hand visualization positions each frame.
    /// </summary>
    public class SimpleHandVisualUpdater : MonoBehaviour
    {
        private bool _isLeft;
        private Dictionary<string, Transform> _jointTransforms;
        private Dictionary<string, XRHandJointID> _jointMapping;

        public void Initialize(bool isLeft)
        {
            _isLeft = isLeft;
            _jointTransforms = new Dictionary<string, Transform>();
            _jointMapping = new Dictionary<string, XRHandJointID>
            {
                { "Palm", XRHandJointID.Palm },
                { "Wrist", XRHandJointID.Wrist },
                { "IndexTip", XRHandJointID.IndexTip },
                { "ThumbTip", XRHandJointID.ThumbTip },
                { "MiddleTip", XRHandJointID.MiddleTip },
                { "RingTip", XRHandJointID.RingTip },
                { "LittleTip", XRHandJointID.LittleTip }
            };

            foreach (Transform child in transform)
            {
                _jointTransforms[child.name] = child;
            }
        }

        void Update()
        {
            var subsystems = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subsystems);

            if (subsystems.Count == 0) return;

            var hand = _isLeft ? subsystems[0].leftHand : subsystems[0].rightHand;

            if (!hand.isTracked)
            {
                gameObject.SetActive(false);
                return;
            }

            gameObject.SetActive(true);

            foreach (var kvp in _jointMapping)
            {
                if (_jointTransforms.TryGetValue(kvp.Key, out var jointTransform))
                {
                    if (hand.GetJoint(kvp.Value).TryGetPose(out var pose))
                    {
                        jointTransform.position = pose.position;
                        jointTransform.rotation = pose.rotation;
                    }
                }
            }
        }
    }
#endif
}
