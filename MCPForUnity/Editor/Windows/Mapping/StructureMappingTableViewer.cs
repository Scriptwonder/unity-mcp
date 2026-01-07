using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Runtime.Mapping;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows.Mapping
{
    public sealed class StructureMappingTableViewer : EditorWindow
    {
        private StructureMappingTable table;
        private string searchText = string.Empty;
        private int predicateIndex = 0;
        private Vector2 scrollPosition;
        private static readonly string[] PredicateOptions = BuildPredicateOptions();

        [MenuItem("MCP/MappingTable/Viewer")]
        public static void ShowWindow()
        {
            var window = GetWindow<StructureMappingTableViewer>("Mapping Table");
            window.minSize = new Vector2(800, 400);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Structure Mapping Table Viewer", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            table = (StructureMappingTable)EditorGUILayout.ObjectField("Table Asset", table, typeof(StructureMappingTable), false);

            using (new EditorGUILayout.HorizontalScope())
            {
                searchText = EditorGUILayout.TextField("Search", searchText);
                predicateIndex = EditorGUILayout.Popup("Predicate", predicateIndex, PredicateOptions);
            }

            EditorGUILayout.Space(6);

            if (table == null)
            {
                EditorGUILayout.HelpBox("Assign a StructureMappingTable asset to browse rows.", MessageType.Info);
                return;
            }

            var rows = FilterRows(table.rows, searchText, PredicateOptions[predicateIndex]);
            EditorGUILayout.LabelField($"Rows: {rows.Count}", EditorStyles.miniLabel);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
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
    }
}
