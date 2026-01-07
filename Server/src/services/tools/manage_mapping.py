from typing import Annotated, Literal, Any

from fastmcp import Context
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
from services.tools.utils import coerce_bool, coerce_int
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

DEFAULT_CONDITION = "Unity Development"
DEFAULT_ASSET_PATH = "Assets/MCP/Generated/StructureMappingTable.asset"


@mcp_for_unity_tool(
    description="Builds prompt-conditioned mapping snapshots for the active Unity scene."
)
async def manage_mapping(
    ctx: Context,
    action: Annotated[Literal[
        "get_scene_object_index",
        "get_object_evidence",
        "get_reference_edges",
        "validate_mapping_rows",
        "commit_mapping_table",
    ], "Mapping and evidence operations for the active Unity scene."],
    condition: Annotated[str, "Condition/prompt that scopes the request."] | None = None,
    include_inactive: Annotated[bool | str, "Include inactive objects in the index."] | None = None,
    include_components_summary: Annotated[bool | str, "Include component type name summary."] | None = None,
    include_transform_summary: Annotated[bool | str, "Include transform summary for objects."] | None = None,
    include_public_fields: Annotated[bool | str, "Include public serialized fields."] | None = None,
    include_private_serialize_field: Annotated[bool | str, "Include private [SerializeField] fields."] | None = None,
    include_collections: Annotated[bool | str, "Include array/list fields."] | None = None,
    max_ref_items_per_field: Annotated[int | str, "Max collection items to inspect per field."] | None = None,
    min_confidence: Annotated[float | str, "Minimum confidence threshold for validation."] | None = None,
    require_evidence: Annotated[bool | str, "Require evidence items for validation."] | None = None,
    limit: Annotated[int | str, "Max objects to return per page."] | None = None,
    offset: Annotated[int | str, "Page offset for objects."] | None = None,
    object_ids: Annotated[list[str] | None, "Object GlobalObjectId list for evidence fetch."] = None,
    rows: Annotated[list[dict[str, Any]] | None, "Mapping rows to validate/commit."] = None,
    asset_path: Annotated[str | None, "Asset path for committing StructureMappingTable."] = None,
    json_path: Annotated[str | None, "JSON path for mapping table export."] = None,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    gate = await preflight(ctx, wait_for_no_compile=True, refresh_if_dirty=True)
    if gate is not None:
        return gate.model_dump()
    try:
        resolved_condition = condition.strip() if condition else DEFAULT_CONDITION
        params: dict[str, Any] = {"action": action, "condition": resolved_condition}

        coerced_include_inactive = coerce_bool(include_inactive, default=None)
        coerced_include_components_summary = coerce_bool(include_components_summary, default=None)
        coerced_include_transform_summary = coerce_bool(include_transform_summary, default=None)
        coerced_include_public_fields = coerce_bool(include_public_fields, default=None)
        coerced_include_private_serialize_field = coerce_bool(include_private_serialize_field, default=None)
        coerced_include_collections = coerce_bool(include_collections, default=None)
        coerced_max_ref_items_per_field = coerce_int(max_ref_items_per_field, default=None)
        coerced_min_confidence = float(min_confidence) if min_confidence is not None else None
        coerced_require_evidence = coerce_bool(require_evidence, default=None)
        coerced_limit = coerce_int(limit, default=None)
        coerced_offset = coerce_int(offset, default=None)

        if coerced_include_inactive is not None:
            params["includeInactive"] = coerced_include_inactive
        if coerced_include_components_summary is not None:
            params["includeComponentsSummary"] = coerced_include_components_summary
        if coerced_include_transform_summary is not None:
            params["includeTransformSummary"] = coerced_include_transform_summary
        if coerced_include_public_fields is not None:
            params["includePublicFields"] = coerced_include_public_fields
        if coerced_include_private_serialize_field is not None:
            params["includePrivateSerializeField"] = coerced_include_private_serialize_field
        if coerced_include_collections is not None:
            params["includeCollections"] = coerced_include_collections
        if coerced_max_ref_items_per_field is not None:
            params["maxRefItemsPerField"] = coerced_max_ref_items_per_field
        if coerced_min_confidence is not None:
            params["minConfidence"] = coerced_min_confidence
        if coerced_require_evidence is not None:
            params["requireEvidence"] = coerced_require_evidence
        if coerced_limit is not None:
            params["limit"] = coerced_limit
        if coerced_offset is not None:
            params["offset"] = coerced_offset
        if object_ids:
            params["objectIds"] = object_ids
        if rows:
            params["rows"] = rows
        if asset_path:
            params["assetPath"] = asset_path
        if json_path:
            params["jsonPath"] = json_path
        if action == "commit_mapping_table" and not params.get("assetPath"):
            params["assetPath"] = DEFAULT_ASSET_PATH

        response = await send_with_unity_instance(
            async_send_command_with_retry, unity_instance, "manage_mapping", params
        )

        if isinstance(response, dict) and response.get("success"):
            return {
                "success": True,
                "message": response.get("message", "Mapping operation successful."),
                "data": response.get("data"),
            }
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}
    except Exception as e:
        return {"success": False, "message": f"Python error managing mapping: {str(e)}"}
