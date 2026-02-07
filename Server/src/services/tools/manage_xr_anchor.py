from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description="Manages XR spatial anchors in a runtime session. Actions: create (place anchor at position), get (retrieve anchor), list (all anchors), delete, update (move/rename). Used for MR content placement.",
    annotations=ToolAnnotations(
        title="Manage XR Anchor",
        destructiveHint=True,
    ),
)
async def manage_xr_anchor(
    ctx: Context,
    action: Annotated[Literal[
        "create",
        "get",
        "list",
        "delete",
        "update",
    ], "Anchor operation to perform."],
    anchor_id: Annotated[str, "Anchor ID (for get/delete/update)."] | None = None,
    name: Annotated[str, "Anchor name."] | None = None,
    position: Annotated[list, "Position [x,y,z]."] | None = None,
    rotation: Annotated[list, "Rotation [x,y,z] Euler angles."] | None = None,
    attach_object: Annotated[str, "GameObject to attach to anchor."] | None = None,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    try:
        params: dict[str, Any] = {"action": action}
        if anchor_id is not None:
            params["anchor_id"] = anchor_id
        if name is not None:
            params["name"] = name
        if position is not None:
            params["position"] = position
        if rotation is not None:
            params["rotation"] = rotation
        if attach_object is not None:
            params["attach_object"] = attach_object

        response = await send_with_unity_instance(
            async_send_command_with_retry, unity_instance, "manage_xr_anchor", params)

        if isinstance(response, dict) and response.get("success"):
            return {"success": True, "message": response.get("message", "XR anchor operation successful."), "data": response.get("data")}
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}
    except Exception as e:
        return {"success": False, "message": f"Python error managing XR anchor: {str(e)}"}
