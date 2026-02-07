from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description="Queries XR boundary/guardian information in a runtime session. Actions: get_status (boundary configured, dimensions), get_play_area (play area size/points), get_geometry (boundary points). Read-only.",
    annotations=ToolAnnotations(
        title="Manage XR Boundary",
        readOnlyHint=True,
    ),
)
async def manage_xr_boundary(
    ctx: Context,
    action: Annotated[Literal[
        "get_status",
        "get_play_area",
        "get_geometry",
    ], "Boundary query to perform."],
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    try:
        params: dict[str, Any] = {"action": action}

        response = await send_with_unity_instance(
            async_send_command_with_retry, unity_instance, "manage_xr_boundary", params)

        if isinstance(response, dict) and response.get("success"):
            return {"success": True, "message": response.get("message", "XR boundary query successful."), "data": response.get("data")}
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}
    except Exception as e:
        return {"success": False, "message": f"Python error querying XR boundary: {str(e)}"}
