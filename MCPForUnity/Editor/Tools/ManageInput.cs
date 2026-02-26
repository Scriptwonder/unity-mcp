using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Simulates player input during play mode for AI game testing.
    /// Supports key presses, mouse clicks, axis simulation, and state queries.
    /// </summary>
    [McpForUnityTool("simulate_input", AutoRegister = false)]
    public static class ManageInput
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            string action = actionResult.Value.ToLowerInvariant();

            switch (action)
            {
                case "send_key":
                    return HandleSendKey(p);
                case "send_mouse_click":
                    return HandleSendMouseClick(p);
                case "send_mouse_move":
                    return HandleSendMouseMove(p);
                case "send_sequence":
                    return HandleSendSequence(p, @params);
                case "get_state":
                    return HandleGetState(p);
                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions: send_key, send_mouse_click, send_mouse_move, send_sequence, get_state.");
            }
        }

        private static object RequirePlayMode()
        {
            if (!EditorApplication.isPlaying)
                return new ErrorResponse("Input simulation requires Play Mode. Use manage_editor action='play' first.");
            return null;
        }

        private static object HandleSendKey(ToolParams p)
        {
            var guard = RequirePlayMode();
            if (guard != null) return guard;

            string key = p.Get("key");
            if (string.IsNullOrEmpty(key))
                return new ErrorResponse("'key' parameter is required (e.g., 'W', 'Space', 'Mouse0').");

            int holdDurationMs = p.GetInt("holdDuration") ?? 100;
            bool press = p.GetBool("press", true);
            bool release = p.GetBool("release", true);

            var simulator = GetOrCreateSimulator();
            if (simulator == null)
                return new ErrorResponse("Failed to create InputSimulator in the scene.");

            simulator.QueueKeyAction(key, holdDurationMs, press, release);

            return new SuccessResponse(
                $"Queued key '{key}' (hold {holdDurationMs}ms, press={press}, release={release}).",
                new { key, holdDurationMs, press, release });
        }

        private static object HandleSendMouseClick(ToolParams p)
        {
            var guard = RequirePlayMode();
            if (guard != null) return guard;

            float x = (float)(p.GetInt("x") ?? 0);
            float y = (float)(p.GetInt("y") ?? 0);
            int button = p.GetInt("button") ?? 0; // 0=left, 1=right, 2=middle

            var simulator = GetOrCreateSimulator();
            if (simulator == null)
                return new ErrorResponse("Failed to create InputSimulator in the scene.");

            simulator.QueueMouseClick(x, y, button);

            return new SuccessResponse(
                $"Queued mouse click at ({x}, {y}) button={button}.",
                new { x, y, button });
        }

        private static object HandleSendMouseMove(ToolParams p)
        {
            var guard = RequirePlayMode();
            if (guard != null) return guard;

            float deltaX = (float)(p.GetInt("deltaX") ?? 0);
            float deltaY = (float)(p.GetInt("deltaY") ?? 0);

            var simulator = GetOrCreateSimulator();
            if (simulator == null)
                return new ErrorResponse("Failed to create InputSimulator in the scene.");

            simulator.QueueMouseMove(deltaX, deltaY);

            return new SuccessResponse(
                $"Queued mouse move delta ({deltaX}, {deltaY}).",
                new { deltaX, deltaY });
        }

        private static object HandleSendSequence(ToolParams p, JObject raw)
        {
            var guard = RequirePlayMode();
            if (guard != null) return guard;

            var stepsToken = raw["steps"];
            if (stepsToken == null || stepsToken.Type != JTokenType.Array)
                return new ErrorResponse("'steps' parameter is required and must be an array of {action, params, duration_ms} objects.");

            var simulator = GetOrCreateSimulator();
            if (simulator == null)
                return new ErrorResponse("Failed to create InputSimulator in the scene.");

            var steps = (JArray)stepsToken;
            int queued = 0;

            foreach (var step in steps)
            {
                if (step.Type != JTokenType.Object) continue;
                var stepObj = (JObject)step;
                string stepAction = stepObj["action"]?.ToString()?.ToLowerInvariant() ?? "";
                int durationMs = ParamCoercion.CoerceIntNullable(stepObj["duration_ms"] ?? stepObj["durationMs"]) ?? 100;

                switch (stepAction)
                {
                    case "key":
                        string key = stepObj["key"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(key))
                        {
                            simulator.QueueKeyAction(key, durationMs, true, true);
                            queued++;
                        }
                        break;
                    case "mouse_click":
                        float cx = (float)(ParamCoercion.CoerceIntNullable(stepObj["x"]) ?? 0);
                        float cy = (float)(ParamCoercion.CoerceIntNullable(stepObj["y"]) ?? 0);
                        int btn = ParamCoercion.CoerceIntNullable(stepObj["button"]) ?? 0;
                        simulator.QueueMouseClick(cx, cy, btn);
                        queued++;
                        break;
                    case "mouse_move":
                        float dx = (float)(ParamCoercion.CoerceIntNullable(stepObj["deltaX"] ?? stepObj["delta_x"]) ?? 0);
                        float dy = (float)(ParamCoercion.CoerceIntNullable(stepObj["deltaY"] ?? stepObj["delta_y"]) ?? 0);
                        simulator.QueueMouseMove(dx, dy);
                        queued++;
                        break;
                    case "wait":
                        simulator.QueueWait(durationMs);
                        queued++;
                        break;
                }
            }

            return new SuccessResponse(
                $"Queued {queued} input steps.",
                new { queued, totalSteps = steps.Count });
        }

        private static object HandleGetState(ToolParams p)
        {
            var guard = RequirePlayMode();
            if (guard != null) return guard;

            string query = p.Get("query") ?? "default";
            var state = new Dictionary<string, object>();

            // Always include basic state
            state["isPlaying"] = EditorApplication.isPlaying;
            state["isPaused"] = EditorApplication.isPaused;
            state["time"] = Time.time;
            state["frameCount"] = Time.frameCount;

            // Find player-like objects
            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            if (cameras.Length > 0)
            {
                var mainCam = Camera.main ?? cameras[0];
                state["cameraPosition"] = new[] { mainCam.transform.position.x, mainCam.transform.position.y, mainCam.transform.position.z };
                state["cameraRotation"] = new[] { mainCam.transform.eulerAngles.x, mainCam.transform.eulerAngles.y, mainCam.transform.eulerAngles.z };
            }

            // Look for common player patterns
            var player = GameObject.FindWithTag("Player");
            if (player != null)
            {
                state["playerPosition"] = new[] { player.transform.position.x, player.transform.position.y, player.transform.position.z };
                state["playerRotation"] = new[] { player.transform.eulerAngles.x, player.transform.eulerAngles.y, player.transform.eulerAngles.z };

                // Check for Rigidbody velocity
                var rb = player.GetComponent<Rigidbody>();
                if (rb != null)
                {
#if UNITY_2023_3_OR_NEWER
                    state["playerVelocity"] = new[] { rb.linearVelocity.x, rb.linearVelocity.y, rb.linearVelocity.z };
                    state["playerSpeed"] = rb.linearVelocity.magnitude;
#else
                    state["playerVelocity"] = new[] { rb.velocity.x, rb.velocity.y, rb.velocity.z };
                    state["playerSpeed"] = rb.velocity.magnitude;
#endif
                }

                var rb2d = player.GetComponent<Rigidbody2D>();
                if (rb2d != null)
                {
#if UNITY_2023_3_OR_NEWER
                    state["playerVelocity"] = new[] { rb2d.linearVelocity.x, rb2d.linearVelocity.y };
                    state["playerSpeed"] = rb2d.linearVelocity.magnitude;
#else
                    state["playerVelocity"] = new[] { rb2d.velocity.x, rb2d.velocity.y };
                    state["playerSpeed"] = rb2d.velocity.magnitude;
#endif
                }
            }

            // Active UI elements (for click-based games)
            if (query == "ui" || query == "default")
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                var activeUiElements = new List<object>();
                foreach (var canvas in canvases)
                {
                    if (!canvas.gameObject.activeInHierarchy) continue;
                    var buttons = canvas.GetComponentsInChildren<UnityEngine.UI.Button>(false);
                    foreach (var btn in buttons)
                    {
                        if (!btn.gameObject.activeInHierarchy || !btn.interactable) continue;
                        var rt = btn.GetComponent<RectTransform>();
                        activeUiElements.Add(new Dictionary<string, object>
                        {
                            { "name", btn.gameObject.name },
                            { "type", "Button" },
                            { "interactable", btn.interactable },
                            { "position", rt != null ? new[] { rt.position.x, rt.position.y } : null },
                        });
                    }
                }
                if (activeUiElements.Count > 0)
                    state["activeUiElements"] = activeUiElements;
            }

            return new SuccessResponse("Game state snapshot.", state);
        }

        private static MCPInputSimulator GetOrCreateSimulator()
        {
            var existing = UnityEngine.Object.FindObjectsOfType<MCPInputSimulator>();
            if (existing.Length > 0) return existing[0];
            var go = new GameObject("__MCP_InputSimulator__");
            go.hideFlags = HideFlags.HideAndDontSave;
            return go.AddComponent<MCPInputSimulator>();
        }
    }
}
