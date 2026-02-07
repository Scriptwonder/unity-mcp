using System.Collections.Generic;
using MCPForUnity.Runtime.Helpers;
using MCPForUnity.Runtime.Tools;
using Newtonsoft.Json.Linq;
using UnityEngine;

#if UNITY_XR_MANAGEMENT
using UnityEngine.XR;
#endif

namespace MCPForUnity.Runtime.XR
{
    /// <summary>
    /// Runtime tool for XR boundary/guardian queries.
    /// Provides play area size, boundary configuration status, and boundary visualization.
    /// </summary>
    [RuntimeMcpTool("manage_xr_boundary")]
    public static class RuntimeXRBoundary
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "get_info");

            return action switch
            {
                "get_info" => GetBoundaryInfo(p),
                "get_play_area" => GetPlayArea(p),
                "is_configured" => IsConfigured(p),
                "visualize" => VisualizeBoundary(p),
                "clear_visualization" => ClearVisualization(p),
                "check_point" => CheckPointInBoundary(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
        }

        private static object GetBoundaryInfo(RuntimeToolParams p)
        {
#if UNITY_XR_MANAGEMENT
            var inputSubsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(inputSubsystems);

            if (inputSubsystems.Count == 0)
            {
                return new RuntimeSuccessResponse("Boundary info (no XR input subsystem)", new
                {
                    hasXRSubsystem = false,
                    isConfigured = false,
                    note = "No XR input subsystem found. Ensure XR is initialized."
                });
            }

            var inputSubsystem = inputSubsystems[0];

            // Try to get boundary points
            var boundaryPoints = new List<Vector3>();
            bool hasBoundary = inputSubsystem.TryGetBoundaryPoints(boundaryPoints);

            // Get tracking origin mode
            var trackingOrigin = inputSubsystem.GetTrackingOriginMode();

            return new RuntimeSuccessResponse("Boundary info retrieved", new
            {
                hasXRSubsystem = true,
                isConfigured = hasBoundary && boundaryPoints.Count > 0,
                boundaryPointCount = boundaryPoints.Count,
                trackingOriginMode = trackingOrigin.ToString(),
                boundaryPoints = hasBoundary ? SerializeBoundaryPoints(boundaryPoints) : null
            });
#else
            return new RuntimeSuccessResponse("Boundary info", new
            {
                hasXRSubsystem = false,
                isConfigured = false,
                note = "XR Management package not installed"
            });
#endif
        }

        private static object GetPlayArea(RuntimeToolParams p)
        {
#if UNITY_XR_MANAGEMENT
            var inputSubsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(inputSubsystems);

            if (inputSubsystems.Count == 0)
                return new RuntimeErrorResponse("No XR input subsystem found");

            var inputSubsystem = inputSubsystems[0];

            var boundaryPoints = new List<Vector3>();
            if (!inputSubsystem.TryGetBoundaryPoints(boundaryPoints) || boundaryPoints.Count < 3)
            {
                return new RuntimeErrorResponse("No boundary configured or insufficient boundary points");
            }

            // Calculate play area bounds
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            foreach (var point in boundaryPoints)
            {
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minZ = Mathf.Min(minZ, point.z);
                maxZ = Mathf.Max(maxZ, point.z);
            }

            float width = maxX - minX;
            float depth = maxZ - minZ;
            var center = new Vector3((minX + maxX) / 2f, 0, (minZ + maxZ) / 2f);

            // Calculate area (approximate using bounding box)
            float area = width * depth;

            // Calculate more accurate area using polygon
            float polygonArea = CalculatePolygonArea(boundaryPoints);

            return new RuntimeSuccessResponse("Play area retrieved", new
            {
                width,
                depth,
                boundingBoxArea = area,
                polygonArea,
                center = RuntimeGameObjectSerializer.SerializeVector3(center),
                bounds = new
                {
                    minX,
                    maxX,
                    minZ,
                    maxZ
                },
                pointCount = boundaryPoints.Count
            });
#else
            return new RuntimeErrorResponse("XR Management package not installed");
#endif
        }

        private static float CalculatePolygonArea(List<Vector3> points)
        {
            if (points.Count < 3) return 0;

            // Shoelace formula for polygon area (using X and Z)
            float area = 0;
            int j = points.Count - 1;

            for (int i = 0; i < points.Count; i++)
            {
                area += (points[j].x + points[i].x) * (points[j].z - points[i].z);
                j = i;
            }

            return Mathf.Abs(area / 2f);
        }

        private static object IsConfigured(RuntimeToolParams p)
        {
#if UNITY_XR_MANAGEMENT
            var inputSubsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(inputSubsystems);

            if (inputSubsystems.Count == 0)
            {
                return new RuntimeSuccessResponse("Boundary configuration status", new
                {
                    isConfigured = false,
                    reason = "No XR input subsystem"
                });
            }

            var boundaryPoints = new List<Vector3>();
            bool hasBoundary = inputSubsystems[0].TryGetBoundaryPoints(boundaryPoints);

            return new RuntimeSuccessResponse("Boundary configuration status", new
            {
                isConfigured = hasBoundary && boundaryPoints.Count >= 3,
                pointCount = boundaryPoints.Count
            });
#else
            return new RuntimeSuccessResponse("Boundary configuration status", new
            {
                isConfigured = false,
                reason = "XR Management not installed"
            });
#endif
        }

        private static object VisualizeBoundary(RuntimeToolParams p)
        {
#if UNITY_XR_MANAGEMENT
            var inputSubsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(inputSubsystems);

            if (inputSubsystems.Count == 0)
                return new RuntimeErrorResponse("No XR input subsystem found");

            var boundaryPoints = new List<Vector3>();
            if (!inputSubsystems[0].TryGetBoundaryPoints(boundaryPoints) || boundaryPoints.Count < 3)
                return new RuntimeErrorResponse("No boundary points available");

            // Clear existing visualization
            var existing = GameObject.Find("BoundaryVisualization");
            if (existing != null) Object.Destroy(existing);

            // Create boundary visualization
            var visualRoot = new GameObject("BoundaryVisualization");

            // Create line renderer for boundary outline
            var lineObj = new GameObject("BoundaryLine");
            lineObj.transform.SetParent(visualRoot.transform, false);

            var lineRenderer = lineObj.AddComponent<LineRenderer>();
            lineRenderer.positionCount = boundaryPoints.Count + 1; // +1 to close the loop
            lineRenderer.loop = true;

            float height = p.GetFloat("height") ?? 0.1f;
            float lineWidth = p.GetFloat("line_width") ?? 0.05f;

            for (int i = 0; i < boundaryPoints.Count; i++)
            {
                lineRenderer.SetPosition(i, boundaryPoints[i] + Vector3.up * height);
            }
            lineRenderer.SetPosition(boundaryPoints.Count, boundaryPoints[0] + Vector3.up * height);

            lineRenderer.startWidth = lineWidth;
            lineRenderer.endWidth = lineWidth;

            // Create material
            var material = new Material(Shader.Find("Sprites/Default"));
            var colorToken = p.GetRaw("color");
            Color color = new Color(0, 1, 1, 0.5f); // Cyan by default
            if (colorToken != null)
            {
                var parsed = Tools.Implementations.RuntimeMaterial.ParseColor(colorToken);
                if (parsed.HasValue) color = parsed.Value;
            }
            material.color = color;
            lineRenderer.material = material;

            // Optionally create corner markers
            if (p.GetBool("show_corners", true))
            {
                for (int i = 0; i < boundaryPoints.Count; i++)
                {
                    var cornerMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    cornerMarker.name = $"Corner_{i}";
                    cornerMarker.transform.SetParent(visualRoot.transform, false);
                    cornerMarker.transform.position = boundaryPoints[i] + Vector3.up * height;
                    cornerMarker.transform.localScale = Vector3.one * 0.1f;
                    Object.Destroy(cornerMarker.GetComponent<Collider>());

                    var renderer = cornerMarker.GetComponent<Renderer>();
                    renderer.material = new Material(Shader.Find("Standard"));
                    renderer.material.color = color;
                }
            }

            return new RuntimeSuccessResponse("Boundary visualization created", new
            {
                pointCount = boundaryPoints.Count,
                visualizationObject = "BoundaryVisualization",
                color = RuntimeGameObjectSerializer.SerializeColor(color)
            });
#else
            return new RuntimeErrorResponse("XR Management not installed");
#endif
        }

        private static object ClearVisualization(RuntimeToolParams p)
        {
            var existing = GameObject.Find("BoundaryVisualization");
            if (existing != null)
            {
                Object.Destroy(existing);
                return new RuntimeSuccessResponse("Boundary visualization cleared", new { cleared = true });
            }

            return new RuntimeSuccessResponse("No boundary visualization to clear", new { cleared = false });
        }

        private static object CheckPointInBoundary(RuntimeToolParams p)
        {
#if UNITY_XR_MANAGEMENT
            var posToken = p.GetRaw("position");
            if (posToken == null)
                return new RuntimeErrorResponse("'position' parameter is required");

            var pos = Tools.Implementations.RuntimeGameObject.ParseVector3(posToken);
            if (!pos.HasValue)
                return new RuntimeErrorResponse("Invalid position format");

            var inputSubsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(inputSubsystems);

            if (inputSubsystems.Count == 0)
                return new RuntimeErrorResponse("No XR input subsystem found");

            var boundaryPoints = new List<Vector3>();
            if (!inputSubsystems[0].TryGetBoundaryPoints(boundaryPoints) || boundaryPoints.Count < 3)
                return new RuntimeErrorResponse("No boundary configured");

            // Check if point is inside polygon (using X,Z plane)
            bool isInside = IsPointInPolygon(pos.Value, boundaryPoints);

            // Calculate distance to nearest boundary edge
            float distanceToBoundary = CalculateDistanceToBoundary(pos.Value, boundaryPoints);

            return new RuntimeSuccessResponse("Point boundary check", new
            {
                position = RuntimeGameObjectSerializer.SerializeVector3(pos.Value),
                isInsideBoundary = isInside,
                distanceToBoundaryEdge = distanceToBoundary
            });
#else
            return new RuntimeErrorResponse("XR Management not installed");
#endif
        }

        private static bool IsPointInPolygon(Vector3 point, List<Vector3> polygon)
        {
            // Ray casting algorithm for point-in-polygon
            bool inside = false;
            int j = polygon.Count - 1;

            for (int i = 0; i < polygon.Count; i++)
            {
                if ((polygon[i].z < point.z && polygon[j].z >= point.z ||
                     polygon[j].z < point.z && polygon[i].z >= point.z) &&
                    (polygon[i].x + (point.z - polygon[i].z) / (polygon[j].z - polygon[i].z) * (polygon[j].x - polygon[i].x) < point.x))
                {
                    inside = !inside;
                }
                j = i;
            }

            return inside;
        }

        private static float CalculateDistanceToBoundary(Vector3 point, List<Vector3> polygon)
        {
            float minDistance = float.MaxValue;

            for (int i = 0; i < polygon.Count; i++)
            {
                int j = (i + 1) % polygon.Count;

                // Project point onto line segment
                Vector3 a = polygon[i];
                Vector3 b = polygon[j];

                // Use only X and Z
                Vector2 p = new Vector2(point.x, point.z);
                Vector2 a2 = new Vector2(a.x, a.z);
                Vector2 b2 = new Vector2(b.x, b.z);

                float dist = DistanceToLineSegment(p, a2, b2);
                minDistance = Mathf.Min(minDistance, dist);
            }

            return minDistance;
        }

        private static float DistanceToLineSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            Vector2 ap = p - a;

            float t = Vector2.Dot(ap, ab) / Vector2.Dot(ab, ab);
            t = Mathf.Clamp01(t);

            Vector2 closest = a + t * ab;
            return Vector2.Distance(p, closest);
        }

#if UNITY_XR_MANAGEMENT
        private static object[] SerializeBoundaryPoints(List<Vector3> points)
        {
            var result = new object[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                result[i] = RuntimeGameObjectSerializer.SerializeVector3(points[i]);
            }
            return result;
        }
#endif
    }
}
