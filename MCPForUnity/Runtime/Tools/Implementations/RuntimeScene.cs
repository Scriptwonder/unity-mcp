using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Runtime.Tools.Implementations
{
    /// <summary>
    /// Runtime tool for scene queries: hierarchy, find objects, scene info.
    /// </summary>
    [RuntimeMcpTool("runtime_scene")]
    public static class RuntimeScene
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new RuntimeToolParams(@params);
            string action = p.Get("action", "get_info");

            return action switch
            {
                "get_info" => GetSceneInfo(),
                "get_hierarchy" => GetHierarchy(p),
                "find_by_name" => FindByName(p),
                "find_by_tag" => FindByTag(p),
                "find_by_component" => FindByComponent(p),
                "get_root_objects" => GetRootObjects(p),
                _ => new RuntimeErrorResponse($"Unknown action: {action}")
            };
        }

        private static object GetSceneInfo()
        {
            var activeScene = SceneManager.GetActiveScene();
            var scenes = new List<object>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new
                {
                    name = scene.name,
                    path = scene.path,
                    buildIndex = scene.buildIndex,
                    isLoaded = scene.isLoaded,
                    isDirty = scene.isDirty,
                    rootCount = scene.rootCount,
                    isActive = scene == activeScene
                });
            }

            return new RuntimeSuccessResponse("Scene info retrieved", new
            {
                activeSceneName = activeScene.name,
                sceneCount = SceneManager.sceneCount,
                scenes
            });
        }

        private static object GetHierarchy(RuntimeToolParams p)
        {
            int maxDepth = p.GetInt("max_depth") ?? 5;
            int pageSize = p.GetInt("page_size") ?? 50;
            int cursor = p.GetInt("cursor") ?? 0;
            string sceneName = p.Get("scene_name");

            Scene scene;
            if (!string.IsNullOrEmpty(sceneName))
            {
                scene = SceneManager.GetSceneByName(sceneName);
                if (!scene.IsValid())
                    return new RuntimeErrorResponse($"Scene '{sceneName}' not found or not loaded");
            }
            else
            {
                scene = SceneManager.GetActiveScene();
            }

            var allNodes = new List<object>();
            var rootObjects = scene.GetRootGameObjects();

            foreach (var root in rootObjects)
            {
                allNodes.AddRange(RuntimeGameObjectSerializer.GetHierarchy(root, maxDepth));
            }

            int total = allNodes.Count;
            var page = allNodes.Skip(cursor).Take(pageSize).ToList();
            int? nextCursor = (cursor + pageSize < total) ? cursor + pageSize : (int?)null;

            return new RuntimeSuccessResponse($"Retrieved {page.Count} nodes", new
            {
                sceneName = scene.name,
                totalNodes = total,
                pageSize,
                cursor,
                nextCursor,
                nodes = page
            });
        }

        private static object FindByName(RuntimeToolParams p)
        {
            string name = p.Get("name");
            if (string.IsNullOrEmpty(name))
                return new RuntimeErrorResponse("'name' parameter is required");

            bool exact = p.GetBool("exact", false);
            int limit = p.GetInt("limit") ?? 50;

            var results = new List<object>();
            var allObjects = Object.FindObjectsOfType<GameObject>(true);

            foreach (var go in allObjects)
            {
                if (results.Count >= limit) break;

                bool match = exact
                    ? go.name == name
                    : go.name.Contains(name);

                if (match)
                {
                    results.Add(RuntimeGameObjectSerializer.GetGameObjectSummary(go));
                }
            }

            return new RuntimeSuccessResponse($"Found {results.Count} objects", new
            {
                searchName = name,
                exact,
                count = results.Count,
                results
            });
        }

        private static object FindByTag(RuntimeToolParams p)
        {
            string tag = p.Get("tag");
            if (string.IsNullOrEmpty(tag))
                return new RuntimeErrorResponse("'tag' parameter is required");

            int limit = p.GetInt("limit") ?? 50;

            try
            {
                var objects = GameObject.FindGameObjectsWithTag(tag);
                var results = objects.Take(limit)
                    .Select(go => RuntimeGameObjectSerializer.GetGameObjectSummary(go))
                    .ToList();

                return new RuntimeSuccessResponse($"Found {results.Count} objects with tag '{tag}'", new
                {
                    tag,
                    count = results.Count,
                    totalFound = objects.Length,
                    results
                });
            }
            catch (UnityException)
            {
                return new RuntimeErrorResponse($"Tag '{tag}' is not defined");
            }
        }

        private static object FindByComponent(RuntimeToolParams p)
        {
            string componentType = p.Get("component_type");
            if (string.IsNullOrEmpty(componentType))
                return new RuntimeErrorResponse("'component_type' parameter is required");

            int limit = p.GetInt("limit") ?? 50;
            bool includeInactive = p.GetBool("include_inactive", true);

            // Try to find the type
            System.Type type = null;

            // Try common Unity namespaces
            string[] namespaces = {
                "UnityEngine.",
                "UnityEngine.UI.",
                "TMPro.",
                ""
            };

            foreach (var ns in namespaces)
            {
                type = System.Type.GetType($"{ns}{componentType}, UnityEngine");
                if (type != null) break;
                type = System.Type.GetType($"{ns}{componentType}, UnityEngine.UI");
                if (type != null) break;
                type = System.Type.GetType($"{ns}{componentType}");
                if (type != null) break;
            }

            // Try searching all assemblies
            if (type == null)
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(componentType);
                    if (type != null) break;

                    foreach (var ns in namespaces)
                    {
                        type = assembly.GetType($"{ns}{componentType}");
                        if (type != null) break;
                    }
                    if (type != null) break;
                }
            }

            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                return new RuntimeErrorResponse($"Component type '{componentType}' not found or is not a Component");
            }

            var components = Object.FindObjectsOfType(type, includeInactive) as Component[];
            if (components == null)
                return new RuntimeErrorResponse($"Failed to find components of type '{componentType}'");

            var results = components.Take(limit)
                .Select(c => new
                {
                    gameObject = RuntimeGameObjectSerializer.GetGameObjectSummary(c.gameObject),
                    component = RuntimeGameObjectSerializer.SerializeComponentBasic(c)
                })
                .ToList();

            return new RuntimeSuccessResponse($"Found {results.Count} {componentType} components", new
            {
                componentType = type.FullName,
                count = results.Count,
                totalFound = components.Length,
                results
            });
        }

        private static object GetRootObjects(RuntimeToolParams p)
        {
            string sceneName = p.Get("scene_name");

            Scene scene;
            if (!string.IsNullOrEmpty(sceneName))
            {
                scene = SceneManager.GetSceneByName(sceneName);
                if (!scene.IsValid())
                    return new RuntimeErrorResponse($"Scene '{sceneName}' not found or not loaded");
            }
            else
            {
                scene = SceneManager.GetActiveScene();
            }

            var rootObjects = scene.GetRootGameObjects();
            var results = rootObjects.Select(go => RuntimeGameObjectSerializer.GetGameObjectSummary(go)).ToList();

            return new RuntimeSuccessResponse($"Found {results.Count} root objects", new
            {
                sceneName = scene.name,
                count = results.Count,
                rootObjects = results
            });
        }
    }
}
