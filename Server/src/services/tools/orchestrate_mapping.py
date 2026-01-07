import json
import os
from typing import Annotated, Any

from fastmcp import Context

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
from services.tools.utils import coerce_bool, coerce_int
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

DEFAULT_CONDITION = "Unity Development"
DEFAULT_ASSET_PATH = "Assets/MCP/Generated/StructureMappingTable.asset"


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
    nodes.sort(key=lambda c: (c.get("object") or {}).get("hierarchyPath", ""))
    for node in nodes:
        _sort_tree(node.get("children", []))


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
        normalized = {_normalize_component_name(name) for name in raw_components if name}
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


def _build_diagrams(objects: list[dict[str, Any]], roots: list[dict[str, Any]], diagram_count: int) -> list[dict[str, Any]]:
    root_paths = []
    root_sizes: dict[str, int] = {}
    for root in roots:
        obj = root.get("object") or {}
        path = obj.get("hierarchyPath")
        if not path:
            continue
        root_paths.append(path)
        root_sizes[path] = _count_subtree(root)

    if not root_paths:
        diagram_count = max(1, diagram_count)
        return [{"title": f"Diagram {i + 1}", "rootPaths": [], "objectIds": [], "nodeCount": 0} for i in range(diagram_count)]

    diagram_count = min(max(1, diagram_count), len(root_paths))
    diagrams = [{"title": f"Diagram {i + 1}", "rootPaths": [], "objectIds": [], "nodeCount": 0} for i in range(diagram_count)]

    for root_path in sorted(root_paths, key=lambda p: (-root_sizes.get(p, 0), p)):
        diagrams.sort(key=lambda d: (d["nodeCount"], d["title"]))
        target = diagrams[0]
        target["rootPaths"].append(root_path)
        target["nodeCount"] += root_sizes.get(root_path, 0)

    for entry in objects:
        obj = entry.get("object") or {}
        path = obj.get("hierarchyPath") or ""
        gid = obj.get("globalId")
        if not gid:
            continue
        for diagram in diagrams:
            for root_path in diagram["rootPaths"]:
                if path == root_path or path.startswith(root_path + "/"):
                    diagram["objectIds"].append(gid)
                    break
            else:
                continue
            break

    return diagrams


def _build_hierarchy_rows(objects: list[dict[str, Any]], condition: str) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    path_to_ref: dict[str, dict[str, Any]] = {}
    seen: set[str] = set()

    for entry in objects:
        obj_ref = entry.get("object") or {}
        path = obj_ref.get("hierarchyPath")
        if path:
            path_to_ref[path] = obj_ref

    for entry in objects:
        child = entry.get("object") or {}
        child_path = child.get("hierarchyPath")
        child_id = child.get("globalId")
        if not child_path or "/" not in child_path or not child_id:
            continue
        parent_path = child_path.rsplit("/", 1)[0]
        parent = path_to_ref.get(parent_path)
        parent_id = parent.get("globalId") if parent else None
        if not parent or not parent_id:
            continue
        key = f"{parent_id}|part_of|{child_id}"
        if key in seen:
            continue
        seen.add(key)
        rows.append({
            "subject": parent,
            "predicate": "part_of",
            "object": child,
            "confidence": 1.0,
            "evidence": [],
            "subsystem": "Scene",
            "condition": condition,
        })

    return rows


def _write_diagrams_json(project_root: str, asset_path: str, diagrams: list[dict[str, Any]], payload: dict[str, Any]) -> str | None:
    if not project_root or not asset_path:
        return None
    if not asset_path.startswith("Assets/"):
        return None
    rel_base = asset_path.rsplit(".", 1)[0]
    rel_path = f"{rel_base}.diagrams.json"
    abs_path = os.path.join(project_root, rel_path).replace("\\", "/")
    os.makedirs(os.path.dirname(abs_path), exist_ok=True)
    data = {
        "diagrams": diagrams,
        "summary": payload.get("summary"),
        "scene": payload.get("scene"),
        "diagramGuidance": payload.get("diagramGuidance"),
        "groupingLenses": payload.get("groupingLenses"),
    }
    with open(abs_path, "w", encoding="utf-8") as handle:
        json.dump(data, handle, indent=2)
    return rel_path


