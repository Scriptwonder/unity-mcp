using System;
using System.Collections.Generic;
using UnityEngine;

namespace MCPForUnity.Runtime.Mapping
{
    [Serializable]
    public sealed class ObjectRef
    {
        public string globalId;
        public string name;
        public string hierarchyPath;
        public string prefabGuid;
        public string prefabPath;
    }

    public enum EvidenceType
    {
        ComponentType,
        FieldReference,
        PrefabLink,
        TagLayer,
        Transform,
        Annotation,
        Other
    }

    [Serializable]
    public sealed class EvidenceItem
    {
        public EvidenceType type;
        public string detail;
        public string sourceObjectGlobalId;
        public string sourceComponentType;
        public string fieldName;
    }

    public enum RelationType
    {
        owns,
        controls,
        spawns,
        drives,
        observes,
        depends_on,
        references,
        part_of
    }

    [Serializable]
    public sealed class MappingRow
    {
        public ObjectRef subject;
        public RelationType predicate;
        public ObjectRef @object;
        public float confidence;
        public List<EvidenceItem> evidence = new();
        public string subsystem;
        public string condition;
    }

    [CreateAssetMenu(menuName = "MCP/Structure Mapping Table", fileName = "StructureMappingTable")]
    public sealed class StructureMappingTable : ScriptableObject
    {
        public List<MappingRow> rows = new();
        public string generatedAt;
        public string unityVersion;

        public void StampMetadata()
        {
            generatedAt = DateTime.UtcNow.ToString("O");
            unityVersion = Application.unityVersion;
        }
    }

    [Serializable]
    public sealed class StructureMappingExport
    {
        public string generatedAt;
        public string unityVersion;
        public List<MappingRow> rows = new();
    }
}
