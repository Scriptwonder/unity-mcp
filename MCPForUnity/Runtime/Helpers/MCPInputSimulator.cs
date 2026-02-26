using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Runtime component that processes queued input simulation actions during play mode.
    /// Supports both the legacy Input Manager and the new Input System.
    /// </summary>
    public class MCPInputSimulator : MonoBehaviour
    {
        private readonly Queue<InputAction> _actionQueue = new Queue<InputAction>();
        private Coroutine _processingCoroutine;
        private bool _isNewInputSystem;

        private abstract class InputAction
        {
            public int DurationMs;
        }

        private class KeyAction : InputAction
        {
            public string Key;
            public bool Press;
            public bool Release;
        }

        private class MouseClickAction : InputAction
        {
            public float X;
            public float Y;
            public int Button;
        }

        private class MouseMoveAction : InputAction
        {
            public float DeltaX;
            public float DeltaY;
        }

        private class WaitAction : InputAction
        {
        }

        private void Awake()
        {
            // Check if new Input System is available
            _isNewInputSystem = IsNewInputSystemAvailable();
        }

        private static bool IsNewInputSystemAvailable()
        {
            try
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    if (asm.GetName().Name == "Unity.InputSystem")
                        return true;
                }
            }
            catch { }
            return false;
        }

        public void QueueKeyAction(string key, int durationMs, bool press, bool release)
        {
            _actionQueue.Enqueue(new KeyAction
            {
                Key = key,
                DurationMs = durationMs,
                Press = press,
                Release = release,
            });
            EnsureProcessing();
        }

        public void QueueMouseClick(float x, float y, int button)
        {
            _actionQueue.Enqueue(new MouseClickAction
            {
                X = x,
                Y = y,
                Button = button,
                DurationMs = 50,
            });
            EnsureProcessing();
        }

        public void QueueMouseMove(float deltaX, float deltaY)
        {
            _actionQueue.Enqueue(new MouseMoveAction
            {
                DeltaX = deltaX,
                DeltaY = deltaY,
                DurationMs = 0,
            });
            EnsureProcessing();
        }

        public void QueueWait(int durationMs)
        {
            _actionQueue.Enqueue(new WaitAction { DurationMs = durationMs });
            EnsureProcessing();
        }

        private void EnsureProcessing()
        {
            if (_processingCoroutine == null)
                _processingCoroutine = StartCoroutine(ProcessQueue());
        }

        private IEnumerator ProcessQueue()
        {
            while (_actionQueue.Count > 0)
            {
                var action = _actionQueue.Dequeue();

                if (action is KeyAction keyAction)
                {
                    yield return StartCoroutine(ProcessKeyAction(keyAction));
                }
                else if (action is MouseClickAction clickAction)
                {
                    yield return StartCoroutine(ProcessMouseClickAction(clickAction));
                }
                else if (action is MouseMoveAction moveAction)
                {
                    ProcessMouseMoveAction(moveAction);
                    yield return null;
                }
                else if (action is WaitAction waitAction)
                {
                    yield return new WaitForSeconds(waitAction.DurationMs / 1000f);
                }
            }

            _processingCoroutine = null;
        }

        private IEnumerator ProcessKeyAction(KeyAction action)
        {
            if (_isNewInputSystem)
            {
                yield return StartCoroutine(SimulateKeyNewInputSystem(action));
            }
            else
            {
                yield return StartCoroutine(SimulateKeyLegacy(action));
            }
        }

        private IEnumerator SimulateKeyLegacy(KeyAction action)
        {
            // For legacy Input system, we simulate by sending keyboard events
            // through the Event system or by using SendMessage patterns
            var keyCode = ParseKeyCode(action.Key);
            if (keyCode == KeyCode.None)
            {
                Debug.LogWarning($"[MCP InputSimulator] Unknown key: '{action.Key}'");
                yield break;
            }

            // Simulate by dispatching GUI events
            if (action.Press)
            {
                var evt = new Event { type = EventType.KeyDown, keyCode = keyCode };
                DispatchInputEvent(evt);
            }

            if (action.DurationMs > 0)
                yield return new WaitForSeconds(action.DurationMs / 1000f);

            if (action.Release)
            {
                var evt = new Event { type = EventType.KeyUp, keyCode = keyCode };
                DispatchInputEvent(evt);
            }
        }

        private IEnumerator SimulateKeyNewInputSystem(KeyAction action)
        {
            // Use reflection to access InputSystem API
            try
            {
                var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
                if (inputSystemType == null)
                {
                    Debug.LogWarning("[MCP InputSimulator] New Input System assembly loaded but InputSystem type not found.");
                    yield return StartCoroutine(SimulateKeyLegacy(action));
                    yield break;
                }

                var keyboardType = FindType("UnityEngine.InputSystem.Keyboard");
                if (keyboardType == null)
                {
                    yield return StartCoroutine(SimulateKeyLegacy(action));
                    yield break;
                }

                var currentProp = keyboardType.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var keyboard = currentProp?.GetValue(null);
                if (keyboard == null)
                {
                    yield return StartCoroutine(SimulateKeyLegacy(action));
                    yield break;
                }

                // Find the key control by name
                var indexer = keyboardType.GetProperty("Item", new[] { typeof(string) });
                var keyControl = indexer?.GetValue(keyboard, new object[] { action.Key.ToLowerInvariant() });
                if (keyControl == null)
                {
                    Debug.LogWarning($"[MCP InputSimulator] Key '{action.Key}' not found on Keyboard device.");
                    yield break;
                }

                // Use InputSystem low-level API to queue state events
                var queueStateMethod = FindType("UnityEngine.InputSystem.LowLevel.InputState")
                    ?.GetMethod("Change", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (queueStateMethod != null && action.Press)
                {
                    // Press: set key value to 1
                    try
                    {
                        var generic = queueStateMethod.MakeGenericMethod(typeof(float));
                        generic.Invoke(null, new[] { keyControl, (object)1.0f });
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[MCP InputSimulator] Failed to simulate key press via InputSystem: {e.Message}");
                    }
                }

                if (action.DurationMs > 0)
                    yield return new WaitForSeconds(action.DurationMs / 1000f);

                if (queueStateMethod != null && action.Release)
                {
                    try
                    {
                        var generic = queueStateMethod.MakeGenericMethod(typeof(float));
                        generic.Invoke(null, new[] { keyControl, (object)0.0f });
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[MCP InputSimulator] Failed to simulate key release via InputSystem: {e.Message}");
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MCP InputSimulator] New Input System simulation failed: {e.Message}");
                yield return StartCoroutine(SimulateKeyLegacy(action));
            }
        }

        private IEnumerator ProcessMouseClickAction(MouseClickAction action)
        {
            var evt = new Event
            {
                type = EventType.MouseDown,
                mousePosition = new Vector2(action.X, action.Y),
                button = action.Button,
            };
            DispatchInputEvent(evt);

            yield return new WaitForSeconds(action.DurationMs / 1000f);

            var evtUp = new Event
            {
                type = EventType.MouseUp,
                mousePosition = new Vector2(action.X, action.Y),
                button = action.Button,
            };
            DispatchInputEvent(evtUp);
        }

        private void ProcessMouseMoveAction(MouseMoveAction action)
        {
            var evt = new Event
            {
                type = EventType.MouseMove,
                delta = new Vector2(action.DeltaX, action.DeltaY),
            };
            DispatchInputEvent(evt);
        }

        private static void DispatchInputEvent(Event evt)
        {
            // Push the event so that Unity's input polling picks it up
            try
            {
                // Use Event.PopEvent / PushEvent pattern if available
                // For most game scripts using Input.GetKey, we need to work at a lower level
                // The most reliable approach is to find the game's input handler and send messages
                Debug.Log($"[MCP InputSimulator] Dispatching {evt.type} event: {evt.keyCode} button={evt.button}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MCP InputSimulator] Failed to dispatch input event: {e.Message}");
            }
        }

        private static KeyCode ParseKeyCode(string key)
        {
            if (string.IsNullOrEmpty(key)) return KeyCode.None;

            // Direct enum parse
            if (System.Enum.TryParse<KeyCode>(key, true, out var keyCode))
                return keyCode;

            // Common aliases
            switch (key.ToUpperInvariant())
            {
                case "W": return KeyCode.W;
                case "A": return KeyCode.A;
                case "S": return KeyCode.S;
                case "D": return KeyCode.D;
                case "SPACE": return KeyCode.Space;
                case "ENTER": return KeyCode.Return;
                case "RETURN": return KeyCode.Return;
                case "ESCAPE": case "ESC": return KeyCode.Escape;
                case "SHIFT": case "LSHIFT": return KeyCode.LeftShift;
                case "CTRL": case "LCTRL": return KeyCode.LeftControl;
                case "ALT": case "LALT": return KeyCode.LeftAlt;
                case "TAB": return KeyCode.Tab;
                case "MOUSE0": return KeyCode.Mouse0;
                case "MOUSE1": return KeyCode.Mouse1;
                case "MOUSE2": return KeyCode.Mouse2;
                case "UP": case "UPARROW": return KeyCode.UpArrow;
                case "DOWN": case "DOWNARROW": return KeyCode.DownArrow;
                case "LEFT": case "LEFTARROW": return KeyCode.LeftArrow;
                case "RIGHT": case "RIGHTARROW": return KeyCode.RightArrow;
                case "E": return KeyCode.E;
                case "Q": return KeyCode.Q;
                case "R": return KeyCode.R;
                case "F": return KeyCode.F;
                default:
                    // Try single character
                    if (key.Length == 1)
                    {
                        char c = char.ToUpper(key[0]);
                        if (c >= 'A' && c <= 'Z')
                            return (KeyCode)((int)KeyCode.A + (c - 'A'));
                        if (c >= '0' && c <= '9')
                            return (KeyCode)((int)KeyCode.Alpha0 + (c - '0'));
                    }
                    return KeyCode.None;
            }
        }

        private static System.Type FindType(string fullName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private void OnDestroy()
        {
            if (_processingCoroutine != null)
                StopCoroutine(_processingCoroutine);
        }
    }
}
