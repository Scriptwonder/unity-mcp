using System;
using System.IO;
using MCPForUnity.Runtime.Mapping;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Mapping
{
    public static class StructureMappingTableMenu
    {
        private const string DefaultFolder = "Assets/MCP/Generated";

        [MenuItem("MCP/MappingTable/Create Empty (Debug)")]
        private static void CreateEmptyMappingTable()
        {
            EnsureFolder(DefaultFolder);

            string assetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(DefaultFolder, "StructureMappingTable.asset"));
            var table = ScriptableObject.CreateInstance<StructureMappingTable>();
            table.rows = BuildDummyRows();
            table.StampMetadata();

            AssetDatabase.CreateAsset(table, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string jsonPath = Path.ChangeExtension(assetPath, ".json");
            WriteJsonExport(table, jsonPath);

            EditorUtility.FocusProjectWindow();
            Selection.activeObject = table;
        }

        private static void WriteJsonExport(StructureMappingTable table, string assetJsonPath)
        {
            var export = new StructureMappingExport
            {
                generatedAt = table.generatedAt,
                unityVersion = table.unityVersion,
                rows = table.rows ?? new System.Collections.Generic.List<MappingRow>()
            };

            string json = JsonConvert.SerializeObject(export, Formatting.Indented);
            File.WriteAllText(assetJsonPath, json, new System.Text.UTF8Encoding(false));
        }

        private static System.Collections.Generic.List<MappingRow> BuildDummyRows()
        {
            return new System.Collections.Generic.List<MappingRow>
            {
                new MappingRow
                {
                    subject = new ObjectRef
                    {
                        globalId = "GlobalObjectId_V0_Subject",
                        name = "ExampleSubject",
                        hierarchyPath = "Root/ExampleSubject"
                    },
                    predicate = RelationType.references,
                    @object = new ObjectRef
                    {
                        globalId = "GlobalObjectId_V0_Object",
                        name = "ExampleObject",
                        hierarchyPath = "Root/ExampleObject"
                    },
                    confidence = 0.5f,
                    evidence = new System.Collections.Generic.List<EvidenceItem>
                    {
                        new EvidenceItem
                        {
                            type = EvidenceType.Other,
                            detail = "dummy row"
                        }
                    },
                    subsystem = "Debug",
                    condition = "example condition"
                }
            };
        }

        private static void EnsureFolder(string folderPath)
        {
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
    }
}
