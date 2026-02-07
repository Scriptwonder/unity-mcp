using System.Linq;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Runtime.Tools.Implementations
{
    /// <summary>
    /// Runtime tool for material operations: set color, shader properties, create materials.
    /// </summary>
    [RuntimeMcpTool("manage_material")]
    public static class RuntimeMaterial
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "set_color");

            return action switch
            {
                "set_color" => SetColor(p),
                "get_color" => GetColor(p),
                "set_property" => SetProperty(p),
                "get_material_info" => GetMaterialInfo(p),
                "create_material" => CreateMaterial(p),
                "assign_material" => AssignMaterial(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
        }

        private static object SetColor(RuntimeToolParams p)
        {
            var (renderer, error) = GetTargetRenderer(p);
            if (error != null) return error;

            var colorToken = p.GetRaw("color");
            if (colorToken == null)
                return new RuntimeErrorResponse("'color' parameter is required");

            var color = ParseColor(colorToken);
            if (!color.HasValue)
                return new RuntimeErrorResponse("Invalid color format. Use [r,g,b], [r,g,b,a], {r,g,b,a}, or color name");

            string propertyName = p.Get("property", "_Color");
            int materialIndex = p.GetInt("material_index") ?? 0;

            if (materialIndex < 0 || materialIndex >= renderer.materials.Length)
                return new RuntimeErrorResponse($"Material index {materialIndex} out of range (0-{renderer.materials.Length - 1})");

            // Use material (not sharedMaterial) to create instance if needed
            var material = renderer.materials[materialIndex];
            material.SetColor(propertyName, color.Value);

            return new RuntimeSuccessResponse($"Set color on '{renderer.gameObject.name}'", new
            {
                gameObjectName = renderer.gameObject.name,
                materialIndex,
                propertyName,
                color = RuntimeGameObjectSerializer.SerializeColor(color.Value)
            });
        }

        private static object GetColor(RuntimeToolParams p)
        {
            var (renderer, error) = GetTargetRenderer(p);
            if (error != null) return error;

            string propertyName = p.Get("property", "_Color");
            int materialIndex = p.GetInt("material_index") ?? 0;

            if (materialIndex < 0 || materialIndex >= renderer.materials.Length)
                return new RuntimeErrorResponse($"Material index {materialIndex} out of range");

            var material = renderer.materials[materialIndex];

            if (!material.HasProperty(propertyName))
                return new RuntimeErrorResponse($"Material does not have property '{propertyName}'");

            var color = material.GetColor(propertyName);

            return new RuntimeSuccessResponse($"Got color from '{renderer.gameObject.name}'", new
            {
                gameObjectName = renderer.gameObject.name,
                materialIndex,
                propertyName,
                color = RuntimeGameObjectSerializer.SerializeColor(color)
            });
        }

        private static object SetProperty(RuntimeToolParams p)
        {
            var (renderer, error) = GetTargetRenderer(p);
            if (error != null) return error;

            string propertyName = p.Get("property");
            if (string.IsNullOrEmpty(propertyName))
                return new RuntimeErrorResponse("'property' parameter is required");

            int materialIndex = p.GetInt("material_index") ?? 0;

            if (materialIndex < 0 || materialIndex >= renderer.materials.Length)
                return new RuntimeErrorResponse($"Material index {materialIndex} out of range");

            var material = renderer.materials[materialIndex];

            if (!material.HasProperty(propertyName))
                return new RuntimeErrorResponse($"Material does not have property '{propertyName}'");

            var valueToken = p.GetRaw("value");
            if (valueToken == null)
                return new RuntimeErrorResponse("'value' parameter is required");

            string propertyType = p.Get("property_type", "auto");

            // Try to determine type and set appropriately
            bool success = false;

            if (propertyType == "color" || (propertyType == "auto" && IsColorToken(valueToken)))
            {
                var color = ParseColor(valueToken);
                if (color.HasValue)
                {
                    material.SetColor(propertyName, color.Value);
                    success = true;
                }
            }
            else if (propertyType == "vector" || (propertyType == "auto" && IsVectorToken(valueToken)))
            {
                var vec = RuntimeGameObject.ParseVector3(valueToken);
                if (vec.HasValue)
                {
                    material.SetVector(propertyName, vec.Value);
                    success = true;
                }
            }
            else if (propertyType == "float" || valueToken.Type == JTokenType.Float || valueToken.Type == JTokenType.Integer)
            {
                material.SetFloat(propertyName, valueToken.Value<float>());
                success = true;
            }
            else if (propertyType == "int")
            {
                material.SetInt(propertyName, valueToken.Value<int>());
                success = true;
            }

            if (!success)
                return new RuntimeErrorResponse($"Could not set property '{propertyName}' with provided value");

            return new RuntimeSuccessResponse($"Set property '{propertyName}' on '{renderer.gameObject.name}'", new
            {
                gameObjectName = renderer.gameObject.name,
                materialIndex,
                propertyName
            });
        }

        private static object GetMaterialInfo(RuntimeToolParams p)
        {
            var (renderer, error) = GetTargetRenderer(p);
            if (error != null) return error;

            var materials = renderer.materials.Select((m, i) => new
            {
                index = i,
                name = m.name,
                shaderName = m.shader?.name ?? "Unknown",
                color = m.HasProperty("_Color") ? RuntimeGameObjectSerializer.SerializeColor(m.color) : null,
                mainTexture = m.mainTexture?.name
            }).ToList();

            return new RuntimeSuccessResponse($"Material info for '{renderer.gameObject.name}'", new
            {
                gameObjectName = renderer.gameObject.name,
                materialCount = materials.Count,
                materials
            });
        }

        private static object CreateMaterial(RuntimeToolParams p)
        {
            string shaderName = p.Get("shader", "Standard");

            var shader = Shader.Find(shaderName);
            if (shader == null)
                return new RuntimeErrorResponse($"Shader '{shaderName}' not found");

            var material = new Material(shader);

            string materialName = p.Get("name", $"RuntimeMaterial_{System.Guid.NewGuid().ToString().Substring(0, 8)}");
            material.name = materialName;

            // Set initial color if provided
            var colorToken = p.GetRaw("color");
            if (colorToken != null)
            {
                var color = ParseColor(colorToken);
                if (color.HasValue && material.HasProperty("_Color"))
                {
                    material.color = color.Value;
                }
            }

            // Optionally assign to a target
            string target = p.Get("assign_to");
            if (!string.IsNullOrEmpty(target))
            {
                var go = RuntimeGameObject.FindGameObject(target);
                if (go != null)
                {
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        int materialIndex = p.GetInt("material_index") ?? 0;
                        var materials = renderer.materials;
                        if (materialIndex >= 0 && materialIndex < materials.Length)
                        {
                            materials[materialIndex] = material;
                            renderer.materials = materials;
                        }
                    }
                }
            }

            return new RuntimeSuccessResponse($"Created material '{materialName}'", new
            {
                name = materialName,
                shaderName = shader.name,
                instanceID = material.GetInstanceID()
            });
        }

        private static object AssignMaterial(RuntimeToolParams p)
        {
            var (renderer, error) = GetTargetRenderer(p);
            if (error != null) return error;

            string materialSource = p.Get("source");
            if (string.IsNullOrEmpty(materialSource))
                return new RuntimeErrorResponse("'source' parameter is required (material name or 'copy_from:GameObjectName')");

            int targetIndex = p.GetInt("material_index") ?? 0;
            if (targetIndex < 0 || targetIndex >= renderer.materials.Length)
                return new RuntimeErrorResponse($"Material index {targetIndex} out of range");

            Material newMaterial = null;

            // Copy from another object
            if (materialSource.StartsWith("copy_from:"))
            {
                string sourceTarget = materialSource.Substring("copy_from:".Length);
                var sourceGo = RuntimeGameObject.FindGameObject(sourceTarget);
                if (sourceGo == null)
                    return new RuntimeErrorResponse($"Source GameObject '{sourceTarget}' not found");

                var sourceRenderer = sourceGo.GetComponent<Renderer>();
                if (sourceRenderer == null)
                    return new RuntimeErrorResponse($"'{sourceTarget}' has no Renderer");

                int sourceIndex = p.GetInt("source_material_index") ?? 0;
                if (sourceIndex < 0 || sourceIndex >= sourceRenderer.materials.Length)
                    return new RuntimeErrorResponse($"Source material index {sourceIndex} out of range");

                newMaterial = new Material(sourceRenderer.materials[sourceIndex]);
            }
            else
            {
                // Try to load from Resources
                newMaterial = Resources.Load<Material>(materialSource);
                if (newMaterial == null)
                {
                    // Try to find among existing materials in the scene
                    var allRenderers = Object.FindObjectsOfType<Renderer>(true);
                    foreach (var r in allRenderers)
                    {
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m != null && m.name == materialSource)
                            {
                                newMaterial = m;
                                break;
                            }
                        }
                        if (newMaterial != null) break;
                    }
                }
            }

            if (newMaterial == null)
                return new RuntimeErrorResponse($"Could not find or load material '{materialSource}'");

            var materials = renderer.materials;
            materials[targetIndex] = newMaterial;
            renderer.materials = materials;

            return new RuntimeSuccessResponse($"Assigned material to '{renderer.gameObject.name}'", new
            {
                gameObjectName = renderer.gameObject.name,
                materialIndex = targetIndex,
                materialName = newMaterial.name
            });
        }

        private static (Renderer renderer, RuntimeErrorResponse error) GetTargetRenderer(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return (null, new RuntimeErrorResponse("'target' parameter is required"));

            var go = RuntimeGameObject.FindGameObject(target);
            if (go == null)
                return (null, new RuntimeErrorResponse($"GameObject '{target}' not found"));

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return (null, new RuntimeErrorResponse($"'{go.name}' has no Renderer component"));

            return (renderer, null);
        }

        internal static Color? ParseColor(JToken token)
        {
            if (token == null) return null;

            // Array format: [r, g, b] or [r, g, b, a]
            if (token is JArray arr)
            {
                if (arr.Count >= 3)
                {
                    float r = arr[0].Value<float>();
                    float g = arr[1].Value<float>();
                    float b = arr[2].Value<float>();
                    float a = arr.Count >= 4 ? arr[3].Value<float>() : 1f;

                    // Normalize if values are 0-255 range
                    if (r > 1 || g > 1 || b > 1)
                    {
                        r /= 255f;
                        g /= 255f;
                        b /= 255f;
                        if (a > 1) a /= 255f;
                    }

                    return new Color(r, g, b, a);
                }
            }

            // Object format: {r, g, b, a}
            if (token is JObject obj)
            {
                float r = obj["r"]?.Value<float>() ?? 0;
                float g = obj["g"]?.Value<float>() ?? 0;
                float b = obj["b"]?.Value<float>() ?? 0;
                float a = obj["a"]?.Value<float>() ?? 1;

                if (r > 1 || g > 1 || b > 1)
                {
                    r /= 255f;
                    g /= 255f;
                    b /= 255f;
                    if (a > 1) a /= 255f;
                }

                return new Color(r, g, b, a);
            }

            // String format: color name or hex
            if (token.Type == JTokenType.String)
            {
                string colorStr = token.Value<string>().ToLower().Trim();

                // Common color names
                return colorStr switch
                {
                    "red" => Color.red,
                    "green" => Color.green,
                    "blue" => Color.blue,
                    "white" => Color.white,
                    "black" => Color.black,
                    "yellow" => Color.yellow,
                    "cyan" => Color.cyan,
                    "magenta" => Color.magenta,
                    "gray" or "grey" => Color.gray,
                    "clear" => Color.clear,
                    "orange" => new Color(1f, 0.5f, 0f),
                    "purple" => new Color(0.5f, 0f, 0.5f),
                    "pink" => new Color(1f, 0.75f, 0.8f),
                    "brown" => new Color(0.6f, 0.3f, 0f),
                    _ => TryParseHex(colorStr)
                };
            }

            return null;
        }

        private static Color? TryParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;

            if (hex.StartsWith("#")) hex = hex.Substring(1);

            if (hex.Length == 6)
            {
                if (ColorUtility.TryParseHtmlString("#" + hex, out var color))
                    return color;
            }
            else if (hex.Length == 8)
            {
                if (ColorUtility.TryParseHtmlString("#" + hex, out var color))
                    return color;
            }

            return null;
        }

        private static bool IsColorToken(JToken token)
        {
            if (token is JObject obj)
                return obj["r"] != null || obj["g"] != null || obj["b"] != null;
            if (token is JArray arr)
                return arr.Count >= 3 && arr.Count <= 4;
            if (token.Type == JTokenType.String)
                return true; // Could be color name or hex
            return false;
        }

        private static bool IsVectorToken(JToken token)
        {
            if (token is JObject obj)
                return obj["x"] != null || obj["y"] != null || obj["z"] != null;
            if (token is JArray arr)
                return arr.Count == 3;
            return false;
        }
    }
}
