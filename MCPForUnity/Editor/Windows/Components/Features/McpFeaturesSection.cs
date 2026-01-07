using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Runtime.Mapping;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Windows.Components.Features
{
    /// <summary>
    /// Controller for the Features section inside the MCP For Unity editor window.
    /// </summary>
    public class McpFeaturesSection
    {
        private Button showGraphButton;
        private Button showSemanticButton;
        private Button showTableButton;
        private DropdownField domainDropdown;
        private TextField outputField;
        private VisualElement graphView;
        private Label graphStatusLabel;
        private GraphView graphCanvas;
        private VisualElement mappingViewer;
        private IMGUIContainer mappingGui;
        private StructureMappingTable mappingTable;
        private string mappingSearch = string.Empty;
        private int mappingPredicateIndex = 0;
        private Vector2 mappingScroll;
        private static readonly string[] PredicateOptions = BuildPredicateOptions();

        public VisualElement Root { get; }

        public McpFeaturesSection(VisualElement root)
        {
            Root = root;
            CacheUIElements();
            RegisterCallbacks();
        }

        private void CacheUIElements()
        {
            showGraphButton = Root.Q<Button>("show-graph-button");
            showSemanticButton = Root.Q<Button>("show-semantic-button");
            showTableButton = Root.Q<Button>("show-table-button");
            domainDropdown = Root.Q<DropdownField>("domain-dropdown");
            outputField = Root.Q<TextField>("features-output");
            graphView = Root.Q<VisualElement>("graph-view");
            graphStatusLabel = Root.Q<Label>("graph-status");
            mappingViewer = Root.Q<VisualElement>("mapping-viewer");

            if (domainDropdown != null)
            {
                domainDropdown.choices = new List<string> { "generic", "game", "education" };
                domainDropdown.value = "generic";
            }

            if (outputField != null)
            {
                outputField.isReadOnly = true;
            }

            BuildGraphView();
            BuildMappingViewer();
        }

        private void RegisterCallbacks()
        {
            if (showGraphButton != null)
            {
                showGraphButton.clicked += () =>
                {
                    RenderLayer(GraphLayer.Object);
                };
            }

            if (showSemanticButton != null)
            {
                showSemanticButton.clicked += () =>
                {
                    RenderLayer(GraphLayer.Semantic);
                };
            }

            if (showTableButton != null)
            {
                showTableButton.clicked += () =>
                {
                    RenderLayer(GraphLayer.Concept);
                };
            }
        }

        private void RenderLayer(GraphLayer layer)
        {
            if (outputField == null)
            {
                return;
            }

            try
            {
                string domain = domainDropdown != null ? domainDropdown.value : "generic";
                var graphResponse = ManageScene.ExecuteSceneGraph();
                if (graphResponse is ErrorResponse)
                {
                    UpdateOutput(graphResponse);
                    return;
                }

                if (graphResponse is not SuccessResponse graphSuccess)
                {
                    UpdateOutput(new ErrorResponse("Unexpected response from scene tools."));
                    return;
                }

                JObject conceptData = null;
                if (layer == GraphLayer.Concept)
                {
                    var conceptResponse = ManageScene.ExecuteConceptTable(domain);
                    if (conceptResponse is ErrorResponse)
                    {
                        UpdateOutput(conceptResponse);
                        return;
                    }

                    if (conceptResponse is not SuccessResponse conceptSuccess)
                    {
                        UpdateOutput(new ErrorResponse("Unexpected response from scene tools."));
                        return;
                    }

                    conceptData = JObject.FromObject(conceptSuccess.Data ?? new object());
                }

                RenderGraphCanvas(graphSuccess.Data, conceptData, layer);
                outputField.SetValueWithoutNotify($"Rendered {layer.ToString().ToLowerInvariant()} layer.");
            }
            catch (Exception ex)
            {
                outputField.SetValueWithoutNotify($"Failed to render graph: {ex.Message}");
            }
        }

        private void UpdateOutput(object response)
        {
            if (outputField == null)
            {
                return;
            }

            try
            {
                outputField.SetValueWithoutNotify(JsonConvert.SerializeObject(response, Formatting.Indented));
            }
            catch (Exception ex)
            {
                outputField.SetValueWithoutNotify($"Failed to format output: {ex.Message}");
            }

            if (response is ErrorResponse error && !string.IsNullOrWhiteSpace(error.Error))
            {
                McpLog.Error(error.Error);
            }
        }

        private void BuildGraphView()
        {
            if (graphView == null)
            {
                return;
            }

            graphView.Clear();
            graphCanvas = new FeaturesGraphView();
            graphCanvas.StretchToParentSize();
            graphCanvas.SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            graphCanvas.AddManipulator(new ContentDragger());
            graphCanvas.AddManipulator(new SelectionDragger());
            graphCanvas.AddManipulator(new RectangleSelector());
            graphCanvas.AddManipulator(new ContentZoomer());

            var grid = new GridBackground();
            graphCanvas.Insert(0, grid);
            grid.StretchToParentSize();

            graphView.Add(graphCanvas);
        }

        private void BuildMappingViewer()
        {
            if (mappingViewer == null)
            {
                return;
            }

            mappingGui = new IMGUIContainer(DrawMappingViewer);
            mappingViewer.Add(mappingGui);
        }

        private void DrawMappingViewer()
        {
            EditorGUILayout.Space(4);
            mappingTable = (StructureMappingTable)EditorGUILayout.ObjectField(
                "Table Asset",
                mappingTable,
                typeof(StructureMappingTable),
                false
            );

            using (new EditorGUILayout.HorizontalScope())
            {
                mappingSearch = EditorGUILayout.TextField("Search", mappingSearch);
                mappingPredicateIndex = EditorGUILayout.Popup("Predicate", mappingPredicateIndex, PredicateOptions);
            }

            EditorGUILayout.Space(6);

            if (mappingTable == null)
            {
                EditorGUILayout.HelpBox("Assign a StructureMappingTable asset to browse rows.", MessageType.Info);
                return;
            }

            var rows = FilterRows(mappingTable.rows, mappingSearch, PredicateOptions[mappingPredicateIndex]);
            EditorGUILayout.LabelField($"Rows: {rows.Count}", EditorStyles.miniLabel);

            mappingScroll = EditorGUILayout.BeginScrollView(mappingScroll, GUILayout.Height(260));
            DrawRows(rows);
            EditorGUILayout.EndScrollView();
        }

        private static List<MappingRow> FilterRows(List<MappingRow> rows, string search, string predicateLabel)
        {
            IEnumerable<MappingRow> filtered = rows ?? Enumerable.Empty<MappingRow>();

            if (!string.IsNullOrWhiteSpace(search))
            {
                string needle = search.Trim().ToLowerInvariant();
                filtered = filtered.Where(row =>
                    Contains(row.subject, needle) ||
                    Contains(row.@object, needle) ||
                    (!string.IsNullOrEmpty(row.subsystem) && row.subsystem.ToLowerInvariant().Contains(needle)) ||
                    (!string.IsNullOrEmpty(row.condition) && row.condition.ToLowerInvariant().Contains(needle)));
            }

            if (!string.Equals(predicateLabel, "All", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(row => row.predicate.ToString() == predicateLabel);
            }

            return filtered.ToList();
        }

        private static bool Contains(ObjectRef obj, string needle)
        {
            if (obj == null) return false;
            if (!string.IsNullOrEmpty(obj.name) && obj.name.ToLowerInvariant().Contains(needle)) return true;
            if (!string.IsNullOrEmpty(obj.hierarchyPath) && obj.hierarchyPath.ToLowerInvariant().Contains(needle)) return true;
            if (!string.IsNullOrEmpty(obj.globalId) && obj.globalId.ToLowerInvariant().Contains(needle)) return true;
            return false;
        }

        private static void DrawRows(List<MappingRow> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                EditorGUILayout.HelpBox("No rows match the current filters.", MessageType.Info);
                return;
            }

            foreach (var row in rows)
            {
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField($"{RowLabel(row)}", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField($"Confidence: {row.confidence:0.00}");
                    if (!string.IsNullOrEmpty(row.subsystem))
                    {
                        EditorGUILayout.LabelField($"Subsystem: {row.subsystem}");
                    }
                    if (!string.IsNullOrEmpty(row.condition))
                    {
                        EditorGUILayout.LabelField($"Condition: {row.condition}");
                    }
                    if (row.evidence != null && row.evidence.Count > 0)
                    {
                        EditorGUILayout.LabelField("Evidence:", EditorStyles.miniBoldLabel);
                        foreach (var evidence in row.evidence)
                        {
                            EditorGUILayout.LabelField($"- {evidence.type}: {evidence.detail}");
                        }
                    }
                }
            }
        }

        private static string RowLabel(MappingRow row)
        {
            string subject = row.subject != null ? row.subject.name : "Unknown";
            string obj = row.@object != null ? row.@object.name : "Unknown";
            return $"{subject}  {row.predicate}  {obj}";
        }

        private static string[] BuildPredicateOptions()
        {
            var values = Enum.GetNames(typeof(RelationType));
            var list = new List<string> { "All" };
            list.AddRange(values);
            return list.ToArray();
        }

        private void RenderGraphCanvas(object graphData, JObject conceptData, GraphLayer layer)
        {
            if (graphCanvas == null)
            {
                BuildGraphView();
            }

            if (graphCanvas == null)
            {
                return;
            }

            graphCanvas.DeleteElements(graphCanvas.graphElements.ToList());

            var sceneNodes = BuildSceneNodes(graphData, layer);
            var edges = BuildSceneEdges(graphData, sceneNodes);

            if (layer == GraphLayer.Concept)
            {
                var conceptNodes = BuildConceptNodes(conceptData, sceneNodes);
                edges = conceptNodes.Edges;
                sceneNodes = conceptNodes.Nodes;
            }

            LayoutNodes(sceneNodes, layer);

            var portLookup = new Dictionary<string, Port>();
            foreach (var node in sceneNodes.Values)
            {
                graphCanvas.AddElement(node.GraphNode);
                portLookup[node.Key] = node.OutputPort;
            }

            int edgeCount = 0;
            foreach (var edge in edges)
            {
                if (!portLookup.TryGetValue(edge.FromKey, out var fromPort)
                    || !portLookup.TryGetValue(edge.ToKey, out var toPort))
                {
                    continue;
                }

                var graphEdge = new Edge
                {
                    output = fromPort,
                    input = toPort
                };
                graphEdge.input.Connect(graphEdge);
                graphEdge.output.Connect(graphEdge);
                graphCanvas.AddElement(graphEdge);
                edgeCount++;
            }

            if (graphStatusLabel != null)
            {
                graphStatusLabel.text = $"Layer: {layer.ToString().ToLowerInvariant()} | nodes: {sceneNodes.Count} | edges: {edgeCount}";
            }
        }

        private Dictionary<string, GraphNodeData> BuildSceneNodes(object graphData, GraphLayer layer)
        {
            var root = JObject.FromObject(graphData ?? new object());
            var nodes = root["nodes"] as JArray;
            var result = new Dictionary<string, GraphNodeData>();
            if (nodes == null)
            {
                return result;
            }

            var nodeLookup = nodes
                .OfType<JObject>()
                .Where(obj => (obj.Value<int?>("instanceID") ?? 0) != 0)
                .ToDictionary(obj => obj.Value<int?>("instanceID").Value, obj => obj);

            bool ShouldInclude(JObject obj)
            {
                if (layer == GraphLayer.Object)
                {
                    return true;
                }

                bool active = obj.Value<bool?>("activeInHierarchy") ?? false;
                string tag = obj.Value<string>("tag") ?? string.Empty;
                int hideFlags = obj.Value<int?>("hideFlags") ?? 0;
                return active && tag != "EditorOnly" && hideFlags == 0;
            }

            foreach (var kvp in nodeLookup)
            {
                var obj = kvp.Value;
                if (!ShouldInclude(obj))
                {
                    continue;
                }

                string key = kvp.Key.ToString();
                var node = new Node { title = obj.Value<string>("name") ?? "Unnamed" };
                node.AddToClassList("mcp-graph-node");

                var label = new Label(obj.Value<string>("path") ?? string.Empty);
                label.style.fontSize = 9;
                label.style.color = new Color(0.7f, 0.7f, 0.7f);
                node.extensionContainer.Add(label);
                node.RefreshExpandedState();

                var inputPort = node.InstantiatePort(Orientation.Vertical, Direction.Input, Port.Capacity.Multi, typeof(float));
                var outputPort = node.InstantiatePort(Orientation.Vertical, Direction.Output, Port.Capacity.Multi, typeof(float));
                node.inputContainer.Add(inputPort);
                node.outputContainer.Add(outputPort);
                node.RefreshPorts();

                result[key] = new GraphNodeData
                {
                    Key = key,
                    InstanceId = kvp.Key,
                    ParentKey = (obj["parentId"]?.Type == JTokenType.Null) ? null : obj.Value<int?>("parentId")?.ToString(),
                    GraphNode = node,
                    OutputPort = outputPort,
                    InputPort = inputPort,
                };
            }

            return result;
        }

        private List<GraphEdgeData> BuildSceneEdges(object graphData, Dictionary<string, GraphNodeData> nodes)
        {
            var root = JObject.FromObject(graphData ?? new object());
            var edges = root["edges"] as JArray;
            var result = new List<GraphEdgeData>();
            if (edges == null)
            {
                return result;
            }

            foreach (var token in edges.OfType<JObject>())
            {
                int? parentId = token.Value<int?>("parentId");
                int? childId = token.Value<int?>("childId");
                if (!parentId.HasValue || !childId.HasValue)
                {
                    continue;
                }

                string fromKey = parentId.Value.ToString();
                string toKey = childId.Value.ToString();
                if (!nodes.ContainsKey(fromKey) || !nodes.ContainsKey(toKey))
                {
                    continue;
                }

                result.Add(new GraphEdgeData { FromKey = fromKey, ToKey = toKey });
            }

            return result;
        }

        private ConceptGraph BuildConceptNodes(JObject conceptData, Dictionary<string, GraphNodeData> sceneNodes)
        {
            var result = new ConceptGraph
            {
                Nodes = new Dictionary<string, GraphNodeData>(),
                Edges = new List<GraphEdgeData>()
            };

            if (conceptData == null)
            {
                return result;
            }

            var entries = conceptData["entries"] as JArray;
            if (entries == null)
            {
                return result;
            }

            var conceptBuckets = new Dictionary<string, List<JObject>>();
            foreach (var entry in entries.OfType<JObject>())
            {
                string concept = entry.Value<string>("concept") ?? "unknown";
                if (!conceptBuckets.TryGetValue(concept, out var list))
                {
                    list = new List<JObject>();
                    conceptBuckets[concept] = list;
                }
                list.Add(entry);
            }

            foreach (var kvp in conceptBuckets)
            {
                string conceptKey = $"concept:{kvp.Key}";
                var conceptNode = new Node { title = kvp.Key };
                conceptNode.AddToClassList("mcp-graph-node");
                var inputPort = conceptNode.InstantiatePort(Orientation.Vertical, Direction.Input, Port.Capacity.Multi, typeof(float));
                var outputPort = conceptNode.InstantiatePort(Orientation.Vertical, Direction.Output, Port.Capacity.Multi, typeof(float));
                conceptNode.inputContainer.Add(inputPort);
                conceptNode.outputContainer.Add(outputPort);
                conceptNode.RefreshPorts();

                result.Nodes[conceptKey] = new GraphNodeData
                {
                    Key = conceptKey,
                    GraphNode = conceptNode,
                    OutputPort = outputPort,
                    InputPort = inputPort,
                };

                foreach (var entry in kvp.Value)
                {
                    int id = entry.Value<int?>("instanceID") ?? 0;
                    if (id == 0)
                    {
                        continue;
                    }

                    string objectKey = id.ToString();
                    if (!result.Nodes.TryGetValue(objectKey, out var objectNode))
                    {
                        if (!sceneNodes.TryGetValue(objectKey, out var sceneNode))
                        {
                            var node = new Node { title = entry.Value<string>("name") ?? "Unnamed" };
                            node.AddToClassList("mcp-graph-node");
                            var input = node.InstantiatePort(Orientation.Vertical, Direction.Input, Port.Capacity.Multi, typeof(float));
                            var output = node.InstantiatePort(Orientation.Vertical, Direction.Output, Port.Capacity.Multi, typeof(float));
                            node.inputContainer.Add(input);
                            node.outputContainer.Add(output);
                            node.RefreshPorts();

                            objectNode = new GraphNodeData
                            {
                                Key = objectKey,
                                GraphNode = node,
                                OutputPort = output,
                                InputPort = input,
                            };
                        }
                        else
                        {
                            objectNode = sceneNode;
                        }

                        result.Nodes[objectKey] = objectNode;
                    }

                    result.Edges.Add(new GraphEdgeData { FromKey = conceptKey, ToKey = objectKey });
                }
            }

            return result;
        }

        private void LayoutNodes(Dictionary<string, GraphNodeData> nodes, GraphLayer layer)
        {
            var layers = new Dictionary<int, List<GraphNodeData>>();
            foreach (var node in nodes.Values)
            {
                int depth = 0;
                if (layer == GraphLayer.Concept)
                {
                    depth = node.Key.StartsWith("concept:", StringComparison.Ordinal) ? 0 : 1;
                }
                else
                {
                    string parentKey = node.ParentKey;
                    while (!string.IsNullOrEmpty(parentKey) && nodes.TryGetValue(parentKey, out var parent))
                    {
                        depth++;
                        parentKey = parent.ParentKey;
                        if (depth > 10)
                        {
                            break;
                        }
                    }
                }

                if (!layers.TryGetValue(depth, out var list))
                {
                    list = new List<GraphNodeData>();
                    layers[depth] = list;
                }
                list.Add(node);
            }

            const float xSpacing = 260f;
            const float ySpacing = 90f;

            foreach (var kvp in layers)
            {
                int depth = kvp.Key;
                var list = kvp.Value.OrderBy(n => n.GraphNode.title).ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    float x = depth * xSpacing + 20f;
                    float y = i * ySpacing + 20f;
                    list[i].GraphNode.SetPosition(new Rect(x, y, 200f, 60f));
                }
            }
        }

        private enum GraphLayer
        {
            Object,
            Semantic,
            Concept
        }

        private sealed class GraphNodeData
        {
            public string Key { get; set; }
            public int InstanceId { get; set; }
            public string ParentKey { get; set; }
            public Node GraphNode { get; set; }
            public Port OutputPort { get; set; }
            public Port InputPort { get; set; }
        }

        private sealed class GraphEdgeData
        {
            public string FromKey { get; set; }
            public string ToKey { get; set; }
        }

        private sealed class ConceptGraph
        {
            public Dictionary<string, GraphNodeData> Nodes { get; set; }
            public List<GraphEdgeData> Edges { get; set; }
        }

        private sealed class FeaturesGraphView : GraphView
        {
        }
    }
}
