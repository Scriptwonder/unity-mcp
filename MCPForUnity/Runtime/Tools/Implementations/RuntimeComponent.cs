using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Runtime.Tools.Implementations
{
    /// <summary>
    /// Runtime tool for component operations: add, remove, get, set properties.
    /// </summary>
    [RuntimeMcpTool("runtime_component")]
    public static class RuntimeComponent
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "get");

            return action switch
            {
                "add" => AddComponent(p),
                "remove" => RemoveComponent(p),
                "get" => GetComponent(p),
                "get_all" => GetAllComponents(p),
                "set_property" => SetProperty(p),
                "get_property" => GetProperty(p),
                "enable" => SetEnabled(p, true),
                "disable" => SetEnabled(p, false),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
        }

        private static object AddComponent(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            string componentType = p.Get("component_type");
            if (string.IsNullOrEmpty(componentType))
                return new RuntimeErrorResponse("'component_type' parameter is required");

            var type = FindComponentType(componentType);
            if (type == null)
                return new RuntimeErrorResponse($"Component type '{componentType}' not found");

            var component = go.AddComponent(type);
            if (component == null)
                return new RuntimeErrorResponse($"Failed to add component '{componentType}'");

            // Set initial properties if provided
            var propertiesToken = p.GetRaw("properties");
            if (propertiesToken is JObject propsObj)
            {
                foreach (var prop in propsObj.Properties())
                {
                    TrySetProperty(component, prop.Name, prop.Value);
                }
            }

            return new RuntimeSuccessResponse($"Added {type.Name} to '{go.name}'",
                RuntimeGameObjectSerializer.SerializeComponentBasic(component));
        }

        private static object RemoveComponent(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            string componentType = p.Get("component_type");
            if (string.IsNullOrEmpty(componentType))
                return new RuntimeErrorResponse("'component_type' parameter is required");

            var type = FindComponentType(componentType);
            if (type == null)
                return new RuntimeErrorResponse($"Component type '{componentType}' not found");

            var component = go.GetComponent(type);
            if (component == null)
                return new RuntimeErrorResponse($"'{go.name}' does not have component '{componentType}'");

            // Prevent removing Transform
            if (component is Transform)
                return new RuntimeErrorResponse("Cannot remove Transform component");

            string typeName = component.GetType().Name;
            UnityEngine.Object.Destroy(component);

            return new RuntimeSuccessResponse($"Removed {typeName} from '{go.name}'", new
            {
                gameObjectName = go.name,
                removedComponent = typeName
            });
        }

        private static object GetComponent(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            string componentType = p.Get("component_type");
            if (string.IsNullOrEmpty(componentType))
                return new RuntimeErrorResponse("'component_type' parameter is required");

            var type = FindComponentType(componentType);
            if (type == null)
                return new RuntimeErrorResponse($"Component type '{componentType}' not found");

            var component = go.GetComponent(type);
            if (component == null)
                return new RuntimeErrorResponse($"'{go.name}' does not have component '{componentType}'");

            bool includeProperties = p.GetBool("include_properties", true);

            if (includeProperties)
            {
                var properties = GetSerializableProperties(component);
                return new RuntimeSuccessResponse($"Got {type.Name} from '{go.name}'", new
                {
                    component = RuntimeGameObjectSerializer.SerializeComponentBasic(component),
                    properties
                });
            }

            return new RuntimeSuccessResponse($"Got {type.Name} from '{go.name}'",
                RuntimeGameObjectSerializer.SerializeComponentBasic(component));
        }

        private static object GetAllComponents(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            bool includeProperties = p.GetBool("include_properties", false);

            var components = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c =>
                {
                    var data = RuntimeGameObjectSerializer.SerializeComponentBasic(c);
                    if (includeProperties)
                    {
                        return new
                        {
                            component = data,
                            properties = GetSerializableProperties(c)
                        } as object;
                    }
                    return data;
                })
                .ToList();

            return new RuntimeSuccessResponse($"Got {components.Count} components from '{go.name}'", new
            {
                gameObjectName = go.name,
                count = components.Count,
                components
            });
        }

        private static object SetProperty(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            string componentType = p.Get("component_type");
            if (string.IsNullOrEmpty(componentType))
                return new RuntimeErrorResponse("'component_type' parameter is required");

            string propertyName = p.Get("property");
            if (string.IsNullOrEmpty(propertyName))
                return new RuntimeErrorResponse("'property' parameter is required");

            var type = FindComponentType(componentType);
            if (type == null)
                return new RuntimeErrorResponse($"Component type '{componentType}' not found");

            var component = go.GetComponent(type);
            if (component == null)
                return new RuntimeErrorResponse($"'{go.name}' does not have component '{componentType}'");

            var valueToken = p.GetRaw("value");
            if (valueToken == null)
                return new RuntimeErrorResponse("'value' parameter is required");

            if (!TrySetProperty(component, propertyName, valueToken))
                return new RuntimeErrorResponse($"Failed to set property '{propertyName}' on {type.Name}");

            return new RuntimeSuccessResponse($"Set '{propertyName}' on {type.Name}", new
            {
                gameObjectName = go.name,
                componentType = type.Name,
                property = propertyName
            });
        }

        private static object GetProperty(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            string componentType = p.Get("component_type");
            if (string.IsNullOrEmpty(componentType))
                return new RuntimeErrorResponse("'component_type' parameter is required");

            string propertyName = p.Get("property");
            if (string.IsNullOrEmpty(propertyName))
                return new RuntimeErrorResponse("'property' parameter is required");

            var type = FindComponentType(componentType);
            if (type == null)
                return new RuntimeErrorResponse($"Component type '{componentType}' not found");

            var component = go.GetComponent(type);
            if (component == null)
                return new RuntimeErrorResponse($"'{go.name}' does not have component '{componentType}'");

            // Try property
            var propInfo = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (propInfo != null && propInfo.CanRead)
            {
                var value = propInfo.GetValue(component);
                return new RuntimeSuccessResponse($"Got '{propertyName}' from {type.Name}", new
                {
                    property = propertyName,
                    value = SerializePropertyValue(value)
                });
            }

            // Try field
            var fieldInfo = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fieldInfo != null)
            {
                var value = fieldInfo.GetValue(component);
                return new RuntimeSuccessResponse($"Got '{propertyName}' from {type.Name}", new
                {
                    property = propertyName,
                    value = SerializePropertyValue(value)
                });
            }

            return new RuntimeErrorResponse($"Property or field '{propertyName}' not found on {type.Name}");
        }

        private static object SetEnabled(RuntimeToolParams p, bool enabled)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            string componentType = p.Get("component_type");

            if (string.IsNullOrEmpty(componentType))
            {
                // Enable/disable the whole GameObject
                go.SetActive(enabled);
                return new RuntimeSuccessResponse($"{(enabled ? "Enabled" : "Disabled")} '{go.name}'",
                    RuntimeGameObjectSerializer.GetGameObjectSummary(go));
            }

            var type = FindComponentType(componentType);
            if (type == null)
                return new RuntimeErrorResponse($"Component type '{componentType}' not found");

            var component = go.GetComponent(type);
            if (component == null)
                return new RuntimeErrorResponse($"'{go.name}' does not have component '{componentType}'");

            if (component is Behaviour behaviour)
            {
                behaviour.enabled = enabled;
                return new RuntimeSuccessResponse($"{(enabled ? "Enabled" : "Disabled")} {type.Name} on '{go.name}'", new
                {
                    gameObjectName = go.name,
                    componentType = type.Name,
                    enabled
                });
            }

            if (component is Renderer renderer)
            {
                renderer.enabled = enabled;
                return new RuntimeSuccessResponse($"{(enabled ? "Enabled" : "Disabled")} {type.Name} on '{go.name}'", new
                {
                    gameObjectName = go.name,
                    componentType = type.Name,
                    enabled
                });
            }

            if (component is Collider collider)
            {
                collider.enabled = enabled;
                return new RuntimeSuccessResponse($"{(enabled ? "Enabled" : "Disabled")} {type.Name} on '{go.name}'", new
                {
                    gameObjectName = go.name,
                    componentType = type.Name,
                    enabled
                });
            }

            return new RuntimeErrorResponse($"{type.Name} cannot be enabled/disabled");
        }

        private static (GameObject go, RuntimeErrorResponse error) GetTargetGameObject(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return (null, new RuntimeErrorResponse("'target' parameter is required"));

            var go = RuntimeGameObject.FindGameObject(target);
            if (go == null)
                return (null, new RuntimeErrorResponse($"GameObject '{target}' not found"));

            return (go, null);
        }

        private static Type FindComponentType(string typeName)
        {
            // Common shortcuts
            var shortcut = typeName.ToLower() switch
            {
                "rigidbody" => typeof(Rigidbody),
                "rigidbody2d" => typeof(Rigidbody2D),
                "boxcollider" => typeof(BoxCollider),
                "spherecollider" => typeof(SphereCollider),
                "capsulecollider" => typeof(CapsuleCollider),
                "meshcollider" => typeof(MeshCollider),
                "boxcollider2d" => typeof(BoxCollider2D),
                "circlecollider2d" => typeof(CircleCollider2D),
                "light" => typeof(Light),
                "camera" => typeof(Camera),
                "audiosource" => typeof(AudioSource),
                "audiolistener" => typeof(AudioListener),
                "meshrenderer" => typeof(MeshRenderer),
                "meshfilter" => typeof(MeshFilter),
                "skinnedmeshrenderer" => typeof(SkinnedMeshRenderer),
                "animator" => typeof(Animator),
                "canvas" => typeof(Canvas),
                "canvasrenderer" => typeof(CanvasRenderer),
                "recttransform" => typeof(RectTransform),
                "linerenderer" => typeof(LineRenderer),
                "trailrenderer" => typeof(TrailRenderer),
                "particlesystem" => typeof(ParticleSystem),
                _ => null
            };

            if (shortcut != null) return shortcut;

            // Try direct type lookup
            var type = Type.GetType(typeName);
            if (type != null && typeof(Component).IsAssignableFrom(type)) return type;

            // Try with UnityEngine namespace
            type = Type.GetType($"UnityEngine.{typeName}, UnityEngine");
            if (type != null && typeof(Component).IsAssignableFrom(type)) return type;

            // Search all assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null && typeof(Component).IsAssignableFrom(type)) return type;

                type = assembly.GetType($"UnityEngine.{typeName}");
                if (type != null && typeof(Component).IsAssignableFrom(type)) return type;
            }

            return null;
        }

        private static bool TrySetProperty(Component component, string propertyName, JToken value)
        {
            var type = component.GetType();

            // Try property first
            var propInfo = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (propInfo != null && propInfo.CanWrite)
            {
                try
                {
                    var convertedValue = ConvertValue(value, propInfo.PropertyType);
                    propInfo.SetValue(component, convertedValue);
                    return true;
                }
                catch { }
            }

            // Try field
            var fieldInfo = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fieldInfo != null)
            {
                try
                {
                    var convertedValue = ConvertValue(value, fieldInfo.FieldType);
                    fieldInfo.SetValue(component, convertedValue);
                    return true;
                }
                catch { }
            }

            return false;
        }

        private static object ConvertValue(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null) return null;

            if (targetType == typeof(Vector3))
            {
                var v = RuntimeGameObject.ParseVector3(token);
                return v ?? Vector3.zero;
            }

            if (targetType == typeof(Vector2))
            {
                if (token is JArray arr && arr.Count >= 2)
                    return new Vector2(arr[0].Value<float>(), arr[1].Value<float>());
                if (token is JObject obj)
                    return new Vector2(obj["x"]?.Value<float>() ?? 0, obj["y"]?.Value<float>() ?? 0);
            }

            if (targetType == typeof(Color))
            {
                return RuntimeMaterial.ParseColor(token) ?? Color.white;
            }

            if (targetType == typeof(bool))
            {
                if (token.Type == JTokenType.Boolean) return token.Value<bool>();
                if (token.Type == JTokenType.String)
                {
                    var s = token.Value<string>().ToLower();
                    return s == "true" || s == "1" || s == "yes";
                }
                if (token.Type == JTokenType.Integer) return token.Value<int>() != 0;
            }

            if (targetType == typeof(float))
                return token.Value<float>();

            if (targetType == typeof(int))
                return token.Value<int>();

            if (targetType == typeof(string))
                return token.Value<string>();

            if (targetType.IsEnum)
            {
                var str = token.ToString();
                if (Enum.TryParse(targetType, str, true, out var result))
                    return result;
            }

            // Fallback
            return token.ToObject(targetType);
        }

        private static Dictionary<string, object> GetSerializableProperties(Component component)
        {
            var result = new Dictionary<string, object>();
            var type = component.GetType();

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

            foreach (var prop in props)
            {
                // Skip problematic properties
                if (prop.Name == "mesh" || prop.Name == "material" || prop.Name == "materials" ||
                    prop.Name == "transform" || prop.Name == "gameObject" ||
                    prop.Name.Contains("Matrix"))
                    continue;

                try
                {
                    var value = prop.GetValue(component);
                    result[prop.Name] = SerializePropertyValue(value);
                }
                catch { }
            }

            return result;
        }

        private static object SerializePropertyValue(object value)
        {
            if (value == null) return null;

            if (value is Vector3 v3) return RuntimeGameObjectSerializer.SerializeVector3(v3);
            if (value is Vector2 v2) return new { x = v2.x, y = v2.y };
            if (value is Color c) return RuntimeGameObjectSerializer.SerializeColor(c);
            if (value is Quaternion q) return new { x = q.x, y = q.y, z = q.z, w = q.w };
            if (value is bool || value is int || value is float || value is double || value is string)
                return value;
            if (value is Enum e) return e.ToString();
            if (value is UnityEngine.Object obj)
                return new { name = obj.name, instanceID = obj.GetInstanceID() };

            return value.ToString();
        }
    }
}
