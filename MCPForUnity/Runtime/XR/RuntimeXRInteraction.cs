using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Runtime.Helpers;
using MCPForUnity.Runtime.Tools;
using Newtonsoft.Json.Linq;
using UnityEngine;

#if UNITY_XR_INTERACTION_TOOLKIT
using UnityEngine.XR.Interaction.Toolkit;
#endif

namespace MCPForUnity.Runtime.XR
{
    /// <summary>
    /// Runtime tool for XR interaction setup: ray, grab, poke interactors.
    /// Requires XR Interaction Toolkit package.
    /// </summary>
    [RuntimeMcpTool("runtime_xr_interaction")]
    public static class RuntimeXRInteraction
    {
        public static object HandleCommand(JObject @params)
        {
#if !UNITY_XR_INTERACTION_TOOLKIT
            return new RuntimeErrorResponse("XR Interaction Toolkit not installed. Install com.unity.xr.interaction.toolkit");
#else
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "get_info");

            return action switch
            {
                "add_ray_interactor" => AddRayInteractor(p),
                "add_direct_interactor" => AddDirectInteractor(p),
                "add_poke_interactor" => AddPokeInteractor(p),
                "add_interactable" => AddInteractable(p),
                "remove_interactor" => RemoveInteractor(p),
                "configure_interactor" => ConfigureInteractor(p),
                "get_info" => GetInteractionInfo(p),
                "setup_interaction_manager" => SetupInteractionManager(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
#endif
        }

#if UNITY_XR_INTERACTION_TOOLKIT
        private static object AddRayInteractor(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required (controller GameObject name or instance ID)");

            var go = Tools.Implementations.RuntimeGameObject.FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            // Ensure interaction manager exists
            EnsureInteractionManager();

            // Check if already has ray interactor
            var existingRay = go.GetComponent<XRRayInteractor>();
            if (existingRay != null && !p.GetBool("force", false))
                return new RuntimeErrorResponse($"'{go.name}' already has XRRayInteractor. Use force=true to replace.");

            if (existingRay != null)
                Object.Destroy(existingRay);

            // Add ray interactor
            var rayInteractor = go.AddComponent<XRRayInteractor>();

            // Configure ray
            float rayLength = p.GetFloat("ray_length") ?? 10f;
            rayInteractor.maxRaycastDistance = rayLength;

            // Add line renderer for visual ray
            if (p.GetBool("add_line_visual", true))
            {
                var existingLine = go.GetComponent<XRInteractorLineVisual>();
                if (existingLine == null)
                {
                    var lineVisual = go.AddComponent<XRInteractorLineVisual>();
                    lineVisual.lineWidth = p.GetFloat("line_width") ?? 0.02f;
                }
            }

            // Add XR Controller if not present
            EnsureXRController(go);

            return new RuntimeSuccessResponse($"Added ray interactor to '{go.name}'", new
            {
                gameObjectName = go.name,
                instanceID = go.GetInstanceID(),
                interactorType = "XRRayInteractor",
                maxRaycastDistance = rayInteractor.maxRaycastDistance,
                hasLineVisual = go.GetComponent<XRInteractorLineVisual>() != null
            });
        }

        private static object AddDirectInteractor(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var go = Tools.Implementations.RuntimeGameObject.FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            EnsureInteractionManager();

            var existing = go.GetComponent<XRDirectInteractor>();
            if (existing != null && !p.GetBool("force", false))
                return new RuntimeErrorResponse($"'{go.name}' already has XRDirectInteractor");

            if (existing != null)
                Object.Destroy(existing);

            var directInteractor = go.AddComponent<XRDirectInteractor>();

            // Add trigger collider if needed for direct grab detection
            if (p.GetBool("add_collider", true))
            {
                var existingCollider = go.GetComponent<Collider>();
                if (existingCollider == null)
                {
                    var sphereCollider = go.AddComponent<SphereCollider>();
                    sphereCollider.isTrigger = true;
                    sphereCollider.radius = p.GetFloat("collider_radius") ?? 0.1f;
                }
            }

            EnsureXRController(go);

            return new RuntimeSuccessResponse($"Added direct interactor to '{go.name}'", new
            {
                gameObjectName = go.name,
                instanceID = go.GetInstanceID(),
                interactorType = "XRDirectInteractor"
            });
        }

        private static object AddPokeInteractor(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var go = Tools.Implementations.RuntimeGameObject.FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            EnsureInteractionManager();

            var existing = go.GetComponent<XRPokeInteractor>();
            if (existing != null && !p.GetBool("force", false))
                return new RuntimeErrorResponse($"'{go.name}' already has XRPokeInteractor");

            if (existing != null)
                Object.Destroy(existing);

            var pokeInteractor = go.AddComponent<XRPokeInteractor>();

            // Configure poke depth
            float pokeDepth = p.GetFloat("poke_depth") ?? 0.1f;
            pokeInteractor.pokeDepth = pokeDepth;

            EnsureXRController(go);

            return new RuntimeSuccessResponse($"Added poke interactor to '{go.name}'", new
            {
                gameObjectName = go.name,
                instanceID = go.GetInstanceID(),
                interactorType = "XRPokeInteractor",
                pokeDepth
            });
        }

        private static object AddInteractable(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var go = Tools.Implementations.RuntimeGameObject.FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            string interactableType = p.Get("type", "grab");

            // Ensure has collider for interaction
            if (go.GetComponent<Collider>() == null)
            {
                if (p.GetBool("add_collider", true))
                {
                    var boxCollider = go.AddComponent<BoxCollider>();
                    // Try to fit to mesh bounds
                    var meshFilter = go.GetComponent<MeshFilter>();
                    if (meshFilter != null && meshFilter.sharedMesh != null)
                    {
                        boxCollider.center = meshFilter.sharedMesh.bounds.center;
                        boxCollider.size = meshFilter.sharedMesh.bounds.size;
                    }
                }
            }

            Component interactable;
            string addedType;

            switch (interactableType.ToLower())
            {
                case "grab":
                    var grabInteractable = go.AddComponent<XRGrabInteractable>();
                    grabInteractable.throwOnDetach = p.GetBool("throw_on_detach", true);

                    // Add rigidbody if not present
                    if (go.GetComponent<Rigidbody>() == null && p.GetBool("add_rigidbody", true))
                    {
                        var rb = go.AddComponent<Rigidbody>();
                        rb.useGravity = p.GetBool("use_gravity", true);
                    }

                    interactable = grabInteractable;
                    addedType = "XRGrabInteractable";
                    break;

                case "simple":
                    interactable = go.AddComponent<XRSimpleInteractable>();
                    addedType = "XRSimpleInteractable";
                    break;

                default:
                    return new RuntimeErrorResponse($"Unknown interactable type: '{interactableType}'. Use: grab, simple");
            }

            return new RuntimeSuccessResponse($"Added {addedType} to '{go.name}'", new
            {
                gameObjectName = go.name,
                instanceID = go.GetInstanceID(),
                interactableType = addedType,
                hasCollider = go.GetComponent<Collider>() != null,
                hasRigidbody = go.GetComponent<Rigidbody>() != null
            });
        }

        private static object RemoveInteractor(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var go = Tools.Implementations.RuntimeGameObject.FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            var interactors = go.GetComponents<XRBaseInteractor>();
            if (interactors.Length == 0)
                return new RuntimeErrorResponse($"'{go.name}' has no interactors");

            var removed = new List<string>();
            foreach (var interactor in interactors)
            {
                removed.Add(interactor.GetType().Name);
                Object.Destroy(interactor);
            }

            // Also remove line visual if present
            var lineVisual = go.GetComponent<XRInteractorLineVisual>();
            if (lineVisual != null)
            {
                Object.Destroy(lineVisual);
                removed.Add("XRInteractorLineVisual");
            }

            return new RuntimeSuccessResponse($"Removed interactors from '{go.name}'", new
            {
                gameObjectName = go.name,
                removedComponents = removed
            });
        }

        private static object ConfigureInteractor(RuntimeToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrEmpty(target))
                return new RuntimeErrorResponse("'target' parameter is required");

            var go = Tools.Implementations.RuntimeGameObject.FindGameObject(target);
            if (go == null)
                return new RuntimeErrorResponse($"GameObject '{target}' not found");

            var rayInteractor = go.GetComponent<XRRayInteractor>();
            if (rayInteractor != null)
            {
                float? rayLength = p.GetFloat("ray_length");
                if (rayLength.HasValue)
                    rayInteractor.maxRaycastDistance = rayLength.Value;

                return new RuntimeSuccessResponse($"Configured ray interactor on '{go.name}'", new
                {
                    maxRaycastDistance = rayInteractor.maxRaycastDistance
                });
            }

            var pokeInteractor = go.GetComponent<XRPokeInteractor>();
            if (pokeInteractor != null)
            {
                float? pokeDepth = p.GetFloat("poke_depth");
                if (pokeDepth.HasValue)
                    pokeInteractor.pokeDepth = pokeDepth.Value;

                return new RuntimeSuccessResponse($"Configured poke interactor on '{go.name}'", new
                {
                    pokeDepth = pokeInteractor.pokeDepth
                });
            }

            return new RuntimeErrorResponse($"'{go.name}' has no configurable interactor");
        }

