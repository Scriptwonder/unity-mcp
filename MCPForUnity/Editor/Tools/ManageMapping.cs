using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Mapping;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("manage_mapping", AutoRegister = false)]
    public static class ManageMapping
    {
        private const string DefaultCondition = "Unity Development";

        private sealed class MappingCommand
        {
            public string action { get; set; } = string.Empty;
            public string condition { get; set; } = string.Empty;
            public float? minConfidence { get; set; }
            public bool? requireEvidence { get; set; }
            public bool? includeInactive { get; set; }
            public bool? includeComponentsSummary { get; set; }
            public bool? includeTransformSummary { get; set; }
            public bool? includePublicFields { get; set; }
            public bool? includePrivateSerializeField { get; set; }
            public bool? includeCollections { get; set; }
            public int? maxRefItemsPerField { get; set; }
            public int? limit { get; set; }
            public int? offset { get; set; }
            public List<string> objectIds { get; set; }
            public JArray rows { get; set; }
            public string assetPath { get; set; }
            public string jsonPath { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            var cmd = ToMappingCommand(@params);
            if (string.IsNullOrEmpty(cmd.action))
            {
                return new ErrorResponse("Action parameter is required.");
            }

            switch (cmd.action)
            {
                case "get_scene_object_index":
                    return GetSceneObjectIndex(cmd);
                case "get_object_evidence":
                    return GetObjectEvidence(cmd);
                case "get_reference_edges":
                    return GetReferenceEdges(cmd);
                case "validate_mapping_rows":
                    return ValidateMappingRows(cmd);
                case "commit_mapping_table":
                    return CommitMappingTable(cmd);
                default:
                    return new ErrorResponse($"Unknown action: '{cmd.action}'. Valid actions: get_scene_object_index, get_object_evidence, get_reference_edges, validate_mapping_rows, commit_mapping_table.");
            }
        }

        private static MappingCommand ToMappingCommand(JObject p)
        {
            if (p == null) return new MappingCommand();
            int? BI(JToken t)
            {
                if (t == null || t.Type == JTokenType.Null) return null;
                var s = t.ToString().Trim();
                if (s.Length == 0) return null;
                if (int.TryParse(s, out var i)) return i;
                if (double.TryParse(s, out var d)) return (int)d;
                return t.Type == JTokenType.Integer ? t.Value<int>() : (int?)null;
            }
            float? BF(JToken t)
            {
                if (t == null || t.Type == JTokenType.Null) return null;
                var s = t.ToString().Trim();
                if (s.Length == 0) return null;
                if (float.TryParse(s, out var f)) return f;
                return t.Type == JTokenType.Float ? t.Value<float>() : (float?)null;
            }
            bool? BB(JToken t)
            {
                if (t == null || t.Type == JTokenType.Null) return null;
                try
                {
                    if (t.Type == JTokenType.Boolean) return t.Value<bool>();
                    var s = t.ToString().Trim();
                    if (s.Length == 0) return null;
                    if (bool.TryParse(s, out var b)) return b;
                    if (s == "1") return true;
                    if (s == "0") return false;
                }
                catch { }
                return null;
            }

            var cmd = new MappingCommand
            {
                action = (p["action"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant(),
                condition = (p["condition"]?.ToString() ?? string.Empty).Trim(),
                minConfidence = BF(p["minConfidence"] ?? p["min_confidence"]),
                requireEvidence = BB(p["requireEvidence"] ?? p["require_evidence"]),
                includeInactive = BB(p["includeInactive"] ?? p["include_inactive"]),
                includeComponentsSummary = BB(p["includeComponentsSummary"] ?? p["include_components_summary"]),
                includeTransformSummary = BB(p["includeTransformSummary"] ?? p["include_transform_summary"]),
                includePublicFields = BB(p["includePublicFields"] ?? p["include_public_fields"]),
                includePrivateSerializeField = BB(p["includePrivateSerializeField"] ?? p["include_private_serialize_field"]),
                includeCollections = BB(p["includeCollections"] ?? p["include_collections"]),
                maxRefItemsPerField = BI(p["maxRefItemsPerField"] ?? p["max_ref_items_per_field"]),
                limit = BI(p["limit"]),
                offset = BI(p["offset"]),
                objectIds = ParseObjectIds(p["objectIds"] ?? p["object_ids"]),
                rows = p?["rows"] as JArray,
                assetPath = (p?["assetPath"] ?? p?["asset_path"])?.ToString(),
                jsonPath = (p?["jsonPath"] ?? p?["json_path"])?.ToString(),
            };
            if (string.IsNullOrEmpty(cmd.condition))
            {
                cmd.condition = DefaultCondition;
            }
            return cmd;
        }

        private static object GetSceneObjectIndex(MappingCommand cmd)
        {
            try
            {
                Scene activeScene = SceneManager.GetActiveScene();
                if (!activeScene.IsValid() || !activeScene.isLoaded)
                {
                    return new ErrorResponse("No valid and loaded scene is active to index.");
                }

                bool includeInactive = cmd.includeInactive ?? false;
                bool includeComponentsSummary = cmd.includeComponentsSummary ?? false;
                bool includeTransformSummary = cmd.includeTransformSummary ?? false;
                int resolvedLimit = Mathf.Clamp(cmd.limit ?? 500, 1, 5000);
                int resolvedOffset = Mathf.Max(0, cmd.offset ?? 0);

                var allObjects = GetAllSceneObjects(activeScene, includeInactive);
                var ordered = allObjects
                    .Select(go => new GameObjectEntry { Go = go, Path = GetGameObjectPath(go) })
                    .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int total = ordered.Count;
                if (resolvedOffset > total) resolvedOffset = total;
                int end = Mathf.Min(total, resolvedOffset + resolvedLimit);

                var objects = new List<object>(Mathf.Max(0, end - resolvedOffset));
                var objectIds = new HashSet<string>();

                for (int i = resolvedOffset; i < end; i++)
                {
                    var go = ordered[i].Go;
                    if (go == null) continue;
                    var objectRef = BuildObjectRef(go, ordered[i].Path);
                    objectIds.Add(objectRef.globalId);

                    var data = new Dictionary<string, object>
                    {
                        { "object", objectRef },
                        { "activeSelf", go.activeSelf },
                        { "activeInHierarchy", go.activeInHierarchy },
                        { "tag", go.tag },
                        { "layer", go.layer },
                        { "isStatic", go.isStatic },
                    };

                    if (includeComponentsSummary)
                    {
                        data["componentTypeNames"] = GetComponentTypeNames(go).OrderBy(n => n).ToArray();
                    }

                    if (includeTransformSummary)
                    {
                        data["transform"] = BuildTransformSummary(go.transform);
                    }

                    objects.Add(data);
                }

                var predicates = BuildPredicates(ordered, objectIds, includeComponentsSummary);

                string nextOffset = end < total ? end.ToString() : null;
                var payload = new
                {
                    condition = cmd.condition,
                    offset = resolvedOffset,
                    limit = resolvedLimit,
                    next_offset = nextOffset,
                    total = total,
                    objects = objects,
                    predicates = predicates,
                };

                return new SuccessResponse($"Indexed {objects.Count} scene objects.", payload);
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error building scene object index: {e.Message}");
            }
        }

        private static object GetObjectEvidence(MappingCommand cmd)
        {
            try
            {
                if (cmd.objectIds == null || cmd.objectIds.Count == 0)
                {
                    return new ErrorResponse("objectIds is required for get_object_evidence.");
                }

                Scene activeScene = SceneManager.GetActiveScene();
                if (!activeScene.IsValid() || !activeScene.isLoaded)
                {
                    return new ErrorResponse("No valid and loaded scene is active to fetch evidence from.");
                }

                bool includeTransformSummary = cmd.includeTransformSummary ?? true;
                var results = new List<object>();

                foreach (var objectId in cmd.objectIds)
                {
                    var go = ResolveObjectId(objectId);
                    if (go == null)
                    {
                        results.Add(new
                        {
                            objectId = objectId,
                            error = "Object not found."
                        });
                        continue;
                    }

                    var evidence = new List<object>();

                    foreach (var component in go.GetComponents<Component>())
                    {
                        if (component == null)
                        {
                            evidence.Add(new EvidenceItem
                            {
                                type = EvidenceType.ComponentType,
                                detail = "MissingScript",
                                sourceObjectGlobalId = GetGlobalId(go),
                                sourceComponentType = "MissingScript"
                            });
                            continue;
                        }

                        var type = component.GetType();
                        evidence.Add(new EvidenceItem
                        {
                            type = EvidenceType.ComponentType,
                            detail = type.FullName ?? type.Name,
                            sourceObjectGlobalId = GetGlobalId(go),
                            sourceComponentType = type.Name
                        });
                    }

                    evidence.Add(new EvidenceItem
                    {
                        type = EvidenceType.TagLayer,
                        detail = $"tag:{go.tag};layer:{go.layer}",
                        sourceObjectGlobalId = GetGlobalId(go)
                    });

                    if (includeTransformSummary && go.transform != null)
                    {
                        var t = go.transform;
                        evidence.Add(new EvidenceItem
                        {
                            type = EvidenceType.Transform,
                            detail = $"pos:{t.localPosition};rot:{t.localEulerAngles};scale:{t.localScale}",
                            sourceObjectGlobalId = GetGlobalId(go)
                        });
                    }

                    var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (prefabSource != null)
                    {
                        string prefabPath = AssetDatabase.GetAssetPath(prefabSource);
                        if (!string.IsNullOrEmpty(prefabPath))
                        {
                            evidence.Add(new EvidenceItem
                            {
                                type = EvidenceType.PrefabLink,
                                detail = prefabPath,
                                sourceObjectGlobalId = GetGlobalId(go)
                            });
                        }
                    }

                    results.Add(new
                    {
                        @object = BuildObjectRef(go, GetGameObjectPath(go)),
                        evidence = evidence
                    });
                }

                return new SuccessResponse($"Retrieved evidence for {results.Count} objects.", new
                {
                    condition = cmd.condition,
                    items = results
                });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error fetching object evidence: {e.Message}");
            }
        }

        private static object GetReferenceEdges(MappingCommand cmd)
        {
            try
            {
                Scene activeScene = SceneManager.GetActiveScene();
                if (!activeScene.IsValid() || !activeScene.isLoaded)
                {
                    return new ErrorResponse("No valid and loaded scene is active to inspect references.");
                }

                bool includePublic = cmd.includePublicFields ?? true;
                bool includePrivateSerialized = cmd.includePrivateSerializeField ?? true;
                bool includeCollections = cmd.includeCollections ?? true;
                int maxItems = Mathf.Clamp(cmd.maxRefItemsPerField ?? 50, 1, 500);
                int resolvedLimit = Mathf.Clamp(cmd.limit ?? 200, 1, 2000);
                int resolvedOffset = Mathf.Max(0, cmd.offset ?? 0);

                var behaviours = GetAllSceneBehaviours(activeScene);
                int total = behaviours.Count;
                if (resolvedOffset > total) resolvedOffset = total;
                int end = Mathf.Min(total, resolvedOffset + resolvedLimit);

                var scripts = new List<object>(Mathf.Max(0, end - resolvedOffset));
                var refs = new List<object>();

                for (int i = resolvedOffset; i < end; i++)
                {
                    var component = behaviours[i];
                    if (component == null) continue;

                    var host = component.gameObject;
                    string hostId = GetGlobalId(host);
                    var type = component.GetType();
                    var schema = new List<object>();

                    foreach (var field in GetSerializableFields(type, includePublic, includePrivateSerialized))
                    {
                        schema.Add(new
                        {
                            fieldName = field.Name,
                            fieldType = field.FieldType.FullName ?? field.FieldType.Name,
                            attributesSummary = GetAttributeSummary(field)
                        });

                        ExtractReferenceEdges(component, field, hostId, includeCollections, maxItems, refs);
                    }

                    scripts.Add(new
                    {
                        hostObjectId = hostId,
                        componentType = type.FullName ?? type.Name,
                        serializedFieldSchema = schema
                    });
                }

                string nextOffset = end < total ? end.ToString() : null;
                var payload = new
                {
                    condition = cmd.condition,
                    offset = resolvedOffset,
                    limit = resolvedLimit,
                    next_offset = nextOffset,
                    total = total,
                    scripts = scripts,
                    refs = refs
                };

                return new SuccessResponse($"Retrieved script references for {scripts.Count} components.", payload);
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error extracting reference edges: {e.Message}");
            }
        }

        private static object ValidateMappingRows(MappingCommand cmd)
        {
            if (cmd.rows == null)
            {
                return new ErrorResponse("rows is required for validate_mapping_rows.");
            }

            float minConfidence = Mathf.Clamp01(cmd.minConfidence ?? 0f);
            bool requireEvidence = cmd.requireEvidence ?? false;

            var validRows = new List<MappingRow>();
            var issues = new List<object>();
            var seen = new HashSet<string>();

            foreach (var token in cmd.rows)
            {
                if (token is not JObject rowObj)
                {
                    issues.Add(new { type = "row_invalid", detail = "Row is not an object." });
                    continue;
                }

                var row = ParseMappingRow(rowObj, issues);
                if (row == null)
                {
                    continue;
                }

                if (row.confidence < minConfidence)
                {
                    issues.Add(new { type = "low_confidence", detail = row.confidence, row = rowObj });
                    continue;
                }

                if (requireEvidence && (row.evidence == null || row.evidence.Count == 0))
                {
                    issues.Add(new { type = "missing_evidence", detail = "Evidence required.", row = rowObj });
                    continue;
                }

                string key = $"{row.subject?.globalId}|{row.predicate}|{row.@object?.globalId}";
                if (seen.Contains(key))
                {
                    issues.Add(new { type = "duplicate_row", detail = key, row = rowObj });
                    continue;
                }
                seen.Add(key);

                validRows.Add(row);
            }

            return new SuccessResponse("Validated mapping rows.", new
            {
                condition = cmd.condition,
                validRows = validRows,
                issues = issues,
                stats = new
                {
                    inputCount = cmd.rows.Count,
                    validCount = validRows.Count,
                    issueCount = issues.Count
                }
            });
        }

        private static object CommitMappingTable(MappingCommand cmd)
        {
            if (cmd.rows == null)
            {
                return new ErrorResponse("rows is required for commit_mapping_table.");
            }

            string assetPath = string.IsNullOrEmpty(cmd.assetPath)
                ? "Assets/MCP/Generated/StructureMappingTable.asset"
                : cmd.assetPath.Replace('\\', '/');
            string jsonPath = string.IsNullOrEmpty(cmd.jsonPath)
                ? Path.ChangeExtension(assetPath, ".json")
                : cmd.jsonPath.Replace('\\', '/');

            EnsureFolder(Path.GetDirectoryName(assetPath));

            var rows = new List<MappingRow>();
            var issues = new List<object>();
            foreach (var token in cmd.rows)
            {
                if (token is not JObject rowObj)
                {
                    issues.Add(new { type = "row_invalid", detail = "Row is not an object." });
                    continue;
                }

                var row = ParseMappingRow(rowObj, issues);
                if (row != null)
                {
                    row.condition = string.IsNullOrEmpty(row.condition) ? cmd.condition : row.condition;
                    rows.Add(row);
                }
            }

            var table = ScriptableObject.CreateInstance<StructureMappingTable>();
            table.rows = rows;
            table.StampMetadata();

            var existing = AssetDatabase.LoadAssetAtPath<StructureMappingTable>(assetPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(table, existing);
                AssetDatabase.SaveAssets();
            }
            else
            {
                AssetDatabase.CreateAsset(table, assetPath);
                AssetDatabase.SaveAssets();
            }

            var export = new StructureMappingExport
            {
                generatedAt = table.generatedAt,
                unityVersion = table.unityVersion,
                rows = rows
            };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(export, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(jsonPath, json, new System.Text.UTF8Encoding(false));
            AssetDatabase.Refresh();

            return new SuccessResponse("Committed mapping table.", new
            {
                assetPath = assetPath,
                jsonPath = jsonPath,
                rowCount = rows.Count,
                issues = issues
            });
        }

        private static List<object> BuildPredicates(
            List<GameObjectEntry> orderedObjects,
            HashSet<string> allowedIds,
            bool includeComponentsSummary)
        {
            var predicates = new List<object>();
            foreach (var entry in orderedObjects)
            {
                var go = entry.Go;
                if (go == null) continue;
                var childGlobalId = GetGlobalId(go);
                if (!allowedIds.Contains(childGlobalId)) continue;

                var parent = go.transform != null ? go.transform.parent : null;
                if (parent != null)
                {
                    string parentId = GetGlobalId(parent.gameObject);
                    if (allowedIds.Contains(parentId))
                    {
                        predicates.Add(new
                        {
                            predicate = "part_of",
                            subjectId = parentId,
                            objectId = childGlobalId,
                        });
                    }
                }

                var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(go);
                if (prefabSource != null)
                {
                    string prefabPath = AssetDatabase.GetAssetPath(prefabSource);
                    if (!string.IsNullOrEmpty(prefabPath))
                    {
                        predicates.Add(new
                        {
                            predicate = "prefab_instance_of",
                            subjectId = childGlobalId,
                            prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath),
                            prefabPath = prefabPath,
                        });
                    }
                }

                if (includeComponentsSummary)
                {
                    foreach (var componentName in GetComponentTypeNames(go))
                    {
                        predicates.Add(new
                        {
                            predicate = "has_component",
                            subjectId = childGlobalId,
                            componentType = componentName,
                        });
                    }
                }
            }

            return predicates;
        }

        private static ObjectRef BuildObjectRef(GameObject go, string path)
        {
            var objectRef = new ObjectRef
            {
                globalId = GetGlobalId(go),
                name = go.name,
                hierarchyPath = path,
            };

            var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (prefabSource != null)
            {
                string prefabPath = AssetDatabase.GetAssetPath(prefabSource);
                if (!string.IsNullOrEmpty(prefabPath))
                {
                    objectRef.prefabPath = prefabPath;
                    objectRef.prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                }
            }

            return objectRef;
        }

        private static string GetGlobalId(GameObject go)
        {
            return GetGlobalId((UnityEngine.Object)go);
        }

        private static object BuildTransformSummary(Transform t)
        {
            if (t == null) return null;
            return new
            {
                position = new[] { t.localPosition.x, t.localPosition.y, t.localPosition.z },
                rotation = new[] { t.localEulerAngles.x, t.localEulerAngles.y, t.localEulerAngles.z },
                scale = new[] { t.localScale.x, t.localScale.y, t.localScale.z },
            };
        }

        private static List<string> ParseObjectIds(JToken token)
        {
            var list = new List<string>();
            if (token == null || token.Type == JTokenType.Null)
            {
                return list;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token)
                {
                    if (item == null || item.Type == JTokenType.Null) continue;
                    var value = item.ToString().Trim();
                    if (!string.IsNullOrEmpty(value)) list.Add(value);
                }
                return list;
            }

            string s = token.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                list.Add(s);
            }

            return list;
        }

        private static GameObject ResolveObjectId(string objectId)
        {
            if (string.IsNullOrEmpty(objectId)) return null;
            try
            {
                if (GlobalObjectId.TryParse(objectId, out var globalId))
                {
                    var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
                    if (obj is GameObject go) return go;
                    if (obj is Component component) return component.gameObject;
                }
            }
            catch { }

            if (int.TryParse(objectId, out var instanceId))
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is GameObject go) return go;
                if (obj is Component component) return component.gameObject;
            }

            return null;
        }

        private static MappingRow ParseMappingRow(JObject rowObj, List<object> issues)
        {
            try
            {
                var subject = ParseObjectRef(rowObj["subject"] as JObject);
                var obj = ParseObjectRef(rowObj["object"] as JObject);
                string predicateRaw = rowObj["predicate"]?.ToString();

                if (subject == null || obj == null || string.IsNullOrEmpty(predicateRaw))
                {
                    issues.Add(new { type = "missing_fields", row = rowObj });
                    return null;
                }

                if (!Enum.TryParse(predicateRaw, ignoreCase: true, out RelationType predicate))
                {
                    issues.Add(new { type = "invalid_predicate", detail = predicateRaw, row = rowObj });
                    return null;
                }

                float confidence = rowObj["confidence"]?.Value<float>() ?? 0f;
                var evidence = ParseEvidence(rowObj["evidence"] as JArray);

                return new MappingRow
                {
                    subject = subject,
                    @object = obj,
                    predicate = predicate,
                    confidence = Mathf.Clamp01(confidence),
                    evidence = evidence,
                    subsystem = rowObj["subsystem"]?.ToString(),
                    condition = rowObj["condition"]?.ToString()
                };
            }
            catch (Exception ex)
            {
                issues.Add(new { type = "row_parse_error", detail = ex.Message, row = rowObj });
                return null;
            }
        }

        private static ObjectRef ParseObjectRef(JObject obj)
        {
            if (obj == null) return null;
            return new ObjectRef
            {
                globalId = obj["globalId"]?.ToString(),
                name = obj["name"]?.ToString(),
                hierarchyPath = obj["hierarchyPath"]?.ToString(),
                prefabGuid = obj["prefabGuid"]?.ToString(),
                prefabPath = obj["prefabPath"]?.ToString(),
            };
        }

        private static List<EvidenceItem> ParseEvidence(JArray items)
        {
            var result = new List<EvidenceItem>();
            if (items == null) return result;

            foreach (var item in items.OfType<JObject>())
            {
                var typeRaw = item["type"]?.ToString();
                EvidenceType type = EvidenceType.Other;
                if (!string.IsNullOrEmpty(typeRaw))
                {
                    Enum.TryParse(typeRaw, ignoreCase: true, out type);
                }

                result.Add(new EvidenceItem
                {
                    type = type,
                    detail = item["detail"]?.ToString(),
                    sourceObjectGlobalId = item["sourceObjectGlobalId"]?.ToString(),
                    sourceComponentType = item["sourceComponentType"]?.ToString(),
                    fieldName = item["fieldName"]?.ToString(),
                });
            }

            return result;
        }

        private static List<MonoBehaviour> GetAllSceneBehaviours(Scene activeScene)
        {
            var results = new List<MonoBehaviour>();
            var stack = new Stack<GameObject>();
            foreach (var root in activeScene.GetRootGameObjects())
            {
                if (root != null) stack.Push(root);
            }

            while (stack.Count > 0)
            {
                var go = stack.Pop();
                if (go == null) continue;
                results.AddRange(go.GetComponents<MonoBehaviour>());

                if (go.transform == null) continue;
                foreach (Transform child in go.transform)
                {
                    if (child != null && child.gameObject != null)
                    {
                        stack.Push(child.gameObject);
                    }
                }
            }

            return results;
        }

        private static void EnsureFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                return;
            }

            folderPath = folderPath.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static IEnumerable<FieldInfo> GetSerializableFields(Type type, bool includePublic, bool includePrivateSerialized)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in type.GetFields(flags))
            {
                if (field.IsStatic) continue;
                if (field.IsPublic && includePublic)
                {
                    if (field.IsDefined(typeof(NonSerializedAttribute), inherit: true)) continue;
                    yield return field;
                    continue;
                }

                if (!field.IsPublic && includePrivateSerialized)
                {
                    if (field.IsDefined(typeof(SerializeField), inherit: true))
                    {
                        yield return field;
                    }
                }
            }
        }

        private static string GetAttributeSummary(FieldInfo field)
        {
            var attributes = field.GetCustomAttributes(false);
            if (attributes == null || attributes.Length == 0) return string.Empty;
            var names = attributes.Select(a => a.GetType().Name);
            return string.Join(",", names);
        }

        private static void ExtractReferenceEdges(
            MonoBehaviour component,
            FieldInfo field,
            string hostId,
            bool includeCollections,
            int maxItems,
            List<object> refs)
        {
            object value;
            try
            {
                value = field.GetValue(component);
            }
            catch
            {
                return;
            }

            if (value == null) return;

            if (IsUnityObject(field.FieldType))
            {
                var obj = value as UnityEngine.Object;
                AddReferenceEdge(hostId, component, field, obj, refs);
                return;
            }

            if (!includeCollections)
            {
                return;
            }

            if (value is System.Collections.IList list)
            {
                int count = 0;
                foreach (var item in list)
                {
                    if (count >= maxItems) break;
                    count++;
                    if (item is UnityEngine.Object obj)
                    {
                        AddReferenceEdge(hostId, component, field, obj, refs);
                    }
                }
            }
        }

        private static bool IsUnityObject(Type fieldType)
        {
            return typeof(UnityEngine.Object).IsAssignableFrom(fieldType);
        }

        private static void AddReferenceEdge(
            string hostId,
            MonoBehaviour component,
            FieldInfo field,
            UnityEngine.Object target,
            List<object> refs)
        {
            if (target == null) return;
            string targetId = GetGlobalId(target);
            if (string.IsNullOrEmpty(targetId)) return;

            refs.Add(new
            {
                subjectId = hostId,
                objectId = targetId,
                fieldName = field.Name,
                componentType = component.GetType().FullName ?? component.GetType().Name,
                refKind = GetReferenceKind(target),
                evidence = new[]
                {
                    new EvidenceItem
                    {
                        type = EvidenceType.FieldReference,
                        detail = field.FieldType.FullName ?? field.FieldType.Name,
                        sourceObjectGlobalId = hostId,
                        sourceComponentType = component.GetType().Name,
                        fieldName = field.Name
                    }
                }
            });
        }

        private static string GetReferenceKind(UnityEngine.Object target)
        {
            if (target is GameObject) return "GameObject";
            if (target is Component) return "Component";
            return "Asset";
        }

        private static string GetGlobalId(UnityEngine.Object obj)
        {
            if (obj == null) return string.Empty;
            try
            {
                return GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
            }
            catch
            {
                return obj.GetInstanceID().ToString();
            }
        }

        private static HashSet<string> GetComponentTypeNames(GameObject go)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (go == null) return names;
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    names.Add("MissingScript");
                    continue;
                }

                var type = component.GetType();
                if (type.FullName != null) names.Add(type.FullName);
                names.Add(type.Name);
            }
            return names;
        }

        private static List<GameObject> GetAllSceneObjects(Scene activeScene, bool includeInactive)
        {
            var results = new List<GameObject>();
            var stack = new Stack<GameObject>();
            foreach (var root in activeScene.GetRootGameObjects())
            {
                if (root != null) stack.Push(root);
            }

            while (stack.Count > 0)
            {
                var go = stack.Pop();
                if (go == null) continue;
                if (includeInactive || go.activeInHierarchy)
                {
                    results.Add(go);
                }

                if (go.transform == null) continue;
                foreach (Transform child in go.transform)
                {
                    if (child != null && child.gameObject != null)
                    {
                        stack.Push(child.gameObject);
                    }
                }
            }

            return results;
        }

        private static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return string.Empty;
            try
            {
                var names = new Stack<string>();
                Transform t = go.transform;
                while (t != null)
                {
                    names.Push(t.name);
                    t = t.parent;
                }
                return string.Join("/", names);
            }
            catch
            {
                return go.name;
            }
        }

        private sealed class GameObjectEntry
        {
            public GameObject Go { get; set; }
            public string Path { get; set; }
        }
    }
}
