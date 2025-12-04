using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime;
using Trellis.Editor;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Manages 3D model generation and object transformation using Trellis AI.
    /// Supports generating new objects or transforming existing scene objects.
    /// </summary>
    [McpForUnityTool("manage_3d_gen", AutoRegister = false, RequiresPolling = true, PollAction = "status")]
    public static class Manage3DGen
    {
        private const string ToolName = "manage_3d_gen";
        private const float DefaultPollIntervalSeconds = 3f;

        // Valid actions for this tool
        private static readonly List<string> ValidActions = new List<string>
        {
            "generate",    // Generate a new 3D object from prompt
            "transform",   // Transform existing source object to target
            "status",      // Poll action for checking generation status
            "revert",      // Revert to previous state
            "revert_original", // Revert to original state
            "list_history" // List all objects with transform history
        };

        /// <summary>
        /// State persisted across domain reloads for async Trellis generation.
        /// </summary>
        [Serializable]
        private class GenerationJobState
        {
            public string status;           // "searching", "generating", "loading_glb", "instantiating", "completed", "error"
            public string actionType;       // "generate" or "transform"
            public string sourceObjectId;   // Instance ID of source object (for transform)
            public string sourceObjectName; // Name of source object (for transform)
            public string targetPrompt;
            public string foundAssetPath;   // If found existing asset
            public bool isGenerating;       // True if waiting for Trellis
            public string generatedGlbPath; // Path to generated GLB
            public string errorMessage;
            public float[] originalPosition;    // [x, y, z]
            public float[] originalRotation;    // [x, y, z] Euler angles
            public float[] originalScale;       // [x, y, z]
            public float[] originalBoundsSize;  // [x, y, z] (for transform)
            public string originalParentPath;
            public int originalSiblingIndex;
            public string gltfLoadingContainerId; // Instance ID of container waiting for glTFast loading
        }

        // Track active Trellis generation
        private static bool s_waitingForTrellis = false;
        private static string s_pendingGlbPath = null;

        public static object HandleCommand(JObject @params)
        {
            string action = @params["action"]?.ToString()?.ToLower() ?? "generate";

            if (!ValidActions.Contains(action))
            {
                string validActionsList = string.Join(", ", ValidActions);
                return new ErrorResponse($"Unknown action: '{action}'. Valid actions are: {validActionsList}");
            }

            try
            {
                switch (action)
                {
                    case "generate":
                        return StartGenerate(@params);
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
                Debug.LogError($"[Manage3DGen] Error: {e.Message}\n{e.StackTrace}");
                return new ErrorResponse($"Error executing manage_3d_gen: {e.Message}");
            }
        }

        /// <summary>
        /// Initiates the generate operation - creates a NEW 3D object from a prompt.
        /// </summary>
        private static object StartGenerate(JObject @params)
        {
            string targetName = @params["target_name"]?.ToString();
            bool searchExisting = @params["search_existing"]?.ToObject<bool>() ?? true;
            bool generateIfMissing = @params["generate_if_missing"]?.ToObject<bool>() ?? true;

            if (string.IsNullOrEmpty(targetName))
                return new ErrorResponse("'target_name' parameter is required for generate action.");

            // Parse position, rotation, scale with defaults
            float[] position = ParseVector3Array(@params["position"]) ?? new float[] { 0, 0, 0 };
            float[] rotation = ParseVector3Array(@params["rotation"]) ?? new float[] { 0, 0, 0 };
            float[] scale = ParseVector3Array(@params["scale"]) ?? new float[] { 1, 1, 1 };
            string parentPath = @params["parent"]?.ToString();

            var state = new GenerationJobState
            {
                status = "searching",
                actionType = "generate",
                targetPrompt = targetName,
                originalPosition = position,
                originalRotation = rotation,
                originalScale = scale,
                originalParentPath = parentPath,
                originalSiblingIndex = -1 // Will be set to last sibling
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
                    return CompleteGenerate(state);
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
                    new { state.status, state.targetPrompt, action = "generate" }
                );
            }

            return new ErrorResponse($"No asset found for '{targetName}' and generation is disabled.");
        }

        /// <summary>
        /// Initiates the transform operation - replaces an existing object.
        /// </summary>
        private static object StartTransform(JObject @params)
        {
            string sourceObject = @params["source_object"]?.ToString();
            string targetName = @params["target_name"]?.ToString();
            bool searchExisting = @params["search_existing"]?.ToObject<bool>() ?? true;
            bool generateIfMissing = @params["generate_if_missing"]?.ToObject<bool>() ?? true;

            if (string.IsNullOrEmpty(sourceObject))
                return new ErrorResponse("'source_object' parameter is required for transform action.");
            if (string.IsNullOrEmpty(targetName))
                return new ErrorResponse("'target_name' parameter is required for transform action.");

            // Find the source object in scene
            GameObject sourceGo = FindSceneObject(sourceObject);
            if (sourceGo == null)
                return new ErrorResponse($"Source object '{sourceObject}' not found in scene.");

            // Capture source object info
            var sourceBounds = GetObjectBounds(sourceGo);
            var sourceTransform = sourceGo.transform;

            var state = new GenerationJobState
            {
                status = "searching",
                actionType = "transform",
                sourceObjectId = sourceGo.GetInstanceID().ToString(),
                sourceObjectName = sourceGo.name,
                targetPrompt = targetName,
                originalPosition = new float[] { sourceTransform.position.x, sourceTransform.position.y, sourceTransform.position.z },
                originalRotation = new float[] { sourceTransform.eulerAngles.x, sourceTransform.eulerAngles.y, sourceTransform.eulerAngles.z },
                originalScale = new float[] { sourceTransform.localScale.x, sourceTransform.localScale.y, sourceTransform.localScale.z },
                originalBoundsSize = new float[] { sourceBounds.size.x, sourceBounds.size.y, sourceBounds.size.z },
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
        /// Checks the status of an ongoing generation/transform operation (poll action).
        /// </summary>
        private static object CheckStatus(JObject @params)
        {
            var state = McpJobStateStore.LoadState<GenerationJobState>(ToolName);
            if (state == null)
            {
                return new { _mcp_status = "complete", message = "No active generation job." };
            }

            switch (state.status)
            {
                case "completed":
                    McpJobStateStore.ClearState(ToolName);
                    string completedMessage = state.actionType == "generate"
                        ? $"Generation completed: '{state.targetPrompt}'"
                        : $"Transform completed: '{state.sourceObjectName}' → '{state.targetPrompt}'";
                    return new
                    {
                        _mcp_status = "complete",
                        message = completedMessage,
                        data = new
                        {
                            actionType = state.actionType,
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
                        error = state.errorMessage ?? "Unknown error during operation"
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

                        // Handle based on action type
                        if (state.actionType == "generate")
                        {
                            return CompleteGenerate(state);
                        }
                        else
                        {
                            // Transform action - find source object again and complete
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
                    }

                    return new PendingResponse(
                        $"Generating model for '{state.targetPrompt}'...",
                        DefaultPollIntervalSeconds,
                        new { state.status, state.targetPrompt, state.actionType }
                    );

                case "loading_glb":
                    // Check if glTFast has completed loading
                    if (!string.IsNullOrEmpty(state.gltfLoadingContainerId) && 
                        int.TryParse(state.gltfLoadingContainerId, out int containerId))
                    {
                        GameObject container = EditorUtility.InstanceIDToObject(containerId) as GameObject;
                        
                        // Check if container has children (loading complete) or if loading failed
                        if (container == null)
                        {
                            state.status = "error";
                            state.errorMessage = "glTFast loading failed - container was destroyed.";
                            McpJobStateStore.SaveState(ToolName, state);
                            return new ErrorResponse(state.errorMessage);
                        }
                        
                        // Check if loading is complete (container has children when glTFast finishes)
                        if (container.transform.childCount > 0 || !s_gltfLoadingInProgress)
                        {
                            if (container.transform.childCount > 0)
                            {
                                Debug.Log($"[Manage3DGen] glTFast loading complete, container has {container.transform.childCount} children");
                                return FinalizeGltfLoading(state, container);
                            }
                            else
                            {
                                // Loading finished but no children - failure
                                state.status = "error";
                                state.errorMessage = "glTFast loading completed but no model was instantiated.";
                                UnityEngine.Object.DestroyImmediate(container);
                                McpJobStateStore.SaveState(ToolName, state);
                                return new ErrorResponse(state.errorMessage);
                            }
                        }
                    }
                    
                    return new PendingResponse(
                        $"Loading GLB model for '{state.targetPrompt}'...",
                        DefaultPollIntervalSeconds,
                        new { state.status, state.targetPrompt, state.actionType }
                    );

                default:
                    return new PendingResponse(
                        $"Operation in progress: {state.status}",
                        DefaultPollIntervalSeconds,
                        new { state.status, state.actionType }
                    );
            }
        }

        /// <summary>
        /// Finalizes the object after glTFast loading is complete.
        /// </summary>
        private static object FinalizeGltfLoading(GenerationJobState state, GameObject container)
        {
            bool isPlayMode = EditorApplication.isPlaying;
            
            // Name the object
            container.name = state.targetPrompt;
            
            // Apply transform - position and rotation from parameters
            Vector3 position = new Vector3(state.originalPosition[0], state.originalPosition[1], state.originalPosition[2]);
            Vector3 rotation = new Vector3(state.originalRotation[0], state.originalRotation[1], state.originalRotation[2]);
            Vector3 scale = new Vector3(state.originalScale[0], state.originalScale[1], state.originalScale[2]);

            container.transform.position = position;
            container.transform.rotation = Quaternion.Euler(rotation);
            container.transform.localScale = scale;

            // Set parent if specified
            if (!string.IsNullOrEmpty(state.originalParentPath))
            {
                GameObject parent = FindSceneObject(state.originalParentPath);
                if (parent != null)
                {
                    container.transform.SetParent(parent.transform);
                }
            }

            // Mark scene dirty (only in Edit Mode)
            if (!isPlayMode)
            {
                EditorUtility.SetDirty(container);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
            }

            // Update state
            state.status = "completed";
            state.gltfLoadingContainerId = null;
            McpJobStateStore.SaveState(ToolName, state);

            Selection.activeGameObject = container;

            return new SuccessResponse(
                $"Successfully generated '{state.targetPrompt}'" + (isPlayMode ? " (Play Mode via glTFast)" : ""),
                new
                {
                    newObjectName = container.name,
                    newObjectId = container.GetInstanceID(),
                    assetUsed = state.generatedGlbPath ?? state.foundAssetPath,
                    wasGenerated = !string.IsNullOrEmpty(state.generatedGlbPath),
                    playMode = isPlayMode,
                    loadedViaGltfFast = true,
                    position = new { x = position.x, y = position.y, z = position.z },
                    rotation = new { x = rotation.x, y = rotation.y, z = rotation.z },
                    scale = new { x = scale.x, y = scale.y, z = scale.z }
                }
            );
        }

        /// <summary>
        /// Finds an object that Trellis may have auto-instantiated from the GLB.
        /// Trellis typically creates objects with names matching the GLB filename.
        /// </summary>
        private static GameObject FindTrellisInstantiatedObject(string glbPath)
        {
            if (string.IsNullOrEmpty(glbPath)) return null;

            string baseName = Path.GetFileNameWithoutExtension(glbPath);
            
            // Look for recently created objects that match the GLB name
            var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var obj in allObjects)
            {
                // Check for exact match or match with (Clone) suffix
                if (obj.name == baseName || 
                    obj.name == baseName + "(Clone)" ||
                    obj.name.StartsWith(baseName))
                {
                    // Verify it's at origin (Trellis default placement)
                    if (obj.transform.position == Vector3.zero && obj.transform.parent == null)
                    {
                        Debug.Log($"[Manage3DGen] Found Trellis-instantiated object: {obj.name}");
                        return obj;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Completes the generate action by instantiating a NEW 3D object.
        /// If Trellis already instantiated the object, we reuse it instead of creating a duplicate.
        /// In Play Mode with GLB files, uses glTFast async loading.
        /// </summary>
        private static object CompleteGenerate(GenerationJobState state)
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
            
            bool isPlayMode = EditorApplication.isPlaying;
            bool isGlbFile = assetPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || 
                             assetPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

            // In Play Mode with GLB files, use async glTFast loading
            if (isPlayMode && isGlbFile)
            {
                GameObject container = LoadGlbWithGltfFast(assetPath);
                if (container != null)
                {
                    // Store container ID and set status to loading_glb
                    state.status = "loading_glb";
                    state.gltfLoadingContainerId = container.GetInstanceID().ToString();
                    McpJobStateStore.SaveState(ToolName, state);
                    
                    return new PendingResponse(
                        $"Loading GLB model for '{state.targetPrompt}' via glTFast...",
                        DefaultPollIntervalSeconds,
                        new { state.status, state.targetPrompt, state.actionType }
                    );
                }
                else
                {
                    state.status = "error";
                    state.errorMessage = $"Failed to start glTFast loading for '{assetPath}'.";
                    McpJobStateStore.SaveState(ToolName, state);
                    return new ErrorResponse(state.errorMessage);
                }
            }

            // Non-glTFast path (Edit Mode or non-GLB files)
            GameObject newObject = null;
            bool wasAlreadyInstantiated = false;

            // Check if Trellis already instantiated this object (for generated assets)
            if (!string.IsNullOrEmpty(state.generatedGlbPath))
            {
                newObject = FindTrellisInstantiatedObject(state.generatedGlbPath);
                if (newObject != null)
                {
                    wasAlreadyInstantiated = true;
                    Debug.Log($"[Manage3DGen] Reusing Trellis-instantiated object instead of creating duplicate");
                }
            }

            // If not found, load and instantiate
            if (newObject == null)
            {
                GameObject prefab = null;

                // Use the Play Mode compatible loader
                prefab = LoadAssetPlayModeCompatible(assetPath);

                if (prefab == null)
                {
                    // In Play Mode with a newly generated GLB, the asset might not be loadable
                    // but Trellis should have already instantiated it - search more broadly
                    if (isPlayMode && !string.IsNullOrEmpty(state.generatedGlbPath))
                    {
                        string baseName = Path.GetFileNameWithoutExtension(state.generatedGlbPath);
                        var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(
                            FindObjectsInactive.Include, FindObjectsSortMode.None);
                        
                        foreach (var obj in allObjects)
                        {
                            if (obj.name.Contains(baseName) || obj.name.Contains(state.targetPrompt))
                            {
                                newObject = obj;
                                Debug.Log($"[Manage3DGen] Found object by name search in Play Mode: {obj.name}");
                                break;
                            }
                        }
                        
                        if (newObject != null)
                        {
                            // Skip the rest of loading, we found it
                            goto FoundObject;
                        }
                    }
                    
                    state.status = "error";
                    state.errorMessage = $"Failed to load asset at '{assetPath}'.";
                    McpJobStateStore.SaveState(ToolName, state);
                    return new ErrorResponse(state.errorMessage);
                }

                // Instantiate the new object
                if (isPlayMode)
                {
                    // In Play Mode, use regular Instantiate
                    newObject = UnityEngine.Object.Instantiate(prefab);
                }
                else
                {
                    // In Edit Mode, prefer PrefabUtility for prefab link preservation
                    newObject = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                    if (newObject == null)
                    {
                        newObject = UnityEngine.Object.Instantiate(prefab);
                    }
                }

                if (newObject == null)
                {
                    state.status = "error";
                    state.errorMessage = $"Failed to instantiate asset '{assetPath}'.";
                    McpJobStateStore.SaveState(ToolName, state);
                    return new ErrorResponse(state.errorMessage);
                }

                // Only register Undo in Edit Mode
                if (!isPlayMode)
                {
                    Undo.RegisterCreatedObjectUndo(newObject, $"Generate {state.targetPrompt}");
                }
            }

            FoundObject:

            // Name the new object
            newObject.name = state.targetPrompt;

            // Apply transform - position and rotation from parameters
            Vector3 position = new Vector3(state.originalPosition[0], state.originalPosition[1], state.originalPosition[2]);
            Vector3 rotation = new Vector3(state.originalRotation[0], state.originalRotation[1], state.originalRotation[2]);
            Vector3 scale = new Vector3(state.originalScale[0], state.originalScale[1], state.originalScale[2]);

            newObject.transform.position = position;
            newObject.transform.rotation = Quaternion.Euler(rotation);
            newObject.transform.localScale = scale;

            // Set parent if specified
            if (!string.IsNullOrEmpty(state.originalParentPath))
            {
                GameObject parent = FindSceneObject(state.originalParentPath);
                if (parent != null)
                {
                    newObject.transform.SetParent(parent.transform);
                }
            }

            // Mark scene dirty (only in Edit Mode)
            if (!isPlayMode)
            {
                EditorUtility.SetDirty(newObject);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
            }

            // Update state
            state.status = "completed";
            McpJobStateStore.SaveState(ToolName, state);

            Selection.activeGameObject = newObject;

            return new SuccessResponse(
                $"Successfully generated '{state.targetPrompt}'" + (isPlayMode ? " (Play Mode)" : ""),
                new
                {
                    newObjectName = newObject.name,
                    newObjectId = newObject.GetInstanceID(),
                    assetUsed = assetPath,
                    wasGenerated = !string.IsNullOrEmpty(state.generatedGlbPath),
                    playMode = isPlayMode,
                    position = new { x = position.x, y = position.y, z = position.z },
                    rotation = new { x = rotation.x, y = rotation.y, z = rotation.z },
                    scale = new { x = scale.x, y = scale.y, z = scale.z }
                }
            );
        }

        /// <summary>
        /// Completes the transform by instantiating the asset and replacing the source.
        /// If Trellis already instantiated the object, we reuse it instead of creating a duplicate.
        /// Works in both Edit Mode and Play Mode.
        /// </summary>
        private static object CompleteTransform(GenerationJobState state, GameObject sourceGo)
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
            
            bool isPlayMode = EditorApplication.isPlaying;
            bool isGlbFile = assetPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || 
                             assetPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

            // In Play Mode with GLB files, use async glTFast loading
            // Note: For transform, we still need the source object, so we handle this differently
            if (isPlayMode && isGlbFile)
            {
                GameObject container = LoadGlbWithGltfFast(assetPath);
                if (container != null)
                {
                    // Store container ID and set status to loading_glb
                    state.status = "loading_glb";
                    state.gltfLoadingContainerId = container.GetInstanceID().ToString();
                    McpJobStateStore.SaveState(ToolName, state);
                    
                    return new PendingResponse(
                        $"Loading GLB model for transform to '{state.targetPrompt}' via glTFast...",
                        DefaultPollIntervalSeconds,
                        new { state.status, state.targetPrompt, state.actionType, state.sourceObjectName }
                    );
                }
                else
                {
                    state.status = "error";
                    state.errorMessage = $"Failed to start glTFast loading for '{assetPath}'.";
                    McpJobStateStore.SaveState(ToolName, state);
                    return new ErrorResponse(state.errorMessage);
                }
            }

            GameObject newObject = null;
            bool wasAlreadyInstantiated = false;

            // Check if Trellis already instantiated this object (for generated assets)
            if (!string.IsNullOrEmpty(state.generatedGlbPath))
            {
                newObject = FindTrellisInstantiatedObject(state.generatedGlbPath);
                if (newObject != null)
                {
                    wasAlreadyInstantiated = true;
                    Debug.Log($"[Manage3DGen] Reusing Trellis-instantiated object for transform");
                }
            }

            // If not found, load and instantiate
            if (newObject == null)
            {
                GameObject prefab = null;

                // Use the Play Mode compatible loader
                prefab = LoadAssetPlayModeCompatible(assetPath);

                if (prefab == null)
                {
                    // In Play Mode with a newly generated GLB, the asset might not be loadable
                    // but Trellis should have already instantiated it - search more broadly
                    if (isPlayMode && !string.IsNullOrEmpty(state.generatedGlbPath))
                    {
                        string baseName = Path.GetFileNameWithoutExtension(state.generatedGlbPath);
                        var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(
                            FindObjectsInactive.Include, FindObjectsSortMode.None);
                        
                        foreach (var obj in allObjects)
                        {
                            if (obj.name.Contains(baseName) || obj.name.Contains(state.targetPrompt))
                            {
                                newObject = obj;
                                Debug.Log($"[Manage3DGen] Found object by name search in Play Mode: {obj.name}");
                                break;
                            }
                        }
                        
                        if (newObject != null)
                        {
                            // Skip the rest of loading, we found it
                            goto FoundTransformObject;
                        }
                    }
                    
                    state.status = "error";
                    state.errorMessage = $"Failed to load asset at '{assetPath}'. In Play Mode, Trellis-generated assets may not be loadable immediately.";
                    McpJobStateStore.SaveState(ToolName, state);
                    return new ErrorResponse(state.errorMessage);
                }

                // Instantiate the new object
                if (isPlayMode)
                {
                    newObject = UnityEngine.Object.Instantiate(prefab);
                }
                else
                {
                    newObject = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                    if (newObject == null)
                    {
                        newObject = UnityEngine.Object.Instantiate(prefab);
                    }
                }

                if (newObject == null)
                {
                    state.status = "error";
                    state.errorMessage = $"Failed to instantiate asset '{assetPath}'.";
                    McpJobStateStore.SaveState(ToolName, state);
                    return new ErrorResponse(state.errorMessage);
                }

                if (!isPlayMode)
                {
                    Undo.RegisterCreatedObjectUndo(newObject, $"Transform {sourceGo.name} to {state.targetPrompt}");
                }
            }

            FoundTransformObject:

            // Name the new object
            newObject.name = state.targetPrompt;

            // Apply transform - position and rotation
            Vector3 position = new Vector3(state.originalPosition[0], state.originalPosition[1], state.originalPosition[2]);
            Vector3 rotation = new Vector3(state.originalRotation[0], state.originalRotation[1], state.originalRotation[2]);
            newObject.transform.position = position;
            newObject.transform.rotation = Quaternion.Euler(rotation);

            // Reset scale to (1,1,1) before measuring bounds to get the true mesh size
            newObject.transform.localScale = Vector3.one;

            // Calculate and apply scale to match original bounds
            var newBounds = GetObjectBounds(newObject);
            Vector3 originalBoundsSize = new Vector3(state.originalBoundsSize[0], state.originalBoundsSize[1], state.originalBoundsSize[2]);
            
            Debug.Log($"[Manage3DGen] Bounds check - Original: {originalBoundsSize}, New (at scale 1): {newBounds.size}");
            
            if (newBounds.size.magnitude > 0.001f && originalBoundsSize.magnitude > 0.001f)
            {
                // Calculate scale factor to match original bounding box
                Vector3 scaleRatio = new Vector3(
                    originalBoundsSize.x / newBounds.size.x,
                    originalBoundsSize.y / newBounds.size.y,
                    originalBoundsSize.z / newBounds.size.z
                );

                // Use the Y-axis (height) as the primary scaling reference for better visual matching
                // This works well for most objects like characters, trees, furniture, etc.
                float uniformScale = scaleRatio.y;
                newObject.transform.localScale = Vector3.one * uniformScale;

                Debug.Log($"[Manage3DGen] Applied scale {uniformScale:F3} (height-based) to match original bounds " +
                    $"(original: {originalBoundsSize}, new: {newBounds.size}, ratios: {scaleRatio})");
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

            // Get source asset path if it's a prefab (only works in Edit Mode)
            string sourceAssetPath = isPlayMode ? null : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(sourceGo);

            Vector3 originalScale = new Vector3(state.originalScale[0], state.originalScale[1], state.originalScale[2]);

            history.RecordTransform(
                sourceObject: sourceGo,
                targetPrompt: state.targetPrompt,
                replacementAssetPath: assetPath,
                wasGenerated: !string.IsNullOrEmpty(state.generatedGlbPath),
                originalPosition: position,
                originalRotation: Quaternion.Euler(rotation),
                originalScale: originalScale,
                originalBoundsSize: originalBoundsSize,
                sourceAssetPath: sourceAssetPath
            );

            // Disable the source object
            if (!isPlayMode)
            {
                Undo.RecordObject(sourceGo, $"Disable {sourceGo.name}");
            }
            sourceGo.SetActive(false);

            // Mark scene dirty (only in Edit Mode)
            if (!isPlayMode)
            {
                EditorUtility.SetDirty(newObject);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
            }

            // Update state
            state.status = "completed";
            McpJobStateStore.SaveState(ToolName, state);

            Selection.activeGameObject = newObject;

            return new SuccessResponse(
                $"Successfully transformed '{state.sourceObjectName}' to '{state.targetPrompt}'" + (isPlayMode ? " (Play Mode)" : ""),
                new
                {
                    newObjectName = newObject.name,
                    newObjectId = newObject.GetInstanceID(),
                    assetUsed = assetPath,
                    wasGenerated = !string.IsNullOrEmpty(state.generatedGlbPath),
                    playMode = isPlayMode,
                    disabledOriginal = state.sourceObjectName,
                    appliedScale = new { x = newObject.transform.localScale.x, y = newObject.transform.localScale.y, z = newObject.transform.localScale.z }
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
        /// Searches for an existing asset by name. Prioritizes exact matches over substring matches.
        /// </summary>
        private static string SearchForAsset(string targetName)
        {
            // Search for prefabs and models containing the target name
            string[] searchFilters = new[] { "t:Prefab", "t:Model" };
            
            // Collect all matches, categorized by match quality
            string exactMatch = null;
            string startsWithMatch = null;
            string containsMatch = null;

            foreach (var filter in searchFilters)
            {
                string[] guids = AssetDatabase.FindAssets($"{filter} {targetName}");

                foreach (var guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    string fileName = Path.GetFileNameWithoutExtension(path);

                    // Check for exact match (case-insensitive)
                    if (string.Equals(fileName, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        exactMatch = path;
                        Debug.Log($"[Manage3DGen] Found exact match asset: {path}");
                        break; // Exact match is best, stop searching
                    }
                    // Check if filename starts with target (e.g., "beehive_large" for "beehive")
                    else if (startsWithMatch == null && fileName.StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        startsWithMatch = path;
                    }
                    // Substring match (e.g., "slider_beehive" for "beehive") - lowest priority
                    else if (containsMatch == null && fileName.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        containsMatch = path;
                    }
                }
                
                if (exactMatch != null) break; // Stop if we found an exact match
            }

            // Also check the Trellis results folder with same priority logic
            string trellisFolder = "Assets/TrellisResults";
            if (exactMatch == null && AssetDatabase.IsValidFolder(trellisFolder))
            {
                string[] trellisGuids = AssetDatabase.FindAssets("t:Model", new[] { trellisFolder });
                foreach (var guid in trellisGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    string fileName = Path.GetFileNameWithoutExtension(path);

                    if (string.Equals(fileName, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        exactMatch = path;
                        Debug.Log($"[Manage3DGen] Found exact match Trellis asset: {path}");
                        break;
                    }
                    else if (startsWithMatch == null && fileName.StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        startsWithMatch = path;
                    }
                    else if (containsMatch == null && fileName.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        containsMatch = path;
                    }
                }
            }

            // Return best match in priority order: exact > starts with > contains
            string result = exactMatch ?? startsWithMatch ?? containsMatch;
            
            if (result != null)
            {
                string matchType = exactMatch != null ? "exact" : (startsWithMatch != null ? "starts-with" : "contains");
                Debug.Log($"[Manage3DGen] Using {matchType} match: {result}");
            }
            else
            {
                Debug.Log($"[Manage3DGen] No existing asset found for '{targetName}'");
            }
            
            return result;
        }

        /// <summary>
        /// Starts Trellis model generation.
        /// </summary>
        private static void StartTrellisGeneration(string prompt, GenerationJobState state)
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

                Debug.Log($"[Manage3DGen] Started Trellis generation for '{prompt}'");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Manage3DGen] Failed to start Trellis generation: {e.Message}");
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
            Debug.Log($"[Manage3DGen] Trellis GLB ready: {localPath}");

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
            // Use ImportAsset for more reliable import of new files
            string assetsRelativePath = ToAssetsRelativePath(localPath);
            AssetDatabase.ImportAsset(assetsRelativePath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
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

        /// <summary>
        /// Loads and instantiates a prefab, compatible with both Edit Mode and Play Mode.
        /// In Edit Mode: Uses AssetDatabase and PrefabUtility
        /// In Play Mode: Uses Resources.Load or direct instantiation
        /// </summary>
        private static GameObject LoadAssetPlayModeCompatible(string assetPath, Vector3 position, Quaternion rotation)
        {
            bool isPlayMode = EditorApplication.isPlaying;

            if (!isPlayMode)
            {
                // Edit Mode: Use standard Editor APIs
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null)
                {
                    Debug.LogWarning($"[Manage3DGen] Could not load prefab at: {assetPath}");
                    return null;
                }

                var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance != null)
                {
                    instance.transform.position = position;
                    instance.transform.rotation = rotation;
                    Undo.RegisterCreatedObjectUndo(instance, "Generate 3D Object");
                }
                return instance;
            }
            else
            {
                // Play Mode: Use runtime-compatible APIs
                GameObject prefab = null;
                
                // First try: Load from AssetDatabase (works in Play Mode in Editor)
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                
                if (prefab == null)
                {
                    // Second try: Check if it's a Resources path
                    string resourcesPath = assetPath;
                    if (resourcesPath.Contains("/Resources/"))
                    {
                        int idx = resourcesPath.IndexOf("/Resources/") + "/Resources/".Length;
                        resourcesPath = resourcesPath.Substring(idx);
                        resourcesPath = Path.ChangeExtension(resourcesPath, null); // Remove extension
                        prefab = UnityEngine.Resources.Load<GameObject>(resourcesPath);
                    }
                }

                if (prefab == null)
                {
                    Debug.LogWarning($"[Manage3DGen] Could not load prefab at: {assetPath} (Play Mode)");
                    return null;
                }

                // Instantiate using runtime API
                var instance = UnityEngine.Object.Instantiate(prefab, position, rotation);
                if (instance != null)
                {
                    // Clean up "(Clone)" suffix
                    instance.name = prefab.name;
                }
                return instance;
            }
        }

        /// <summary>
        /// Loads a prefab asset, compatible with both Edit Mode and Play Mode.
        /// Returns the prefab/asset without instantiation.
        /// In Play Mode, uses glTFast for runtime GLB/GLTF loading.
        /// </summary>
        private static GameObject LoadAssetPlayModeCompatible(string assetPath)
        {
            bool isPlayMode = EditorApplication.isPlaying;
            bool isGlbFile = assetPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || 
                             assetPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

            // In Play Mode with GLB files, use glTFast for runtime loading
            if (isPlayMode && isGlbFile)
            {
                return LoadGlbWithGltfFast(assetPath);
            }

            // Edit Mode: ensure GLB is imported first
            if (isGlbFile)
            {
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            }

            // AssetDatabase.LoadAssetAtPath works in Play Mode in the Editor
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            
            // If direct load failed, try loading the main asset (for models/GLB)
            if (prefab == null)
            {
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (mainAsset is GameObject go)
                {
                    prefab = go;
                }
                else if (mainAsset != null)
                {
                    Debug.Log($"[Manage3DGen] Main asset at '{assetPath}' is {mainAsset.GetType().Name}, not GameObject");
                    
                    // Try to find a GameObject in the asset's sub-assets
                    var subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                    foreach (var subAsset in subAssets)
                    {
                        if (subAsset is GameObject subGo)
                        {
                            prefab = subGo;
                            Debug.Log($"[Manage3DGen] Found GameObject sub-asset: {subGo.name}");
                            break;
                        }
                    }
                }
            }
            
            if (prefab == null && isPlayMode)
            {
                // Fallback: Try Resources.Load if it's in a Resources folder
                if (assetPath.Contains("/Resources/"))
                {
                    int idx = assetPath.IndexOf("/Resources/") + "/Resources/".Length;
                    string resourcesPath = assetPath.Substring(idx);
                    resourcesPath = Path.ChangeExtension(resourcesPath, null); // Remove extension
                    prefab = UnityEngine.Resources.Load<GameObject>(resourcesPath);
                }
            }

            if (prefab == null)
            {
                Debug.LogWarning($"[Manage3DGen] Could not load asset at: {assetPath}" + (isPlayMode ? " (Play Mode)" : "") + 
                    $". File exists: {System.IO.File.Exists(assetPath.Replace("Assets/", Application.dataPath + "/"))}");
            }

            return prefab;
        }

        /// <summary>
        /// Loads a GLB/GLTF file at runtime using glTFast.
        /// This works in Play Mode where Unity's import pipeline doesn't run.
        /// Note: This starts async loading. The result is retrieved via polling.
        /// </summary>
        private static GameObject LoadGlbWithGltfFast(string assetPath)
        {
            // Convert Assets-relative path to absolute file path
            string absolutePath = assetPath;
            if (assetPath.StartsWith("Assets/") || assetPath.StartsWith("Assets\\"))
            {
                absolutePath = Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
            }

            if (!File.Exists(absolutePath))
            {
                Debug.LogError($"[Manage3DGen] GLB file not found: {absolutePath}");
                return null;
            }

            // Validate file size - GLB files should be at least a few KB
            var fileInfo = new FileInfo(absolutePath);
            Debug.Log($"[Manage3DGen] GLB file size: {fileInfo.Length} bytes ({fileInfo.Length / 1024f:F1} KB)");
            
            if (fileInfo.Length < 100)
            {
                Debug.LogError($"[Manage3DGen] GLB file is too small ({fileInfo.Length} bytes), likely corrupted or incomplete: {absolutePath}");
                return null;
            }

            // Validate GLB magic number (first 4 bytes should be "glTF" = 0x46546C67)
            try
            {
                using (var fs = new FileStream(absolutePath, FileMode.Open, FileAccess.Read))
                {
                    byte[] magic = new byte[4];
                    fs.Read(magic, 0, 4);
                    uint magicNumber = BitConverter.ToUInt32(magic, 0);
                    
                    if (magicNumber != 0x46546C67) // "glTF" in little-endian
                    {
                        Debug.LogError($"[Manage3DGen] Invalid GLB file - magic number mismatch. Expected 'glTF', got bytes: {magic[0]:X2} {magic[1]:X2} {magic[2]:X2} {magic[3]:X2}");
                        return null;
                    }
                    
                    Debug.Log($"[Manage3DGen] GLB magic number validated: glTF");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Manage3DGen] Failed to validate GLB file: {e.Message}");
                return null;
            }

            Debug.Log($"[Manage3DGen] Loading GLB with glTFast: {absolutePath}");

            // Create a parent GameObject to hold the loaded model
            string modelName = Path.GetFileNameWithoutExtension(assetPath);
            GameObject container = new GameObject(modelName + "_glTFast");

            // Use glTFast for runtime loading with file:// prefix
            string fileUri = "file://" + absolutePath.Replace("\\", "/");
            
            // Start async loading - use fire-and-forget pattern with completion callback
            StartGltfLoadAsync(fileUri, container);
            
            // Return the container immediately - it will be populated by the async loader
            // The caller should check if the container has children to know if loading is complete
            return container;
        }

        // Static state for async glTFast loading  
        private static bool s_gltfLoadingInProgress = false;
        private static GameObject s_gltfLoadingContainer = null;
        private static string s_gltfLoadingError = null;

        private static async void StartGltfLoadAsync(string uri, GameObject container)
        {
            s_gltfLoadingInProgress = true;
            s_gltfLoadingContainer = container;
            s_gltfLoadingError = null;
            
            try
            {
                // Use a logger to capture glTFast errors
                var logger = new GLTFast.Logging.ConsoleLogger();
                var gltf = new GLTFast.GltfImport(logger: logger);
                
                Debug.Log($"[Manage3DGen] Starting glTFast.Load for: {uri}");
                bool success = await gltf.Load(uri);
                
                if (success && container != null)
                {
                    Debug.Log($"[Manage3DGen] glTFast.Load succeeded, instantiating scene...");
                    await gltf.InstantiateMainSceneAsync(container.transform);
                    Debug.Log($"[Manage3DGen] glTFast loading completed successfully for: {container.name}, children: {container.transform.childCount}");
                }
                else
                {
                    s_gltfLoadingError = $"glTFast.Load returned false for: {uri}";
                    Debug.LogError($"[Manage3DGen] {s_gltfLoadingError}");
                }
            }
            catch (Exception e)
            {
                s_gltfLoadingError = e.Message;
                Debug.LogError($"[Manage3DGen] glTFast exception: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                s_gltfLoadingInProgress = false;
                s_gltfLoadingContainer = null;
            }
        }

        /// <summary>
        /// Parses a JToken into a float array for Vector3 representation.
        /// Handles arrays like [x, y, z], strings like "[x, y, z]" or "x, y, z", and objects like {x: 0, y: 0, z: 0}
        /// </summary>
        private static float[] ParseVector3Array(JToken token)
        {
            if (token == null) return null;

            try
            {
                // Handle JArray: [x, y, z]
                if (token is JArray arr && arr.Count >= 3)
                {
                    return new float[]
                    {
                        arr[0].ToObject<float>(),
                        arr[1].ToObject<float>(),
                        arr[2].ToObject<float>()
                    };
                }

                // Handle JObject: {x: 0, y: 0, z: 0}
                if (token is JObject obj)
                {
                    if (obj.ContainsKey("x") && obj.ContainsKey("y") && obj.ContainsKey("z"))
                    {
                        return new float[]
                        {
                            obj["x"].ToObject<float>(),
                            obj["y"].ToObject<float>(),
                            obj["z"].ToObject<float>()
                        };
                    }
                }

                // Handle string: "[x, y, z]" or "x, y, z"
                if (token.Type == JTokenType.String)
                {
                    string str = token.ToString().Trim();
                    
                    // Remove brackets if present
                    if (str.StartsWith("[") && str.EndsWith("]"))
                    {
                        str = str.Substring(1, str.Length - 2);
                    }
                    
                    // Split by comma and parse
                    string[] parts = str.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        return new float[]
                        {
                            float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                        };
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Manage3DGen] Failed to parse Vector3 from token '{token}': {e.Message}");
            }

            return null;
        }

        #endregion

        #region Parameters Class for Tool Discovery

        public class Parameters
        {
            [ToolParameter("Action to perform: generate, transform, status, revert, revert_original, list_history", Required = false)]
            public string action { get; set; }

            [ToolParameter("Name or path of the source object to transform (for 'transform' action)")]
            public string source_object { get; set; }

            [ToolParameter("Name/prompt of the 3D model to generate or transform into")]
            public string target_name { get; set; }

            [ToolParameter("World position [x, y, z] for the generated object (for 'generate' action)", Required = false)]
            public float[] position { get; set; }

            [ToolParameter("Euler rotation [x, y, z] for the generated object (for 'generate' action)", Required = false)]
            public float[] rotation { get; set; }

            [ToolParameter("Scale [x, y, z] for the generated object (for 'generate' action)", Required = false)]
            public float[] scale { get; set; }

            [ToolParameter("Parent object name or path (for 'generate' action)", Required = false)]
            public string parent { get; set; }

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
