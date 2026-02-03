using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Windows.Components.Connection
{
    /// <summary>
    /// Controller for the Connection section of the MCP For Unity editor window.
    /// Handles HTTP scope selection, connection status, and health checks.
    /// </summary>
    public class McpConnectionSection
    {
        // HTTP scope enum
        private enum HttpScope
        {
            Local,
            Remote
        }

        // UI Elements
        private EnumField transportDropdown;
        private VisualElement httpUrlRow;
        private VisualElement httpServerControlRow;
        private Foldout manualCommandFoldout;
        private VisualElement httpServerCommandSection;
        private TextField httpServerCommandField;
        private Button copyHttpServerCommandButton;
        private Label httpServerCommandHint;
        private TextField httpUrlField;
        private Button startHttpServerButton;
        private Button stopHttpServerButton;
        private VisualElement statusIndicator;
        private Label connectionStatusLabel;
        private Button connectionToggleButton;

        private bool connectionToggleInProgress;
        private bool httpServerToggleInProgress;
        private Task verificationTask;
        private string lastHealthStatus;
        private double lastLocalServerRunningPollTime;
        private bool lastLocalServerRunning;

        // Reference to Advanced section for health status updates
        private Action<bool, string> onHealthStatusUpdate;

        public VisualElement Root { get; private set; }

        public void SetHealthStatusUpdateCallback(Action<bool, string> callback)
        {
            onHealthStatusUpdate = callback;
        }

        public McpConnectionSection(VisualElement root)
        {
            Root = root;
            CacheUIElements();
            InitializeUI();
            RegisterCallbacks();
        }

        private void CacheUIElements()
        {
            transportDropdown = Root.Q<EnumField>("transport-dropdown");
            httpUrlRow = Root.Q<VisualElement>("http-url-row");
            httpServerControlRow = Root.Q<VisualElement>("http-server-control-row");
            manualCommandFoldout = Root.Q<Foldout>("manual-command-foldout");
            httpServerCommandSection = Root.Q<VisualElement>("http-server-command-section");
            httpServerCommandField = Root.Q<TextField>("http-server-command");
            copyHttpServerCommandButton = Root.Q<Button>("copy-http-server-command-button");
            httpServerCommandHint = Root.Q<Label>("http-server-command-hint");
            httpUrlField = Root.Q<TextField>("http-url");
            startHttpServerButton = Root.Q<Button>("start-http-server-button");
            stopHttpServerButton = Root.Q<Button>("stop-http-server-button");
            statusIndicator = Root.Q<VisualElement>("status-indicator");
            connectionStatusLabel = Root.Q<Label>("connection-status");
            connectionToggleButton = Root.Q<Button>("connection-toggle");
        }

        private void InitializeUI()
        {
            // Ensure manual command foldout starts collapsed
            if (manualCommandFoldout != null)
            {
                manualCommandFoldout.value = false;
            }

            transportDropdown.Init(HttpScope.Local);
            // Back-compat: if scope pref isn't set yet, infer from current URL.
            string scope = EditorPrefs.GetString(EditorPrefKeys.HttpTransportScope, string.Empty);
            if (string.IsNullOrEmpty(scope))
            {
                scope = MCPServiceLocator.Server.IsLocalUrl() ? "local" : "remote";
                try
                {
                    EditorPrefs.SetString(EditorPrefKeys.HttpTransportScope, scope);
                }
                catch
                {
                    McpLog.Debug("Failed to set HttpTransportScope pref.");
                }
            }

            transportDropdown.value = scope == "remote" ? HttpScope.Remote : HttpScope.Local;

            // Set tooltips
            if (httpUrlField != null)
                httpUrlField.tooltip = "HTTP endpoint URL for the CLI bridge server. Use localhost for local servers.";
            if (connectionToggleButton != null)
                connectionToggleButton.tooltip = "Start or end the Unity CLI bridge session.";

            httpUrlField.value = HttpEndpointUtility.GetBaseUrl();

            UpdateHttpFieldVisibility();
            RefreshHttpUi();
            UpdateConnectionStatus();
        }

        private void RegisterCallbacks()
        {
            transportDropdown.RegisterValueChangedCallback(evt =>
            {
                var selected = (HttpScope)evt.newValue;
                string scope = selected == HttpScope.Remote ? "remote" : "local";
                EditorPrefs.SetString(EditorPrefKeys.HttpTransportScope, scope);

                UpdateHttpFieldVisibility();
                RefreshHttpUi();
                UpdateConnectionStatus();
                McpLog.Info($"HTTP scope changed to: {scope}");
            });

            // Don't normalize/overwrite the URL on every keystroke (it fights the user and can duplicate schemes).
            // Instead, persist + normalize on focus-out / Enter, then update UI once.
            httpUrlField.RegisterCallback<FocusOutEvent>(_ => PersistHttpUrlFromField());
            httpUrlField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    PersistHttpUrlFromField();
                    evt.StopPropagation();
                }
            });

            if (startHttpServerButton != null)
            {
                startHttpServerButton.clicked += OnHttpServerToggleClicked;
            }

            if (stopHttpServerButton != null)
            {
                // Stop button removed from UXML as part of consolidated Start/Stop UX.
                // Kept null-check for backward compatibility if older UXML is loaded.
                stopHttpServerButton.clicked += () =>
                {
                    // In older UXML layouts, route the stop button to the consolidated toggle behavior.
                    // If a session is active, this will end it and attempt to stop the local server.
                    OnHttpServerToggleClicked();
                };
            }

            if (copyHttpServerCommandButton != null)
            {
                copyHttpServerCommandButton.clicked += () =>
                {
                    if (!string.IsNullOrEmpty(httpServerCommandField?.value) && copyHttpServerCommandButton.enabledSelf)
                    {
                        EditorGUIUtility.systemCopyBuffer = httpServerCommandField.value;
                        McpLog.Info("HTTP server command copied to clipboard.");
                    }
                };
            }

            connectionToggleButton.clicked += OnConnectionToggleClicked;
        }

        private void PersistHttpUrlFromField()
        {
            if (httpUrlField == null)
            {
                return;
            }

            HttpEndpointUtility.SaveBaseUrl(httpUrlField.text);
            // Update displayed value to normalized form without re-triggering callbacks/caret jumps.
            httpUrlField.SetValueWithoutNotify(HttpEndpointUtility.GetBaseUrl());
            RefreshHttpUi();
        }

        public void UpdateConnectionStatus()
        {
            var bridgeService = MCPServiceLocator.Bridge;
            bool isRunning = bridgeService.IsRunning;
            bool showLocalServerControls = IsHttpLocalSelected();

            // Keep the Start/Stop Server button label in sync even when the session is not running
            // (e.g., orphaned server after a domain reload).
            // NOTE: This also updates lastLocalServerRunning which is used below for session toggle visibility.
            UpdateStartHttpButtonState();

            // Detect orphaned session: if HTTP Local session thinks it's running but the server is gone,
            // automatically end the session to keep UI in sync with reality.
            if (showLocalServerControls && isRunning && !lastLocalServerRunning && !connectionToggleInProgress)
            {
                McpLog.Info("Server no longer running; ending orphaned session.");
                _ = EndOrphanedSessionAsync();
                isRunning = false; // Update local state for the rest of this method
            }

            // For HTTP Local: show session toggle button only when server is running (so user can manually start/end session).
            // For HTTP Remote: always show the session toggle button.
            // This separates server lifecycle from session lifecycle for multi-instance scenarios.
            // We use lastLocalServerRunning which was just refreshed by UpdateStartHttpButtonState() above.
            if (connectionToggleButton != null)
            {
                bool showSessionToggle = !showLocalServerControls || lastLocalServerRunning;
                connectionToggleButton.style.display = showSessionToggle ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (isRunning)
            {
                // Show instance name (project folder name) for better identification in multi-instance scenarios.
                // Defensive: handle edge cases where path parsing might return null/empty.
                string projectDir = System.IO.Path.GetDirectoryName(Application.dataPath);
                string instanceName = !string.IsNullOrEmpty(projectDir) 
                    ? System.IO.Path.GetFileName(projectDir) 
                    : "Unity";
                if (string.IsNullOrEmpty(instanceName)) instanceName = "Unity";
                connectionStatusLabel.text = $"Session Active ({instanceName})";
                statusIndicator.RemoveFromClassList("disconnected");
                statusIndicator.AddToClassList("connected");
                connectionToggleButton.text = "End Session";
                connectionToggleButton.SetEnabled(true); // Re-enable in case it was disabled during resumption
            }
            else
            {
                connectionStatusLabel.text = "No Session";
                statusIndicator.RemoveFromClassList("connected");
                statusIndicator.AddToClassList("disconnected");
                connectionToggleButton.text = "Start Session";
                connectionToggleButton.SetEnabled(true);
            }
            connectionToggleButton?.EnableInClassList("server-running", isRunning);
        }

        public void UpdateHttpServerCommandDisplay()
        {
            if (httpServerCommandSection == null || httpServerCommandField == null)
            {
                return;
            }

            bool httpLocalSelected = IsHttpLocalSelected();
            bool isLocalHttpUrl = MCPServiceLocator.Server.IsLocalUrl();

            // Only show the local-server helper UI when HTTP Local is selected.
            if (!httpLocalSelected)
            {
                httpServerCommandSection.style.display = DisplayStyle.None;
                httpServerCommandField.value = string.Empty;
                httpServerCommandField.tooltip = string.Empty;
                httpServerCommandField.SetEnabled(false);
                if (httpServerCommandHint != null)
                {
                    httpServerCommandHint.text = string.Empty;
                }
                if (copyHttpServerCommandButton != null)
                {
                    copyHttpServerCommandButton.SetEnabled(false);
                }
                return;
            }

            httpServerCommandSection.style.display = DisplayStyle.Flex;

            if (!isLocalHttpUrl)
            {
                httpServerCommandField.value = string.Empty;
                httpServerCommandField.tooltip = string.Empty;
                httpServerCommandField.SetEnabled(false);
                httpServerCommandSection.EnableInClassList("http-local-invalid-url", true);
                if (httpServerCommandHint != null)
                {
                    httpServerCommandHint.text = "⚠ HTTP Local requires a localhost URL (localhost/127.0.0.1/0.0.0.0/::1).";
                    httpServerCommandHint.AddToClassList("http-local-url-error");
                }
                copyHttpServerCommandButton?.SetEnabled(false);
                return;
            }

            httpServerCommandSection.EnableInClassList("http-local-invalid-url", false);
            httpServerCommandField.SetEnabled(true);
            if (httpServerCommandHint != null)
            {
                httpServerCommandHint.RemoveFromClassList("http-local-url-error");
            }

            if (MCPServiceLocator.Server.TryGetLocalHttpServerCommand(out var command, out var error))
            {
                httpServerCommandField.value = command;
                httpServerCommandField.tooltip = command;
                if (httpServerCommandHint != null)
                {
                    httpServerCommandHint.text = "Run this command in your shell if you prefer to start the server manually.";
                }
                if (copyHttpServerCommandButton != null)
                {
                    copyHttpServerCommandButton.SetEnabled(true);
                }
            }
            else
            {
                httpServerCommandField.value = string.Empty;
                httpServerCommandField.tooltip = string.Empty;
                if (httpServerCommandHint != null)
                {
                    httpServerCommandHint.text = error ?? "The command is not available with the current configuration.";
                }
                if (copyHttpServerCommandButton != null)
                {
                    copyHttpServerCommandButton.SetEnabled(false);
                }
            }
        }

        private void UpdateHttpFieldVisibility()
        {
            bool httpLocalSelected = IsHttpLocalSelected();

            httpUrlRow.style.display = DisplayStyle.Flex;
            httpServerControlRow.style.display = httpLocalSelected ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private bool IsHttpLocalSelected()
        {
            return transportDropdown != null && (HttpScope)transportDropdown.value == HttpScope.Local;
        }

        private void UpdateStartHttpButtonState()
        {
            if (startHttpServerButton == null)
                return;

            bool httpLocalSelected = IsHttpLocalSelected();
            bool canStartLocalServer = httpLocalSelected && MCPServiceLocator.Server.IsLocalUrl();
            bool localServerRunning = false;

            // Avoid running expensive port/PID checks every UI tick; use a fast socket probe for UI state.
            if (httpLocalSelected)
            {
                double now = EditorApplication.timeSinceStartup;
                if ((now - lastLocalServerRunningPollTime) > 0.75f || httpServerToggleInProgress)
                {
                    lastLocalServerRunningPollTime = now;
                    lastLocalServerRunning = MCPServiceLocator.Server.IsLocalHttpServerReachable();
                }
                localServerRunning = lastLocalServerRunning;
            }

            // Server button only controls server lifecycle (Start/Stop Server).
            // Session lifecycle is handled by the separate connectionToggleButton.
            bool shouldShowStop = localServerRunning;
            startHttpServerButton.text = shouldShowStop ? "Stop Server" : "Start Server";
            // Note: Server logs may contain transient HTTP 400s on /mcp during startup probing and
            // CancelledError stack traces on shutdown when streaming requests are cancelled; this is expected.
            startHttpServerButton.EnableInClassList("server-running", localServerRunning);
            startHttpServerButton.SetEnabled(
                !httpServerToggleInProgress && (shouldShowStop || canStartLocalServer));
            startHttpServerButton.tooltip = httpLocalSelected
                ? (canStartLocalServer ? string.Empty : "HTTP Local requires a localhost URL (localhost/127.0.0.1/0.0.0.0/::1).")
                : "Server control is only available for local scope.";

            // Stop button is no longer used; it may be null depending on UXML version.
            stopHttpServerButton?.SetEnabled(false);
        }

        private void RefreshHttpUi()
        {
            UpdateStartHttpButtonState();
            UpdateHttpServerCommandDisplay();
        }

        private async void OnHttpServerToggleClicked()
        {
            if (httpServerToggleInProgress)
            {
                return;
            }

            var bridgeService = MCPServiceLocator.Bridge;
            httpServerToggleInProgress = true;
            startHttpServerButton?.SetEnabled(false);

            try
            {
                // Check if a local server is running.
                bool serverRunning = IsHttpLocalSelected() && MCPServiceLocator.Server.IsLocalHttpServerReachable();

                if (serverRunning)
                {
                    // Stop Server: end session first (if active), then stop the server.
                    if (bridgeService.IsRunning)
                    {
                        await bridgeService.StopAsync();
                    }
                    bool stopped = MCPServiceLocator.Server.StopLocalHttpServer();
                    if (!stopped)
                    {
                        McpLog.Warn("Failed to stop HTTP server or no server was running");
                    }
                }
                else
                {
                    // Start Server: launch the local HTTP server.
                    // When WE start the server, auto-start our session (we clearly want to use it).
                    // This differs from detecting an already-running server, where we require manual session start.
                    bool serverStarted = MCPServiceLocator.Server.StartLocalHttpServer();
                    if (serverStarted)
                    {
                        await TryAutoStartSessionAsync();
                    }
                    else
                    {
                        McpLog.Warn("Failed to start local HTTP server");
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"HTTP server toggle failed: {ex.Message}");
                EditorUtility.DisplayDialog("Error", $"Failed to toggle local HTTP server:\n\n{ex.Message}", "OK");
            }
            finally
            {
                httpServerToggleInProgress = false;
                RefreshHttpUi();
                UpdateConnectionStatus();
            }
        }

        private async Task TryAutoStartSessionAsync()
        {
            // Wait briefly for the HTTP server to become ready, then start the session.
            // This is called when THIS instance starts the server (not when detecting an external server).
            var bridgeService = MCPServiceLocator.Bridge;
            // Windows/dev mode may take much longer due to uv package resolution, fresh downloads, antivirus scans, etc.
            const int maxAttempts = 30;
            // Use shorter delays initially, then longer delays to allow server startup
            var shortDelay = TimeSpan.FromMilliseconds(500);
            var longDelay = TimeSpan.FromSeconds(3);

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var delay = attempt < 6 ? shortDelay : longDelay;

                // Check if server is actually accepting connections
                bool serverDetected = MCPServiceLocator.Server.IsLocalHttpServerReachable();

                if (serverDetected)
                {
                    // Server detected - try to connect
                    bool started = await bridgeService.StartAsync();
                    if (started)
                    {
                        await VerifyBridgeConnectionAsync();
                        UpdateConnectionStatus();
                        return;
                    }
                }
                else if (attempt >= 20)
                {
                    // After many attempts without detection, try connecting anyway as a last resort.
                    // This handles cases where process detection fails but the server is actually running.
                    // Only try once every 3 attempts to avoid spamming connection errors (at attempts 20, 23, 26, 29).
                    if ((attempt - 20) % 3 != 0) continue;
                    
                    bool started = await bridgeService.StartAsync();
                    if (started)
                    {
                        await VerifyBridgeConnectionAsync();
                        UpdateConnectionStatus();
                        return;
                    }
                }

                if (attempt < maxAttempts - 1)
                {
                    await Task.Delay(delay);
                }
            }

            McpLog.Warn("Failed to auto-start session after launching the HTTP server.");
        }

        private async void OnConnectionToggleClicked()
        {
            if (connectionToggleInProgress)
            {
                return;
            }

            var bridgeService = MCPServiceLocator.Bridge;
            connectionToggleInProgress = true;
            connectionToggleButton?.SetEnabled(false);

            try
            {
                if (bridgeService.IsRunning)
                {
                    // Clear any resume flags when user manually ends the session to prevent
                    // getting stuck in "Resuming..." state (the flag may have been set by a
                    // domain reload that started just before the user clicked End Session)
                    try { EditorPrefs.DeleteKey(EditorPrefKeys.ResumeHttpAfterReload); } catch { }

                    await bridgeService.StopAsync();
                }
                else
                {
                    bool started = await bridgeService.StartAsync();
                    if (started)
                    {
                        await VerifyBridgeConnectionAsync();
                    }
                    else
                    {
                        McpLog.Warn("Failed to start CLI bridge");
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"Connection toggle failed: {ex.Message}");
                EditorUtility.DisplayDialog("Connection Error",
                    $"Failed to toggle the CLI bridge connection:\n\n{ex.Message}",
                    "OK");
            }
            finally
            {
                connectionToggleInProgress = false;
                connectionToggleButton?.SetEnabled(true);
                UpdateConnectionStatus();
            }
        }

        private async Task EndOrphanedSessionAsync()
        {
            // Fire-and-forget cleanup of orphaned session when server is no longer running.
            // This prevents the UI from showing "Session Active" when the underlying server is gone.
            try
            {
                connectionToggleInProgress = true;
                connectionToggleButton?.SetEnabled(false);

                // Clear resume flags to prevent getting stuck in "Resuming..." state
                try { EditorPrefs.DeleteKey(EditorPrefKeys.ResumeHttpAfterReload); } catch { }

                await MCPServiceLocator.Bridge.StopAsync();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to end orphaned session: {ex.Message}");
            }
            finally
            {
                connectionToggleInProgress = false;
                connectionToggleButton?.SetEnabled(true);
                UpdateConnectionStatus();
            }
        }

        public async Task VerifyBridgeConnectionAsync()
        {
            // Prevent concurrent verification calls
            if (verificationTask != null && !verificationTask.IsCompleted)
            {
                return;
            }

            verificationTask = VerifyBridgeConnectionInternalAsync();
            await verificationTask;
        }

        private async Task VerifyBridgeConnectionInternalAsync()
        {
            var bridgeService = MCPServiceLocator.Bridge;
            if (!bridgeService.IsRunning)
            {
                onHealthStatusUpdate?.Invoke(false, HealthStatus.Unknown);

                // Only log if state changed
                if (lastHealthStatus != HealthStatus.Unknown)
                {
                    McpLog.Warn("Cannot verify connection: Bridge is not running");
                    lastHealthStatus = HealthStatus.Unknown;
                }
                return;
            }

            var result = await bridgeService.VerifyAsync();

            string newStatus;
            bool isHealthy;
            if (result.Success && result.PingSucceeded)
            {
                newStatus = HealthStatus.Healthy;
                isHealthy = true;

                // Only log if state changed
                if (lastHealthStatus != newStatus)
                {
                    McpLog.Debug($"Connection verification successful: {result.Message}");
                    lastHealthStatus = newStatus;
                }
            }
            else if (result.HandshakeValid)
            {
                newStatus = HealthStatus.PingFailed;
                isHealthy = false;

                // Log once per distinct warning state
                if (lastHealthStatus != newStatus)
                {
                    McpLog.Warn($"Connection verification warning: {result.Message}");
                    lastHealthStatus = newStatus;
                }
            }
            else
            {
                newStatus = HealthStatus.Unhealthy;
                isHealthy = false;

                // Log once per distinct error state
                if (lastHealthStatus != newStatus)
                {
                    McpLog.Error($"Connection verification failed: {result.Message}");
                    lastHealthStatus = newStatus;
                }
            }

            onHealthStatusUpdate?.Invoke(isHealthy, newStatus);
        }

    }
}
