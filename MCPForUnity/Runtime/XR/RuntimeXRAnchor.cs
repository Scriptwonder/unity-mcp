using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Runtime.Helpers;
using MCPForUnity.Runtime.Tools;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Runtime.XR
{
    /// <summary>
    /// Runtime tool for XR spatial anchors for MR placement.
    /// Provides a generic anchor system with platform-specific extensions.
    /// </summary>
    [RuntimeMcpTool("runtime_xr_anchor")]
    public static class RuntimeXRAnchor
    {
        // Simple runtime anchor tracking (for generic use)
        private static Dictionary<string, RuntimeAnchorData> s_anchors = new();
        private static int s_anchorCounter = 0;

        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "get_all");

            return action switch
            {
                "create" => CreateAnchor(p),
                "delete" => DeleteAnchor(p),
                "get" => GetAnchor(p),
                "get_all" => GetAllAnchors(p),
                "update_pose" => UpdateAnchorPose(p),
                "attach_object" => AttachObjectToAnchor(p),
                "detach_object" => DetachObjectFromAnchor(p),
                "get_status" => GetAnchorSystemStatus(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
        }

        private static object CreateAnchor(RuntimeToolParams p)
        {
            // Get position
            var posToken = p.GetRaw("position");
            Vector3 position = Vector3.zero;

            if (posToken != null)
            {
                var parsedPos = Tools.Implementations.RuntimeGameObject.ParseVector3(posToken);
                if (parsedPos.HasValue)
                    position = parsedPos.Value;
            }
            else
            {
                // Use current head position if no position provided
                var mainCamera = Camera.main;
                if (mainCamera != null)
                {
                    position = mainCamera.transform.position + mainCamera.transform.forward * 1f;
                }
            }

            // Get rotation
            var rotToken = p.GetRaw("rotation");
            Quaternion rotation = Quaternion.identity;
            if (rotToken != null)
            {
                var euler = Tools.Implementations.RuntimeGameObject.ParseVector3(rotToken);
                if (euler.HasValue)
                    rotation = Quaternion.Euler(euler.Value);
            }

            // Create anchor
            string anchorName = p.Get("name", $"Anchor_{s_anchorCounter++}");
            string anchorId = System.Guid.NewGuid().ToString().Substring(0, 8);

            // Create anchor GameObject
            var anchorGo = new GameObject(anchorName);
            anchorGo.transform.position = position;
            anchorGo.transform.rotation = rotation;

            // Add visual indicator if requested
            if (p.GetBool("add_visual", true))
            {
                CreateAnchorVisual(anchorGo.transform, p.GetFloat("visual_size") ?? 0.1f);
            }

            // Track anchor
            var anchorData = new RuntimeAnchorData
            {
                Id = anchorId,
                Name = anchorName,
                GameObject = anchorGo,
                Position = position,
                Rotation = rotation,
                CreatedTime = Time.time,
                AttachedObjects = new List<GameObject>()
            };

            s_anchors[anchorId] = anchorData;

#if META_XR_SDK
            // If Meta XR SDK is available, try to create a real spatial anchor
            TryCreateMetaAnchor(anchorGo, anchorData);
#endif

            return new RuntimeSuccessResponse($"Created anchor '{anchorName}'", new
            {
                anchorId,
                name = anchorName,
                instanceID = anchorGo.GetInstanceID(),
                position = RuntimeGameObjectSerializer.SerializeVector3(position),
                rotation = new { x = rotation.eulerAngles.x, y = rotation.eulerAngles.y, z = rotation.eulerAngles.z }
            });
        }

        private static void CreateAnchorVisual(Transform parent, float size)
        {
            // Create a simple axis indicator
            var visual = new GameObject("AnchorVisual");
            visual.transform.SetParent(parent, false);

            // Center sphere
            var center = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            center.name = "Center";
            center.transform.SetParent(visual.transform, false);
            center.transform.localScale = Vector3.one * size * 0.3f;
            Object.Destroy(center.GetComponent<Collider>());
            center.GetComponent<Renderer>().material.color = Color.white;

            // X axis (red)
            var xAxis = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            xAxis.name = "XAxis";
            xAxis.transform.SetParent(visual.transform, false);
            xAxis.transform.localScale = new Vector3(size * 0.1f, size * 0.5f, size * 0.1f);
            xAxis.transform.localPosition = new Vector3(size * 0.5f, 0, 0);
            xAxis.transform.localRotation = Quaternion.Euler(0, 0, 90);
            Object.Destroy(xAxis.GetComponent<Collider>());
            xAxis.GetComponent<Renderer>().material.color = Color.red;

            // Y axis (green)
            var yAxis = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            yAxis.name = "YAxis";
            yAxis.transform.SetParent(visual.transform, false);
            yAxis.transform.localScale = new Vector3(size * 0.1f, size * 0.5f, size * 0.1f);
            yAxis.transform.localPosition = new Vector3(0, size * 0.5f, 0);
            Object.Destroy(yAxis.GetComponent<Collider>());
            yAxis.GetComponent<Renderer>().material.color = Color.green;

            // Z axis (blue)
            var zAxis = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            zAxis.name = "ZAxis";
            zAxis.transform.SetParent(visual.transform, false);
            zAxis.transform.localScale = new Vector3(size * 0.1f, size * 0.5f, size * 0.1f);
            zAxis.transform.localPosition = new Vector3(0, 0, size * 0.5f);
            zAxis.transform.localRotation = Quaternion.Euler(90, 0, 0);
            Object.Destroy(zAxis.GetComponent<Collider>());
            zAxis.GetComponent<Renderer>().material.color = Color.blue;
        }

#if META_XR_SDK
        private static void TryCreateMetaAnchor(GameObject anchorGo, RuntimeAnchorData anchorData)
        {
            // Try to add OVRSpatialAnchor component
            try
            {
                var spatialAnchor = anchorGo.AddComponent<OVRSpatialAnchor>();
                anchorData.HasPlatformAnchor = true;
                RuntimeLog.Info($"Created Meta spatial anchor for '{anchorData.Name}'");
            }
            catch (System.Exception e)
            {
                RuntimeLog.Warn($"Could not create Meta spatial anchor: {e.Message}");
            }
        }
#endif

        private static object DeleteAnchor(RuntimeToolParams p)
        {
            string anchorId = p.Get("anchor_id");
            if (string.IsNullOrEmpty(anchorId))
                return new RuntimeErrorResponse("'anchor_id' parameter is required");

            if (!s_anchors.TryGetValue(anchorId, out var anchorData))
                return new RuntimeErrorResponse($"Anchor '{anchorId}' not found");

            // Detach all attached objects first
            foreach (var obj in anchorData.AttachedObjects.ToList())
            {
                if (obj != null)
                    obj.transform.SetParent(null);
            }

            // Destroy anchor GameObject
            if (anchorData.GameObject != null)
                Object.Destroy(anchorData.GameObject);

            s_anchors.Remove(anchorId);

            return new RuntimeSuccessResponse($"Deleted anchor '{anchorData.Name}'", new
            {
                deletedAnchorId = anchorId,
                deletedName = anchorData.Name
            });
        }

        private static object GetAnchor(RuntimeToolParams p)
        {
            string anchorId = p.Get("anchor_id");
            if (string.IsNullOrEmpty(anchorId))
                return new RuntimeErrorResponse("'anchor_id' parameter is required");

            if (!s_anchors.TryGetValue(anchorId, out var anchorData))
                return new RuntimeErrorResponse($"Anchor '{anchorId}' not found");

            return new RuntimeSuccessResponse($"Anchor '{anchorData.Name}'", SerializeAnchor(anchorData));
        }

        private static object GetAllAnchors(RuntimeToolParams p)
        {
            var anchors = s_anchors.Values
                .Where(a => a.GameObject != null)
                .Select(a => SerializeAnchor(a))
                .ToList();

            return new RuntimeSuccessResponse($"Found {anchors.Count} anchors", new
            {
                count = anchors.Count,
                anchors
            });
        }

        private static object SerializeAnchor(RuntimeAnchorData anchor)
        {
            Vector3 currentPos = anchor.GameObject != null ? anchor.GameObject.transform.position : anchor.Position;
            Quaternion currentRot = anchor.GameObject != null ? anchor.GameObject.transform.rotation : anchor.Rotation;

            return new
            {
                anchorId = anchor.Id,
                name = anchor.Name,
                instanceID = anchor.GameObject?.GetInstanceID() ?? -1,
                position = RuntimeGameObjectSerializer.SerializeVector3(currentPos),
                rotation = new { x = currentRot.eulerAngles.x, y = currentRot.eulerAngles.y, z = currentRot.eulerAngles.z },
                attachedObjectCount = anchor.AttachedObjects.Count(o => o != null),
                hasPlatformAnchor = anchor.HasPlatformAnchor,
                ageSeconds = Time.time - anchor.CreatedTime
            };
        }

        private static object UpdateAnchorPose(RuntimeToolParams p)
        {
            string anchorId = p.Get("anchor_id");
            if (string.IsNullOrEmpty(anchorId))
                return new RuntimeErrorResponse("'anchor_id' parameter is required");

            if (!s_anchors.TryGetValue(anchorId, out var anchorData))
                return new RuntimeErrorResponse($"Anchor '{anchorId}' not found");

            if (anchorData.GameObject == null)
                return new RuntimeErrorResponse($"Anchor GameObject has been destroyed");

            var posToken = p.GetRaw("position");
            if (posToken != null)
            {
                var pos = Tools.Implementations.RuntimeGameObject.ParseVector3(posToken);
                if (pos.HasValue)
                {
                    anchorData.GameObject.transform.position = pos.Value;
                    anchorData.Position = pos.Value;
                }
            }

            var rotToken = p.GetRaw("rotation");
            if (rotToken != null)
            {
                var euler = Tools.Implementations.RuntimeGameObject.ParseVector3(rotToken);
                if (euler.HasValue)
                {
                    anchorData.GameObject.transform.eulerAngles = euler.Value;
                    anchorData.Rotation = anchorData.GameObject.transform.rotation;
                }
            }

            return new RuntimeSuccessResponse($"Updated anchor '{anchorData.Name}'", SerializeAnchor(anchorData));
        }

        private static object AttachObjectToAnchor(RuntimeToolParams p)
        {
            string anchorId = p.Get("anchor_id");
            if (string.IsNullOrEmpty(anchorId))
                return new RuntimeErrorResponse("'anchor_id' parameter is required");

            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            if (!s_anchors.TryGetValue(anchorId, out var anchorData))
                return new RuntimeErrorResponse($"Anchor '{anchorId}' not found");

            var targetGo = Tools.Implementations.RuntimeGameObject.FindGameObject(target);
            if (targetGo == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            bool maintainWorldPosition = p.GetBool("maintain_world_position", true);
            targetGo.transform.SetParent(anchorData.GameObject.transform, maintainWorldPosition);

            if (!anchorData.AttachedObjects.Contains(targetGo))
                anchorData.AttachedObjects.Add(targetGo);

            return new RuntimeSuccessResponse($"Attached '{targetGo.name}' to anchor '{anchorData.Name}'", new
            {
                anchorId,
                anchorName = anchorData.Name,
                attachedObject = targetGo.name,
                attachedObjectInstanceID = targetGo.GetInstanceID()
            });
        }

        private static object DetachObjectFromAnchor(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var targetGo = Tools.Implementations.RuntimeGameObject.FindGameObject(target);
            if (targetGo == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            // Find which anchor it's attached to
            string detachedFrom = null;
            foreach (var kvp in s_anchors)
            {
                if (kvp.Value.AttachedObjects.Contains(targetGo))
                {
                    kvp.Value.AttachedObjects.Remove(targetGo);
                    detachedFrom = kvp.Value.Name;
                    break;
                }
            }

            targetGo.transform.SetParent(null);

            return new RuntimeSuccessResponse($"Detached '{targetGo.name}'", new
            {
                detachedObject = targetGo.name,
                detachedFromAnchor = detachedFrom
            });
        }

        private static object GetAnchorSystemStatus(RuntimeToolParams p)
        {
            bool hasPlatformSupport = false;
            string platformName = "Generic";

#if META_XR_SDK
            hasPlatformSupport = true;
            platformName = "Meta XR SDK";
#endif

            return new RuntimeSuccessResponse("Anchor system status", new
            {
                platform = platformName,
                hasPlatformAnchorSupport = hasPlatformSupport,
                activeAnchorCount = s_anchors.Count(a => a.Value.GameObject != null),
                totalAnchorsCreated = s_anchorCounter
            });
        }

        private class RuntimeAnchorData
        {
            public string Id;
            public string Name;
            public GameObject GameObject;
            public Vector3 Position;
            public Quaternion Rotation;
            public float CreatedTime;
            public bool HasPlatformAnchor;
            public List<GameObject> AttachedObjects;
        }
    }
}
