from typing import Any

from fastmcp import Context

from models import MCPResponse
from services.registry import mcp_for_unity_resource
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

DEFAULT_CONDITION = "Unity Development"


def _normalize_component_name(component_name: str) -> str:
    if not component_name:
        return ""
    if "." in component_name:
        return component_name.rsplit(".", 1)[-1]
    return component_name


def _build_tree(objects: list[dict[str, Any]]) -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]]]:
    nodes: dict[str, dict[str, Any]] = {}
    for entry in objects:
        obj = entry.get("object") or {}
        path = obj.get("hierarchyPath")
        if not path:
            continue
        nodes[path] = {
            "object": obj,
            "activeSelf": entry.get("activeSelf"),
            "activeInHierarchy": entry.get("activeInHierarchy"),
            "tag": entry.get("tag"),
            "layer": entry.get("layer"),
            "isStatic": entry.get("isStatic"),
            "componentTypeNames": entry.get("componentTypeNames") or [],
            "transform": entry.get("transform"),
            "children": [],
        }

    roots: list[dict[str, Any]] = []
    for path, node in nodes.items():
        if "/" in path:
            parent_path = path.rsplit("/", 1)[0]
            parent = nodes.get(parent_path)
            if parent:
                parent["children"].append(node)
            else:
                roots.append(node)
        else:
            roots.append(node)

    return roots, nodes


def _sort_tree(nodes: list[dict[str, Any]]) -> None:
    for node in nodes:
        node["children"].sort(
            key=lambda c: (c.get("object") or {}).get("hierarchyPath", "")
        )
        _sort_tree(node["children"])


def _count_subtree(node: dict[str, Any]) -> int:
    count = 1
    for child in node.get("children", []):
        count += _count_subtree(child)
    return count


def _build_grouping_lenses(objects: list[dict[str, Any]], roots: list[dict[str, Any]]) -> dict[str, Any]:
    tag_counts: dict[str, int] = {}
    layer_counts: dict[str, int] = {}
    prefab_counts: dict[str, int] = {}
    component_counts: dict[str, int] = {}
    depth_counts: dict[int, int] = {}

    for entry in objects:
        obj = entry.get("object") or {}
        tag = entry.get("tag") or "Untagged"
        tag_counts[tag] = tag_counts.get(tag, 0) + 1

        layer = entry.get("layer")
        layer_key = str(layer) if layer is not None else "0"
        layer_counts[layer_key] = layer_counts.get(layer_key, 0) + 1

        prefab_path = obj.get("prefabPath")
        if prefab_path:
            prefab_counts[prefab_path] = prefab_counts.get(prefab_path, 0) + 1

        raw_components = entry.get("componentTypeNames") or []
        normalized = { _normalize_component_name(name) for name in raw_components if name }
        for comp in normalized:
            if not comp:
                continue
            component_counts[comp] = component_counts.get(comp, 0) + 1

        path = obj.get("hierarchyPath") or ""
        depth = path.count("/")
        depth_counts[depth] = depth_counts.get(depth, 0) + 1

    root_groups = []
    for root in roots:
        obj = root.get("object") or {}
        root_groups.append({
            "globalId": obj.get("globalId"),
            "name": obj.get("name"),
            "hierarchyPath": obj.get("hierarchyPath"),
            "subtreeSize": _count_subtree(root),
        })

    def top_counts(source: dict[str, int], limit: int = 25) -> list[dict[str, Any]]:
        items = sorted(source.items(), key=lambda kv: (-kv[1], kv[0]))
        return [{"key": k, "count": v} for k, v in items[:limit]]

    depth_histogram = [{"depth": k, "count": v} for k, v in sorted(depth_counts.items())]

    return {
        "roots": sorted(root_groups, key=lambda r: (-r.get("subtreeSize", 0), r.get("hierarchyPath") or "")),
        "tags": top_counts(tag_counts),
        "layers": top_counts(layer_counts),
        "prefabs": top_counts(prefab_counts),
        "components": top_counts(component_counts),
        "depthHistogram": depth_histogram,
    }


def _suggest_diagram_counts(total: int) -> dict[str, Any]:
    if total <= 80:
        count = 1
    elif total <= 200:
        count = 2
    elif total <= 400:
        count = 3
    else:
        count = 4
    target = max(30, min(150, (total // max(count, 1)) if total else 0))
    return {
        "diagramCount": count,
        "targetNodesPerDiagram": target,
    }


@mcp_for_unity_resource(
    uri="unity://mapping/scene_outline",
    name="mapping_scene_outline",
    description="Hierarchy-first scene outline for diagram grouping and mapping. Provides hierarchy tree and grouping lenses."
)
async def get_scene_outline(ctx: Context) -> MCPResponse:
    unity_instance = get_unity_instance_from_context(ctx)

    scene_name = ""
    scene_path = ""
    active_response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_scene",
        {"action": "get_active"},
    )
    if isinstance(active_response, dict) and active_response.get("success"):
        data = active_response.get("data") or {}
        scene_name = data.get("name", "") or ""
        scene_path = data.get("path", "") or ""

    objects: list[dict[str, Any]] = []
    limit = 1000
    offset = 0
    next_offset = 0
    while next_offset is not None:
        index_response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "manage_mapping",
            {
                "action": "get_scene_object_index",
                "condition": DEFAULT_CONDITION,
                "includeInactive": True,
                "includeComponentsSummary": True,
                "includeTransformSummary": False,
                "limit": limit,
                "offset": offset,
            },
        )
        if not (isinstance(index_response, dict) and index_response.get("success")):
            return MCPResponse(
                success=False,
                error=index_response.get("message", "Failed to load scene outline.")
                if isinstance(index_response, dict) else "Failed to load scene outline.",
            )

        data = index_response.get("data") or {}
        batch = data.get("objects") or []
        objects.extend(batch)
        next_offset = data.get("next_offset")
        if next_offset is None:
            break
        try:
            offset = int(next_offset)
        except Exception:
            break

    roots, _nodes = _build_tree(objects)
    _sort_tree(roots)

    total_count = len(objects)
    active_in_hierarchy = sum(1 for entry in objects if entry.get("activeInHierarchy") is True)
    active_self = sum(1 for entry in objects if entry.get("activeSelf") is True)
    inactive = total_count - active_in_hierarchy

    grouping_lenses = _build_grouping_lenses(objects, roots)
    diagram_suggestion = _suggest_diagram_counts(total_count)

    payload = {
        "condition": DEFAULT_CONDITION,
        "scene": {
            "name": scene_name,
            "path": scene_path,
        },
        "summary": {
            "totalObjects": total_count,
            "activeInHierarchy": active_in_hierarchy,
            "activeSelf": active_self,
            "inactive": inactive,
        },
        "hierarchy": roots,
        "objectsFlat": objects,
        "groupingLenses": grouping_lenses,
        "diagramGuidance": {
            "suggested": diagram_suggestion,
            "ideas": [
                "Split by top-level roots to keep diagrams readable.",
                "Split by layer or tag when roots are too large.",
                "Use component-heavy clusters (UI, cameras, systems) as diagram anchors.",
                "Keep each diagram close to the target node count.",
            ],
        },
    }

    return MCPResponse(success=True, data=payload)
