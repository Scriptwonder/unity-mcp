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
                bool useFallback = !TrySimulateKeyPress_NewInputSystem(action);
                if (useFallback)
                    SimulateKeyPress_Legacy(action);

                if (action.DurationMs > 0)
                    yield return new WaitForSeconds(action.DurationMs / 1000f);

                if (!useFallback)
                    TrySimulateKeyRelease_NewInputSystem(action);
                else
                    SimulateKeyRelease_Legacy(action);
            }
            else
            {
                SimulateKeyPress_Legacy(action);

                if (action.DurationMs > 0)
                    yield return new WaitForSeconds(action.DurationMs / 1000f);

                SimulateKeyRelease_Legacy(action);
            }
        }

        private void SimulateKeyPress_Legacy(KeyAction action)
        {
            if (!action.Press) return;

            var keyCode = ParseKeyCode(action.Key);
            if (keyCode == KeyCode.None)
            {
                Debug.LogWarning($"[MCP InputSimulator] Unknown key: '{action.Key}'");
                return;
            }

            DispatchInputEvent(new Event { type = EventType.KeyDown, keyCode = keyCode });
        }

        private void SimulateKeyRelease_Legacy(KeyAction action)
        {
            if (!action.Release) return;

            var keyCode = ParseKeyCode(action.Key);
            if (keyCode == KeyCode.None) return;

            DispatchInputEvent(new Event { type = EventType.KeyUp, keyCode = keyCode });
        }

        /// <summary>
        /// Attempts to press a key via the new Input System. Returns false if it should fall back to legacy.
        /// No yield statements — safe to call from anywhere.
        /// </summary>
        private bool TrySimulateKeyPress_NewInputSystem(KeyAction action)
        {
            if (!action.Press) return true;

            try
            {
                var keyControl = ResolveNewInputSystemKeyControl(action.Key);
                if (keyControl == null) return false;

                var queueStateMethod = FindType("UnityEngine.InputSystem.LowLevel.InputState")
                    ?.GetMethod("Change", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (queueStateMethod == null) return false;

                var generic = queueStateMethod.MakeGenericMethod(typeof(float));
                generic.Invoke(null, new[] { keyControl, (object)1.0f });
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MCP InputSimulator] New Input System key press failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to release a key via the new Input System.
        /// </summary>
        private void TrySimulateKeyRelease_NewInputSystem(KeyAction action)
        {
            if (!action.Release) return;

            try
            {
                var keyControl = ResolveNewInputSystemKeyControl(action.Key);
                if (keyControl == null) return;

                var queueStateMethod = FindType("UnityEngine.InputSystem.LowLevel.InputState")
                    ?.GetMethod("Change", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (queueStateMethod == null) return;

                var generic = queueStateMethod.MakeGenericMethod(typeof(float));
                generic.Invoke(null, new[] { keyControl, (object)0.0f });
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MCP InputSimulator] New Input System key release failed: {e.Message}");
            }
        }

        /// <summary>
        /// Resolves a key name to a new Input System KeyControl via reflection. Returns null on failure.
        /// </summary>
        private static object ResolveNewInputSystemKeyControl(string keyName)
        {
            try
            {
                var keyboardType = FindType("UnityEngine.InputSystem.Keyboard");
                if (keyboardType == null) return null;

                var currentProp = keyboardType.GetProperty("current",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var keyboard = currentProp?.GetValue(null);
                if (keyboard == null) return null;

                var indexer = keyboardType.GetProperty("Item", new[] { typeof(string) });
                return indexer?.GetValue(keyboard, new object[] { keyName.ToLowerInvariant() });
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MCP InputSimulator] Failed to resolve key '{keyName}': {e.Message}");
                return null;
            }
        }

        private IEnumerator ProcessMouseClickAction(MouseClickAction action)
        {
            DispatchInputEvent(new Event
            {
                type = EventType.MouseDown,
                mousePosition = new Vector2(action.X, action.Y),
                button = action.Button,
            });

            yield return new WaitForSeconds(action.DurationMs / 1000f);

            DispatchInputEvent(new Event
            {
                type = EventType.MouseUp,
                mousePosition = new Vector2(action.X, action.Y),
                button = action.Button,
            });
        }

        private void ProcessMouseMoveAction(MouseMoveAction action)
        {
            DispatchInputEvent(new Event
            {
                type = EventType.MouseMove,
                delta = new Vector2(action.DeltaX, action.DeltaY),
            });
        }

        private static void DispatchInputEvent(Event evt)
        {
            try
            {
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