        private static object GetInteractionInfo(RuntimeToolParams p)
        {
            var interactionManager = Object.FindObjectOfType<XRInteractionManager>();
            var interactors = Object.FindObjectsOfType<XRBaseInteractor>(true);
            var interactables = Object.FindObjectsOfType<XRBaseInteractable>(true);

            var interactorInfo = interactors.Select(i => new
            {
                gameObjectName = i.gameObject.name,
                instanceID = i.gameObject.GetInstanceID(),
                type = i.GetType().Name,
                enabled = i.enabled
            }).ToList();

            var interactableInfo = interactables.Select(i => new
            {
                gameObjectName = i.gameObject.name,
                instanceID = i.gameObject.GetInstanceID(),
                type = i.GetType().Name,
                enabled = i.enabled
            }).ToList();

            return new RuntimeSuccessResponse("XR Interaction info retrieved", new
            {
                hasInteractionManager = interactionManager != null,
                interactorCount = interactors.Length,
                interactableCount = interactables.Length,
                interactors = interactorInfo,
                interactables = interactableInfo
            });
        }

        private static object SetupInteractionManager(RuntimeToolParams p)
        {
            var existing = Object.FindObjectOfType<XRInteractionManager>();
            if (existing != null && !p.GetBool("force", false))
            {
                return new RuntimeSuccessResponse("XR Interaction Manager already exists", new
                {
                    gameObjectName = existing.gameObject.name,
                    instanceID = existing.gameObject.GetInstanceID()
                });
            }

            if (existing != null)
                Object.Destroy(existing.gameObject);

            var managerGo = new GameObject("XR Interaction Manager");
            var manager = managerGo.AddComponent<XRInteractionManager>();

            return new RuntimeSuccessResponse("Created XR Interaction Manager", new
            {
                gameObjectName = managerGo.name,
                instanceID = managerGo.GetInstanceID()
            });
        }

        private static void EnsureInteractionManager()
        {
            if (Object.FindObjectOfType<XRInteractionManager>() == null)
            {
                var managerGo = new GameObject("XR Interaction Manager");
                managerGo.AddComponent<XRInteractionManager>();
            }
        }

        private static void EnsureXRController(GameObject go)
        {
            if (go.GetComponent<XRBaseController>() == null)
            {
                go.AddComponent<ActionBasedController>();
            }
        }
#endif
    }
}
