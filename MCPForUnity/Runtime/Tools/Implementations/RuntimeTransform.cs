using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Runtime.Tools.Implementations
{
    /// <summary>
    /// Runtime tool for transform operations: position, rotation, scale, parent, move_relative.
    /// </summary>
    [RuntimeMcpTool("manage_transform")]
    public static class RuntimeTransform
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "get");

            return action switch
            {
                "get" => GetTransform(p),
                "set_position" => SetPosition(p),
                "set_rotation" => SetRotation(p),
                "set_scale" => SetScale(p),
                "set_parent" => SetParent(p),
                "move_relative" => MoveRelative(p),
                "rotate" => Rotate(p),
                "look_at" => LookAt(p),
                "reset" => ResetTransform(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
        }

        private static object GetTransform(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var go = RuntimeGameObject.FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            return new RuntimeSuccessResponse($"Transform of '{go.name}'",
                RuntimeGameObjectSerializer.SerializeTransform(go.transform));
        }

        private static object SetPosition(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            bool worldSpace = p.GetBool("world_space", true);
            var posToken = p.GetRaw("position") ?? p.GetRaw("value");

            if (posToken == null)
                return new RuntimeErrorResponse("'position' or 'value' parameter is required");

            var pos = RuntimeGameObject.ParseVector3(posToken);
            if (!pos.HasValue)
                return new RuntimeErrorResponse("Invalid position format. Use [x,y,z] or {x,y,z}");

            if (worldSpace)
                go.transform.position = pos.Value;
            else
                go.transform.localPosition = pos.Value;

            return new RuntimeSuccessResponse($"Set position of '{go.name}'", new
            {
                name = go.name,
                worldSpace,
                position = RuntimeGameObjectSerializer.SerializeVector3(worldSpace ? go.transform.position : go.transform.localPosition)
            });
        }

        private static object SetRotation(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            bool worldSpace = p.GetBool("world_space", true);
            var rotToken = p.GetRaw("rotation") ?? p.GetRaw("value");

            if (rotToken == null)
                return new RuntimeErrorResponse("'rotation' or 'value' parameter is required");

            var rot = RuntimeGameObject.ParseVector3(rotToken);
            if (!rot.HasValue)
                return new RuntimeErrorResponse("Invalid rotation format. Use [x,y,z] (Euler angles) or {x,y,z}");

            if (worldSpace)
                go.transform.eulerAngles = rot.Value;
            else
                go.transform.localEulerAngles = rot.Value;

            return new RuntimeSuccessResponse($"Set rotation of '{go.name}'", new
            {
                name = go.name,
                worldSpace,
                rotation = RuntimeGameObjectSerializer.SerializeVector3(worldSpace ? go.transform.eulerAngles : go.transform.localEulerAngles)
            });
        }

        private static object SetScale(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            var scaleToken = p.GetRaw("scale") ?? p.GetRaw("value");

            if (scaleToken == null)
                return new RuntimeErrorResponse("'scale' or 'value' parameter is required");

            // Allow uniform scale with single number
            if (scaleToken.Type == JTokenType.Float || scaleToken.Type == JTokenType.Integer)
            {
                float uniform = scaleToken.Value<float>();
                go.transform.localScale = new Vector3(uniform, uniform, uniform);
            }
            else
            {
                var scale = RuntimeGameObject.ParseVector3(scaleToken);
                if (!scale.HasValue)
                    return new RuntimeErrorResponse("Invalid scale format. Use [x,y,z], {x,y,z}, or a single number for uniform scale");

                go.transform.localScale = scale.Value;
            }

            return new RuntimeSuccessResponse($"Set scale of '{go.name}'", new
            {
                name = go.name,
                scale = RuntimeGameObjectSerializer.SerializeVector3(go.transform.localScale)
            });
        }

        private static object SetParent(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            string parentTarget = p.Get("parent");
            bool worldPositionStays = p.GetBool("world_position_stays", true);

            if (string.IsNullOrEmpty(parentTarget))
            {
                // Unparent
                go.transform.SetParent(null, worldPositionStays);
                return new RuntimeSuccessResponse($"Unparented '{go.name}'",
                    RuntimeGameObjectSerializer.GetGameObjectData(go));
            }

            var parentGo = RuntimeGameObject.FindGameObject(parentTarget);
            if (parentGo == null)
                return new RuntimeErrorResponse($"Parent '{parentTarget}' not found");

            go.transform.SetParent(parentGo.transform, worldPositionStays);

            return new RuntimeSuccessResponse($"Set parent of '{go.name}' to '{parentGo.name}'",
                RuntimeGameObjectSerializer.GetGameObjectData(go));
        }

        private static object MoveRelative(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            // Support direction + distance or direct offset
            string direction = p.Get("direction");
            float? distance = p.GetFloat("distance");
            var offsetToken = p.GetRaw("offset");

            Vector3 offset;

            if (!string.IsNullOrEmpty(direction) && distance.HasValue)
            {
                offset = direction.ToLower() switch
                {
                    "forward" or "front" => go.transform.forward * distance.Value,
                    "back" or "backward" or "behind" => -go.transform.forward * distance.Value,
                    "right" => go.transform.right * distance.Value,
                    "left" => -go.transform.right * distance.Value,
                    "up" => go.transform.up * distance.Value,
                    "down" => -go.transform.up * distance.Value,
                    _ => Vector3.zero
                };

                if (offset == Vector3.zero && direction != "none")
                {
                    return new RuntimeErrorResponse($"Unknown direction: '{direction}'. Use: forward, back, right, left, up, down");
                }
            }
            else if (offsetToken != null)
            {
                var parsedOffset = RuntimeGameObject.ParseVector3(offsetToken);
                if (!parsedOffset.HasValue)
                    return new RuntimeErrorResponse("Invalid offset format. Use [x,y,z] or {x,y,z}");
                offset = parsedOffset.Value;
            }
            else
            {
                return new RuntimeErrorResponse("Provide either 'direction' + 'distance' or 'offset'");
            }

            bool worldSpace = p.GetBool("world_space", true);

            if (worldSpace)
                go.transform.position += offset;
            else
                go.transform.localPosition += offset;

            return new RuntimeSuccessResponse($"Moved '{go.name}'", new
            {
                name = go.name,
                offset = RuntimeGameObjectSerializer.SerializeVector3(offset),
                newPosition = RuntimeGameObjectSerializer.SerializeVector3(go.transform.position)
            });
        }

        private static object Rotate(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            var anglesToken = p.GetRaw("angles") ?? p.GetRaw("value");
            if (anglesToken == null)
                return new RuntimeErrorResponse("'angles' or 'value' parameter is required");

            var angles = RuntimeGameObject.ParseVector3(anglesToken);
            if (!angles.HasValue)
                return new RuntimeErrorResponse("Invalid angles format. Use [x,y,z] (Euler angles)");

            bool worldSpace = p.GetBool("world_space", false);

            if (worldSpace)
                go.transform.Rotate(angles.Value, Space.World);
            else
                go.transform.Rotate(angles.Value, Space.Self);

            return new RuntimeSuccessResponse($"Rotated '{go.name}'", new
            {
                name = go.name,
                rotatedBy = RuntimeGameObjectSerializer.SerializeVector3(angles.Value),
                newRotation = RuntimeGameObjectSerializer.SerializeVector3(go.transform.eulerAngles)
            });
        }

        private static object LookAt(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            // Look at position
            var posToken = p.GetRaw("position");
            if (posToken != null)
            {
                var pos = RuntimeGameObject.ParseVector3(posToken);
                if (!pos.HasValue)
                    return new RuntimeErrorResponse("Invalid position format");

                go.transform.LookAt(pos.Value);
                return new RuntimeSuccessResponse($"'{go.name}' now looking at position",
                    RuntimeGameObjectSerializer.SerializeTransform(go.transform));
            }

            // Look at target
            string lookTarget = p.Get("look_target");
            if (!string.IsNullOrEmpty(lookTarget))
            {
                var targetGo = RuntimeGameObject.FindGameObject(lookTarget);
                if (targetGo == null)
                    return new RuntimeErrorResponse($"Look target '{lookTarget}' not found");

                go.transform.LookAt(targetGo.transform);
                return new RuntimeSuccessResponse($"'{go.name}' now looking at '{targetGo.name}'",
                    RuntimeGameObjectSerializer.SerializeTransform(go.transform));
            }

            return new RuntimeErrorResponse("Provide either 'position' or 'look_target'");
        }

        private static object ResetTransform(RuntimeToolParams p)
        {
            var (go, error) = GetTargetGameObject(p);
            if (error != null) return error;

            bool resetPosition = p.GetBool("reset_position", true);
            bool resetRotation = p.GetBool("reset_rotation", true);
            bool resetScale = p.GetBool("reset_scale", true);

            if (resetPosition) go.transform.localPosition = Vector3.zero;
            if (resetRotation) go.transform.localRotation = Quaternion.identity;
            if (resetScale) go.transform.localScale = Vector3.one;

            return new RuntimeSuccessResponse($"Reset transform of '{go.name}'",
                RuntimeGameObjectSerializer.SerializeTransform(go.transform));
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
    }
}
