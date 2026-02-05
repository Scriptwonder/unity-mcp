using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MCPForUnity.Runtime.Helpers
{
    /// <summary>
    /// Serializes GameObjects and Components for runtime MCP responses.
    /// Uses only runtime APIs (no AssetDatabase, no EditorUtility).
    /// </summary>
    public static class RuntimeGameObjectSerializer
    {
        public static object GetGameObjectData(GameObject go)
        {
            if (go == null) return null;

            return new
            {
                name = go.name,
                instanceID = go.GetInstanceID(),
                tag = go.tag,
                layer = go.layer,
                layerName = LayerMask.LayerToName(go.layer),
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                isStatic = go.isStatic,
                sceneName = go.scene.name,
                transform = SerializeTransform(go.transform),
                parentInstanceID = go.transform.parent?.gameObject.GetInstanceID() ?? 0,
                childCount = go.transform.childCount,
                componentNames = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToList()
            };
        }

        public static object GetGameObjectSummary(GameObject go)
        {
            if (go == null) return null;

            return new
            {
                name = go.name,
                instanceID = go.GetInstanceID(),
                activeSelf = go.activeSelf,
                position = SerializeVector3(go.transform.position)
            };
        }

        public static object SerializeTransform(Transform t)
        {
            if (t == null) return null;

            return new
            {
                position = SerializeVector3(t.position),
                localPosition = SerializeVector3(t.localPosition),
                rotation = SerializeVector3(t.eulerAngles),
                localRotation = SerializeVector3(t.localEulerAngles),
                scale = SerializeVector3(t.localScale),
                forward = SerializeVector3(t.forward),
                up = SerializeVector3(t.up),
                right = SerializeVector3(t.right)
            };
        }

        public static object SerializeVector3(Vector3 v)
        {
            return new { x = v.x, y = v.y, z = v.z };
        }

        public static object SerializeColor(Color c)
        {
            return new { r = c.r, g = c.g, b = c.b, a = c.a };
        }

        public static object SerializeComponentBasic(Component c)
        {
            if (c == null) return null;

            return new
            {
                typeName = c.GetType().Name,
                typeFullName = c.GetType().FullName,
                instanceID = c.GetInstanceID(),
                enabled = (c is Behaviour b) ? b.enabled : true,
                gameObjectName = c.gameObject.name,
                gameObjectInstanceID = c.gameObject.GetInstanceID()
            };
        }

        public static List<object> GetHierarchy(GameObject root, int maxDepth = 10, int currentDepth = 0)
        {
            var result = new List<object>();
            if (root == null || currentDepth > maxDepth) return result;

            result.Add(new
            {
                name = root.name,
                instanceID = root.GetInstanceID(),
                depth = currentDepth,
                activeSelf = root.activeSelf,
                childCount = root.transform.childCount,
                position = SerializeVector3(root.transform.position)
            });

            for (int i = 0; i < root.transform.childCount; i++)
            {
                var child = root.transform.GetChild(i).gameObject;
                result.AddRange(GetHierarchy(child, maxDepth, currentDepth + 1));
            }

            return result;
        }
    }
}