@mcp_for_unity_tool(
    description="Orchestrates scene outline, diagram grouping, and mapping table commit in one call."
)
async def orchestrate_mapping(
    ctx: Context,
    condition: Annotated[str, "Condition/prompt that scopes the request."] | None = None,
    include_inactive: Annotated[bool | str, "Include inactive objects in the index."] | None = True,
    include_components_summary: Annotated[bool | str, "Include component type name summary."] | None = True,
    include_transform_summary: Annotated[bool | str, "Include transform summary for objects."] | None = False,
    asset_path: Annotated[str | None, "Asset path for committing StructureMappingTable."] = None,
    write_diagrams_json: Annotated[bool | str, "Write diagram grouping JSON next to the asset."] | None = True,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    gate = await preflight(ctx, wait_for_no_compile=True, refresh_if_dirty=True)
    if gate is not None:
        return gate.model_dump()

    resolved_condition = condition.strip() if condition else DEFAULT_CONDITION
    resolved_asset_path = asset_path or DEFAULT_ASSET_PATH
    resolved_include_inactive = coerce_bool(include_inactive, default=True)
    resolved_include_components = coerce_bool(include_components_summary, default=True)
    resolved_include_transform = coerce_bool(include_transform_summary, default=False)
    resolved_write_diagrams = coerce_bool(write_diagrams_json, default=True)

    active_scene = {"name": "", "path": ""}
    active_response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_scene",
        {"action": "get_active"},
    )
    if isinstance(active_response, dict) and active_response.get("success"):
        data = active_response.get("data") or {}
        active_scene["name"] = data.get("name", "") or ""
        active_scene["path"] = data.get("path", "") or ""

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
                "condition": resolved_condition,
                "includeInactive": resolved_include_inactive,
                "includeComponentsSummary": resolved_include_components,
                "includeTransformSummary": resolved_include_transform,
                "limit": limit,
                "offset": offset,
            },
        )
        if not (isinstance(index_response, dict) and index_response.get("success")):
            return index_response if isinstance(index_response, dict) else {
                "success": False,
                "message": str(index_response),
            }

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

    summary = {
        "totalObjects": total_count,
        "activeInHierarchy": active_in_hierarchy,
        "activeSelf": active_self,
        "inactive": inactive,
    }

    grouping_lenses = _build_grouping_lenses(objects, roots)
    diagram_suggestion = _suggest_diagram_counts(total_count)
    diagrams = _build_diagrams(objects, roots, int(diagram_suggestion["diagramCount"]))

    rows = _build_hierarchy_rows(objects, resolved_condition)
    commit_response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_mapping",
        {
            "action": "commit_mapping_table",
            "condition": resolved_condition,
            "assetPath": resolved_asset_path,
            "rows": rows,
        },
    )

    diagrams_json_path = None
    if resolved_write_diagrams:
        info_response = await send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "get_project_info",
            {},
        )
        project_root = ""
        if isinstance(info_response, dict) and info_response.get("success"):
            pdata = info_response.get("data") or {}
            project_root = pdata.get("projectRoot", "") or ""
        diagrams_json_path = _write_diagrams_json(project_root, resolved_asset_path, diagrams, {
            "summary": summary,
            "scene": active_scene,
            "diagramGuidance": {"suggested": diagram_suggestion},
            "groupingLenses": grouping_lenses,
        })

    return {
        "success": True,
        "message": "Orchestration complete.",
        "data": {
            "condition": resolved_condition,
            "scene": active_scene,
            "summary": summary,
            "diagramGuidance": {
                "suggested": diagram_suggestion,
                "ideas": [
                    "Split by top-level roots to keep diagrams readable.",
                    "Split by layer or tag when roots are too large.",
                    "Use component-heavy clusters (UI, cameras, systems) as diagram anchors.",
                    "Keep each diagram close to the target node count.",
                ],
            },
            "diagrams": diagrams,
            "mappingCommit": commit_response,
            "diagramJsonPath": diagrams_json_path,
        },
    }
