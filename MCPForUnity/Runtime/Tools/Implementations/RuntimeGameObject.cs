using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Runtime.Tools.Implementations
{
    /// <summary>
    /// Runtime tool for GameObject operations: create, modify, delete, duplicate, find.
    /// </summary>
    [RuntimeMcpTool("runtime_gameobject")]
    public static class RuntimeGameObject
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "create");

            return action switch
            {
                "create" => Create(p),
                "delete" => Delete(p),
                "duplicate" => Duplicate(p),
                "get" => GetGameObject(p),
                "modify" => Modify(p),
                "find" => Find(p),
                "create_primitive" => CreatePrimitive(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
        }

        private static object Create(RuntimeToolParams p)
        {
            string name = p.Get("name");
            if (string.IsNullOrEmpty(name))
                return new RuntimeErrorResponse("'name' parameter is required");

            var go = new GameObject(name);

            // Set parent
            string parentTarget = p.Get("parent");
            if (!string.IsNullOrEmpty(parentTarget))
            {
                var parentGo = FindGameObject(parentTarget);
                if (parentGo != null)
                {
                    go.transform.SetParent(parentGo.transform, false);
                }
            }

            // Set transform
            ApplyTransform(go.transform, p);

            // Set tag
            string tag = p.Get("tag");
            if (!string.IsNullOrEmpty(tag))
            {
                try { go.tag = tag; }
                catch { RuntimeLog.Warn($"Failed to set tag '{tag}' - tag may not exist"); }
            }

            // Set layer
            string layerName = p.Get("layer");
            if (!string.IsNullOrEmpty(layerName))
            {
                int layer = LayerMask.NameToLayer(layerName);
                if (layer != -1) go.layer = layer;
            }

            // Set active
            bool? active = p.GetBool("active", true) ? true : (bool?)null;
            if (active.HasValue) go.SetActive(active.Value);

            return new RuntimeSuccessResponse($"Created GameObject '{name}'",
                RuntimeGameObjectSerializer.GetGameObjectData(go));
        }

        private static object CreatePrimitive(RuntimeToolParams p)
        {
            string primitiveTypeStr = p.Get("primitive_type", "Cube");
            string name = p.Get("name");

            if (!Enum.TryParse<PrimitiveType>(primitiveTypeStr, true, out var primitiveType))
            {
                return new RuntimeErrorResponse($"Invalid primitive type: '{primitiveTypeStr}'. Valid types: Sphere, Capsule, Cylinder, Cube, Plane, Quad");
            }

            var go = GameObject.CreatePrimitive(primitiveType);

            if (!string.IsNullOrEmpty(name))
            {
                go.name = name;
            }

            // Set parent
            string parentTarget = p.Get("parent");
            if (!string.IsNullOrEmpty(parentTarget))
            {
                var parentGo = FindGameObject(parentTarget);
                if (parentGo != null)
                {
                    go.transform.SetParent(parentGo.transform, false);
                }
            }

            // Set transform
            ApplyTransform(go.transform, p);

            return new RuntimeSuccessResponse($"Created {primitiveType} '{go.name}'",
                RuntimeGameObjectSerializer.GetGameObjectData(go));
        }

        private static object Delete(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var go = FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            string name = go.name;
            int id = go.GetInstanceID();

            UnityEngine.Object.Destroy(go);

            return new RuntimeSuccessResponse($"Deleted GameObject '{name}'", new
            {
                deletedName = name,
                deletedInstanceID = id
            });
        }

        private static object Duplicate(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var original = FindGameObject(target);
            if (original == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            var duplicate = UnityEngine.Object.Instantiate(original);
            duplicate.name = p.Get("name", original.name + " (Copy)");

            // Optionally set new parent
            string parentTarget = p.Get("parent");
            if (!string.IsNullOrEmpty(parentTarget))
            {
                var parentGo = FindGameObject(parentTarget);
                if (parentGo != null)
                {
                    duplicate.transform.SetParent(parentGo.transform, false);
                }
            }

            return new RuntimeSuccessResponse($"Duplicated '{original.name}' as '{duplicate.name}'",
                RuntimeGameObjectSerializer.GetGameObjectData(duplicate));
        }

        private static object GetGameObject(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var go = FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            bool includeComponents = p.GetBool("include_components", false);

            var data = RuntimeGameObjectSerializer.GetGameObjectData(go);

            if (includeComponents)
            {
                var components = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => RuntimeGameObjectSerializer.SerializeComponentBasic(c))
                    .ToList();

                return new RuntimeSuccessResponse($"Retrieved GameObject '{go.name}'", new
                {
                    gameObject = data,
                    components
                });
            }

            return new RuntimeSuccessResponse($"Retrieved GameObject '{go.name}'", data);
        }

        private static object Modify(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var go = FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            // Modify name
            string newName = p.Get("name");
            if (!string.IsNullOrEmpty(newName))
            {
                go.name = newName;
            }

            // Modify tag
            string tag = p.Get("tag");
            if (!string.IsNullOrEmpty(tag))
            {
                try { go.tag = tag; }
                catch { RuntimeLog.Warn($"Failed to set tag '{tag}'"); }
            }

            // Modify layer
            string layerName = p.Get("layer");
            if (!string.IsNullOrEmpty(layerName))
            {
                int layer = LayerMask.NameToLayer(layerName);
                if (layer != -1) go.layer = layer;
            }

            // Modify active state
            if (p.Has("active"))
            {
                go.SetActive(p.GetBool("active", true));
            }

            // Modify parent
            if (p.Has("parent"))
            {
                string parentTarget = p.Get("parent");
                if (string.IsNullOrEmpty(parentTarget))
                {
                    go.transform.SetParent(null);
                }
                else
                {
                    var parentGo = FindGameObject(parentTarget);
                    if (parentGo != null)
                    {
                        go.transform.SetParent(parentGo.transform, true);
                    }
                }
            }

            // Modify transform
            ApplyTransform(go.transform, p);

            return new RuntimeSuccessResponse($"Modified GameObject '{go.name}'",
                RuntimeGameObjectSerializer.GetGameObjectData(go));
        }

        private static object Find(RuntimeToolParams p)
        {
            string searchTerm = p.Get("search_term");
            if (string.IsNullOrEmpty(searchTerm))
                return new RuntimeErrorResponse("'search_term' parameter is required");

            string method = p.Get("search_method", "by_name");
            int limit = p.GetInt("limit") ?? 50;
            bool includeInactive = p.GetBool("include_inactive", true);

            List<GameObject> results = new();

            switch (method.ToLower())
            {
                case "by_name":
                    var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>(includeInactive);
                    results = allObjects.Where(go => go.name.Contains(searchTerm)).Take(limit).ToList();
                    break;

                case "by_tag":
                    try
                    {
                        results = GameObject.FindGameObjectsWithTag(searchTerm).Take(limit).ToList();
                    }
                    catch
                    {
                        return new RuntimeErrorResponse($"Tag '{searchTerm}' is not defined");
                    }
                    break;

                case "by_id":
                    if (int.TryParse(searchTerm, out int instanceId))
                    {
                        var allGos = UnityEngine.Object.FindObjectsOfType<GameObject>(includeInactive);
                        var found = allGos.FirstOrDefault(go => go.GetInstanceID() == instanceId);
                        if (found != null) results.Add(found);
                    }
                    break;

                case "by_path":
                    var foundByPath = GameObject.Find(searchTerm);
                    if (foundByPath != null) results.Add(foundByPath);
                    break;

                default:
                    return new RuntimeErrorResponse($"Unknown search method: {method}. Use: by_name, by_tag, by_id, by_path");
            }

            var serialized = results.Select(go => RuntimeGameObjectSerializer.GetGameObjectSummary(go)).ToList();

            return new RuntimeSuccessResponse($"Found {results.Count} GameObjects", new
            {
                searchTerm,
                searchMethod = method,
                count = results.Count,
                results = serialized
            });
        }

        private static void ApplyTransform(Transform t, RuntimeToolParams p)
        {
            var positionToken = p.GetRaw("position");
            if (positionToken != null)
            {
                var pos = ParseVector3(positionToken);
                if (pos.HasValue) t.position = pos.Value;
            }

            var localPositionToken = p.GetRaw("local_position");
            if (localPositionToken != null)
            {
                var pos = ParseVector3(localPositionToken);
                if (pos.HasValue) t.localPosition = pos.Value;
            }

            var rotationToken = p.GetRaw("rotation");
            if (rotationToken != null)
            {
                var rot = ParseVector3(rotationToken);
                if (rot.HasValue) t.eulerAngles = rot.Value;
            }

            var localRotationToken = p.GetRaw("local_rotation");
            if (localRotationToken != null)
            {
                var rot = ParseVector3(localRotationToken);
                if (rot.HasValue) t.localEulerAngles = rot.Value;
            }

            var scaleToken = p.GetRaw("scale");
            if (scaleToken != null)
            {
                var scale = ParseVector3(scaleToken);
                if (scale.HasValue) t.localScale = scale.Value;
            }
        }

        internal static GameObject FindGameObject(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;

            // Try by instance ID first
            if (int.TryParse(target, out int instanceId))
            {
                var allGos = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
                var found = allGos.FirstOrDefault(go => go.GetInstanceID() == instanceId);
                if (found != null) return found;
            }

            // Try by path (starts with /)
            if (target.StartsWith("/"))
            {
                var found = GameObject.Find(target);
                if (found != null) return found;
            }

            // Try by name
            {
                var allGos = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
                var found = allGos.FirstOrDefault(go => go.name == target);
                if (found != null) return found;
            }

            // Try partial match
            {
                var allGos = UnityEngine.Object.FindObjectsOfType<GameObject>(true);
                return allGos.FirstOrDefault(go => go.name.Contains(target));
            }
        }

        internal static Vector3? ParseVector3(JToken token)
        {
            if (token == null) return null;

            // Array format: [x, y, z]
            if (token is JArray arr && arr.Count >= 3)
            {
                return new Vector3(
                    arr[0].Value<float>(),
                    arr[1].Value<float>(),
                    arr[2].Value<float>()
                );
            }

            // Object format: {x: 1, y: 2, z: 3}
            if (token is JObject obj)
            {
                float x = obj["x"]?.Value<float>() ?? 0;
                float y = obj["y"]?.Value<float>() ?? 0;
                float z = obj["z"]?.Value<float>() ?? 0;
                return new Vector3(x, y, z);
            }

            return null;
        }
    }
}
