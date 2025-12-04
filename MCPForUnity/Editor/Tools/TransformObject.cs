using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime;
using Trellis.Editor;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Handles object transformation/replacement operations.
    /// Allows transforming one object into another by finding existing assets or generating via Trellis.
    /// </summary>
    [McpForUnityTool("transform_object", AutoRegister = false, RequiresPolling = true, PollAction = "status")]
    public static class TransformObject
    {
        private const string ToolName = "transform_object";
        private const float DefaultPollIntervalSeconds = 3f;

        // Valid actions for this tool
        private static readonly List<string> ValidActions = new List<string>
        {
            "transform",   // Main action: transform source object to target
            "status",      // Poll action for checking generation status
            "revert",      // Revert to previous state
            "revert_original", // Revert to original state
            "list_history" // List all objects with transform history
        };

        /// <summary>
        /// State persisted across domain reloads for async Trellis generation.
        /// </summary>
        [Serializable]
        private class TransformJobState
        {
            public string status;           // "searching", "generating", "instantiating", "completed", "error"
            public string sourceObjectId;   // Instance ID of source object
            public string sourceObjectName;
            public string targetPrompt;
            public string foundAssetPath;   // If found existing asset
            public bool isGenerating;       // True if waiting for Trellis
            public string generatedGlbPath; // Path to generated GLB
            public string errorMessage;
            public Vector3 originalPosition;
            public Vector3 originalRotation;  // Euler angles
            public Vector3 originalScale;
            public Vector3 originalBoundsSize;
            public string originalParentPath;
            public int originalSiblingIndex;
        }

        // Track active Trellis generation
        private static bool s_waitingForTrellis = false;
        private static string s_pendingGlbPath = null;

        public static object HandleCommand(JObject @params)
        {
            string action = @params["action"]?.ToString()?.ToLower() ?? "transform";

            if (!ValidActions.Contains(action))
            {
                string validActionsList = string.Join(", ", ValidActions);
                return new ErrorResponse($"Unknown action: '{action}'. Valid actions are: {validActionsList}");
            }

            try
            {
                switch (action)
                {
                    case "transform":
                        return StartTransform(@params);
                    case "status":
                        return CheckStatus(@params);
                    case "revert":
                        return RevertObject(@params, revertToOriginal: false);
                    case "revert_original":
                        return RevertObject(@params, revertToOriginal: true);
                    case "list_history":
                        return ListTransformHistory();
                    default:
                        return new ErrorResponse($"Unhandled action: {action}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TransformObject] Error: {e.Message}\n{e.StackTrace}");
                return new ErrorResponse($"Error executing transform_object: {e.Message}");
            }
        }

        /// <summary>
        /// Initiates the transform operation.
        /// </summary>
        private static object StartTransform(JObject @params)
        {
            string sourceObject = @params["source_object"]?.ToString();
            string targetName = @params["target_name"]?.ToString();
            bool searchExisting = @params["search_existing"]?.ToObject<bool>() ?? true;
            bool generateIfMissing = @params["generate_if_missing"]?.ToObject<bool>() ?? true;

            if (string.IsNullOrEmpty(sourceObject))
                return new ErrorResponse("'source_object' parameter is required.");
            if (string.IsNullOrEmpty(targetName))
                return new ErrorResponse("'target_name' parameter is required.");

            // Find the source object in scene
            GameObject sourceGo = FindSceneObject(sourceObject);
            if (sourceGo == null)
                return new ErrorResponse($"Source object '{sourceObject}' not found in scene.");

            // Capture source object info
            var sourceBounds = GetObjectBounds(sourceGo);
            var sourceTransform = sourceGo.transform;

            var state = new TransformJobState
            {
                status = "searching",
                sourceObjectId = sourceGo.GetInstanceID().ToString(),
                sourceObjectName = sourceGo.name,
                targetPrompt = targetName,
                originalPosition = sourceTransform.position,
                originalRotation = sourceTransform.eulerAngles,
                originalScale = sourceTransform.localScale,
                originalBoundsSize = sourceBounds.size,
                originalParentPath = GetGameObjectPath(sourceTransform.parent?.gameObject),
                originalSiblingIndex = sourceTransform.GetSiblingIndex()
            };

            // Step 1: Search for existing asset
            if (searchExisting)
            {
                string foundPath = SearchForAsset(targetName);
                if (!string.IsNullOrEmpty(foundPath))
                {
                    state.foundAssetPath = foundPath;
                    state.status = "instantiating";
                    McpJobStateStore.SaveState(ToolName, state);

                    // Immediately instantiate and complete
                    return CompleteTransform(state, sourceGo);
                }
            }

            // Step 2: Generate with Trellis if no asset found
            if (generateIfMissing)
            {
                state.status = "generating";
                state.isGenerating = true;
                McpJobStateStore.SaveState(ToolName, state);

                // Start Trellis generation
                StartTrellisGeneration(targetName, state);

                return new PendingResponse(
                    $"No existing asset found for '{targetName}'. Starting Trellis generation...",
                    DefaultPollIntervalSeconds,
                    new { state.status, state.sourceObjectName, state.targetPrompt }
                );
            }

            return new ErrorResponse($"No asset found for '{targetName}' and generation is disabled.");
        }

        /// <summary>
        /// Checks the status of an ongoing transform operation (poll action).
        /// </summary>
        private static object CheckStatus(JObject @params)
        {
            var state = McpJobStateStore.LoadState<TransformJobState>(ToolName);
            if (state == null)
            {
                return new { _mcp_status = "complete", message = "No active transform job." };
            }

            switch (state.status)
            {
                case "completed":
                    McpJobStateStore.ClearState(ToolName);
                    return new
                    {
                        _mcp_status = "complete",
                        message = $"Transform completed: '{state.sourceObjectName}' → '{state.targetPrompt}'",
                        data = new
                        {
                            sourceObject = state.sourceObjectName,
                            targetPrompt = state.targetPrompt,
                            assetUsed = state.foundAssetPath ?? state.generatedGlbPath,
                            wasGenerated = !string.IsNullOrEmpty(state.generatedGlbPath)
                        }
                    };

                case "error":
                    McpJobStateStore.ClearState(ToolName);
                    return new
                    {
                        _mcp_status = "error",
                        error = state.errorMessage ?? "Unknown error during transform"
                    };

                case "generating":
                    // Check if Trellis has completed
                    if (!string.IsNullOrEmpty(s_pendingGlbPath))
                    {
                        state.generatedGlbPath = s_pendingGlbPath;
                        state.status = "instantiating";
                        s_pendingGlbPath = null;
                        s_waitingForTrellis = false;
                        McpJobStateStore.SaveState(ToolName, state);

                        // Find source object again and complete
                        if (int.TryParse(state.sourceObjectId, out int instanceId))
                        {
                            GameObject sourceGo = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                            if (sourceGo != null)
                            {
                                return CompleteTransform(state, sourceGo);
                            }
                        }

                        state.status = "error";
                        state.errorMessage = "Source object was destroyed during generation.";
                        McpJobStateStore.SaveState(ToolName, state);
                    }

                    return new PendingResponse(
                        $"Generating model for '{state.targetPrompt}'...",
                        DefaultPollIntervalSeconds,
                        new { state.status, state.targetPrompt }
                    );

                default:
                    return new PendingResponse(
                        $"Transform in progress: {state.status}",
                        DefaultPollIntervalSeconds,
                        new { state.status }
                    );
            }
        }

        /// <summary>
        /// Completes the transform by instantiating the asset and replacing the source.
        /// </summary>
        private static object CompleteTransform(TransformJobState state, GameObject sourceGo)
        {
            string assetPath = state.foundAssetPath ?? state.generatedGlbPath;
            if (string.IsNullOrEmpty(assetPath))
            {
                state.status = "error";
                state.errorMessage = "No asset path available for instantiation.";
                McpJobStateStore.SaveState(ToolName, state);
                return new ErrorResponse(state.errorMessage);
            }

            // Convert to Assets-relative path if needed
            assetPath = ToAssetsRelativePath(assetPath);

            // Load the asset
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                state.status = "error";
                state.errorMessage = $"Failed to load asset at '{assetPath}'.";
                McpJobStateStore.SaveState(ToolName, state);
                return new ErrorResponse(state.errorMessage);
            }

            // Instantiate the new object
            GameObject newObject = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (newObject == null)
            {
                // Fallback to regular instantiate for non-prefab assets
                newObject = UnityEngine.Object.Instantiate(prefab);
            }

            if (newObject == null)
            {
                state.status = "error";
                state.errorMessage = $"Failed to instantiate asset '{assetPath}'.";
                McpJobStateStore.SaveState(ToolName, state);
                return new ErrorResponse(state.errorMessage);
            }

            Undo.RegisterCreatedObjectUndo(newObject, $"Transform {sourceGo.name} to {state.targetPrompt}");

            // Name the new object
            newObject.name = state.targetPrompt;

            // Apply transform - position and rotation
            newObject.transform.position = state.originalPosition;
            newObject.transform.rotation = Quaternion.Euler(state.originalRotation);

            // Calculate and apply scale to match original bounds
            var newBounds = GetObjectBounds(newObject);
            if (newBounds.size.magnitude > 0.001f && state.originalBoundsSize.magnitude > 0.001f)
            {
                // Calculate scale factor to match original bounding box
                Vector3 scaleRatio = new Vector3(
                    state.originalBoundsSize.x / newBounds.size.x,
                    state.originalBoundsSize.y / newBounds.size.y,
                    state.originalBoundsSize.z / newBounds.size.z
                );

                // Use uniform scale (average) to maintain proportions
                float uniformScale = (scaleRatio.x + scaleRatio.y + scaleRatio.z) / 3f;
                newObject.transform.localScale = Vector3.one * uniformScale;

                Debug.Log($"[TransformObject] Applied scale {uniformScale:F3} to match original bounds " +
                    $"(original: {state.originalBoundsSize}, new: {newBounds.size})");
            }

            // Set parent
            if (!string.IsNullOrEmpty(state.originalParentPath))
            {
                GameObject parent = GameObject.Find(state.originalParentPath);
                if (parent != null)
                {
                    newObject.transform.SetParent(parent.transform);
                    newObject.transform.SetSiblingIndex(state.originalSiblingIndex);
                }
            }

            // Add history component and record the transform
            var history = newObject.GetComponent<ObjectTransformHistory>();
            if (history == null)
            {
                history = newObject.AddComponent<ObjectTransformHistory>();
            }

            // Check if source has history (chained transform)
            var sourceHistory = sourceGo.GetComponent<ObjectTransformHistory>();
            if (sourceHistory != null)
            {
                history.CopyHistoryFrom(sourceHistory);
            }

            // Get source asset path if it's a prefab
            string sourceAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(sourceGo);

            history.RecordTransform(
                sourceObject: sourceGo,
                targetPrompt: state.targetPrompt,
                replacementAssetPath: assetPath,
                wasGenerated: !string.IsNullOrEmpty(state.generatedGlbPath),
                originalPosition: state.originalPosition,
                originalRotation: Quaternion.Euler(state.originalRotation),
                originalScale: state.originalScale,
                originalBoundsSize: state.originalBoundsSize,
                sourceAssetPath: sourceAssetPath
            );

            // Disable the source object
            Undo.RecordObject(sourceGo, $"Disable {sourceGo.name}");
            sourceGo.SetActive(false);

            // Mark scene dirty
            EditorUtility.SetDirty(newObject);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

            // Update state
            state.status = "completed";
            McpJobStateStore.SaveState(ToolName, state);

            Selection.activeGameObject = newObject;

            return new SuccessResponse(
                $"Successfully transformed '{state.sourceObjectName}' to '{state.targetPrompt}'",
                new
                {
                    newObjectName = newObject.name,
                    newObjectId = newObject.GetInstanceID(),
                    assetUsed = assetPath,
                    wasGenerated = !string.IsNullOrEmpty(state.generatedGlbPath),
                    disabledOriginal = state.sourceObjectName,
                    appliedScale = newObject.transform.localScale
                }
            );
        }

        /// <summary>
        /// Reverts an object to its previous or original state.
        /// </summary>
        private static object RevertObject(JObject @params, bool revertToOriginal)
        {
            string target = @params["target"]?.ToString();
            if (string.IsNullOrEmpty(target))
                return new ErrorResponse("'target' parameter is required for revert.");

            GameObject targetGo = FindSceneObject(target);
            if (targetGo == null)
                return new ErrorResponse($"Target object '{target}' not found in scene.");

            var history = targetGo.GetComponent<ObjectTransformHistory>();
            if (history == null || history.History.Count == 0)
                return new ErrorResponse($"Object '{target}' has no transform history to revert.");

            GameObject revertedObject;
            if (revertToOriginal)
            {
                revertedObject = history.RevertToOriginal();
            }
            else
            {
                revertedObject = history.RevertToPrevious();
            }

            if (revertedObject == null)
                return new ErrorResponse("Failed to revert - source object reference is missing.");

            Selection.activeGameObject = revertedObject;

            return new SuccessResponse(
                $"Reverted to '{revertedObject.name}'",
                new
                {
                    revertedObjectName = revertedObject.name,
                    revertedObjectId = revertedObject.GetInstanceID()
                }
            );
        }

        /// <summary>
        /// Lists all objects in scene with transform history.
        /// </summary>
        private static object ListTransformHistory()
        {
            var allHistories = UnityEngine.Object.FindObjectsByType<ObjectTransformHistory>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var results = new List<object>();
            foreach (var history in allHistories)
            {
                results.Add(new
                {
                    objectName = history.gameObject.name,
                    objectId = history.gameObject.GetInstanceID(),
                    isActive = history.gameObject.activeInHierarchy,
                    historyCount = history.History.Count,
                    latestTransform = history.LatestEntry != null ? new
                    {
                        from = history.LatestEntry.sourceObjectName,
                        to = history.LatestEntry.targetPrompt,
                        timestamp = history.LatestEntry.timestamp,
                        wasGenerated = history.LatestEntry.wasGenerated
                    } : null
                });
            }

            return new SuccessResponse(
                $"Found {results.Count} object(s) with transform history.",
                new { objects = results }
            );
        }

        #region Helper Methods

        /// <summary>
        /// Searches for an existing asset by name using substring matching.
        /// </summary>
        private static string SearchForAsset(string targetName)
        {
            // Search for prefabs and models containing the target name
            string[] searchFilters = new[] { "t:Prefab", "t:Model" };

            foreach (var filter in searchFilters)
            {
                string[] guids = AssetDatabase.FindAssets($"{filter} {targetName}");

                foreach (var guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    string fileName = Path.GetFileNameWithoutExtension(path);

                    // Substring match (case-insensitive)
                    if (fileName.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Debug.Log($"[TransformObject] Found existing asset: {path}");
                        return path;
                    }
                }
            }

            // Also check the Trellis results folder
            string trellisFolder = "Assets/TrellisResults";
            if (AssetDatabase.IsValidFolder(trellisFolder))
            {
                string[] trellisGuids = AssetDatabase.FindAssets("t:Model", new[] { trellisFolder });
                foreach (var guid in trellisGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    string fileName = Path.GetFileNameWithoutExtension(path);

                    if (fileName.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Debug.Log($"[TransformObject] Found existing Trellis asset: {path}");
                        return path;
                    }
                }
            }

            Debug.Log($"[TransformObject] No existing asset found for '{targetName}'");
            return null;
        }

        /// <summary>
        /// Starts Trellis model generation.
        /// </summary>
        private static void StartTrellisGeneration(string prompt, TransformJobState state)
        {
            try
            {
                var client = TrellisServiceHost.EnsureClient();
                s_waitingForTrellis = true;
                s_pendingGlbPath = null;

                // Subscribe to the GLB ready event
                client.AddGlbReadyListener(OnTrellisGlbReady);

                // Start generation
                client.SubmitPrompt(prompt);

                Debug.Log($"[TransformObject] Started Trellis generation for '{prompt}'");
            }
            catch (Exception e)
            {
                Debug.LogError($"[TransformObject] Failed to start Trellis generation: {e.Message}");
                state.status = "error";
                state.errorMessage = $"Failed to start Trellis: {e.Message}";
                McpJobStateStore.SaveState(ToolName, state);
            }
        }

        /// <summary>
        /// Callback when Trellis finishes generating a GLB.
        /// </summary>
        private static void OnTrellisGlbReady(string remoteUrl, string localPath)
        {
            Debug.Log($"[TransformObject] Trellis GLB ready: {localPath}");

            // Remove listener
            try
            {
                var client = TrellisServiceHost.EnsureClient();
                client.RemoveGlbReadyListener(OnTrellisGlbReady);
            }
            catch { }

            s_pendingGlbPath = localPath;
            s_waitingForTrellis = false;

            // Refresh asset database to make the GLB available
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Finds a GameObject in the scene by name or path.
        /// </summary>
        private static GameObject FindSceneObject(string nameOrPath)
        {
            // Try direct find (path)
            GameObject go = GameObject.Find(nameOrPath);
            if (go != null) return go;

            // Try by name in all scene objects
            var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var obj in allObjects)
            {
                if (obj.name == nameOrPath)
                    return obj;
            }

            // Try partial match
            foreach (var obj in allObjects)
            {
                if (obj.name.IndexOf(nameOrPath, StringComparison.OrdinalIgnoreCase) >= 0)
                    return obj;
            }

            return null;
        }

        /// <summary>
        /// Gets the world-space bounds of an object (from all renderers or colliders).
        /// </summary>
        private static Bounds GetObjectBounds(GameObject go)
        {
            Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
            bool hasBounds = false;

            // Try renderers first
            var renderers = go.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            // Fallback to colliders
            if (!hasBounds)
            {
                var colliders = go.GetComponentsInChildren<Collider>();
                foreach (var collider in colliders)
                {
                    if (!hasBounds)
                    {
                        bounds = collider.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(collider.bounds);
                    }
                }
            }

            // Fallback to transform position with small default size
            if (!hasBounds || bounds.size.magnitude < 0.001f)
            {
                bounds = new Bounds(go.transform.position, Vector3.one);
            }

            return bounds;
        }

        /// <summary>
        /// Gets the full hierarchy path of a GameObject.
        /// </summary>
        private static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return null;

            string path = go.name;
            Transform current = go.transform.parent;

            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        /// <summary>
        /// Converts an absolute path to Assets-relative path.
        /// </summary>
        private static string ToAssetsRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            // Already relative
            if (path.StartsWith("Assets/") || path.StartsWith("Assets\\"))
                return path;

            // Convert absolute to relative
            string dataPath = Application.dataPath;
            if (path.StartsWith(dataPath))
            {
                return "Assets" + path.Substring(dataPath.Length).Replace("\\", "/");
            }

            return path;
        }

        #endregion

        #region Parameters Class for Tool Discovery

        public class Parameters
        {
            [ToolParameter("Action to perform: transform, status, revert, revert_original, list_history", Required = false)]
            public string action { get; set; }

            [ToolParameter("Name or path of the source object to transform")]
            public string source_object { get; set; }

            [ToolParameter("Name of what to transform into (e.g., 'sprinkler', 'tree')")]
            public string target_name { get; set; }

            [ToolParameter("Whether to search for existing assets first", Required = false)]
            public bool? search_existing { get; set; }

            [ToolParameter("Whether to generate via Trellis if no asset found", Required = false)]
            public bool? generate_if_missing { get; set; }

            [ToolParameter("Target object for revert actions")]
            public string target { get; set; }
        }

        #endregion
    }
}
