from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description="Manages the XR rig/origin in a runtime session. Actions: create (create XR Origin with camera and controllers), get_info (query XR state, devices, tracking), configure (set camera offset, tracking origin), set_tracking_origin (floor/device). Requires XR packages in Unity project.",
    annotations=ToolAnnotations(
        title="Manage XR Rig",
        destructiveHint=True,
    ),
)
async def manage_xr_rig(
    ctx: Context,
    action: Annotated[Literal[
        "create",
        "get_info",
        "configure",
        "set_tracking_origin",
    ], "XR rig operation to perform."],
    name: Annotated[str, "Rig name (for create)."] | None = None,
    tracking_origin: Annotated[str, "Tracking origin mode: 'floor' or 'device'."] | None = None,
    include_controllers: Annotated[bool, "Include controller GameObjects (for create)."] | None = None,
    camera_y_offset: Annotated[float, "Camera Y offset (for configure)."] | None = None,
    position: Annotated[list, "Position [x,y,z] (for create)."] | None = None,
    force: Annotated[bool, "Force create even if XR Origin exists."] | None = None,
    mode: Annotated[str, "Tracking origin mode (for set_tracking_origin)."] | None = None,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    try:
        params: dict[str, Any] = {"action": action}
        if name is not None:
            params["name"] = name
        if tracking_origin is not None:
            params["tracking_origin"] = tracking_origin
        if include_controllers is not None:
            params["include_controllers"] = include_controllers
        if camera_y_offset is not None:
            params["camera_y_offset"] = camera_y_offset
        if position is not None:
            params["position"] = position
        if force is not None:
            params["force"] = force
        if mode is not None:
            params["mode"] = mode

        response = await send_with_unity_instance(
            async_send_command_with_retry, unity_instance, "manage_xr_rig", params)

        if isinstance(response, dict) and response.get("success"):
            return {"success": True, "message": response.get("message", "XR rig operation successful."), "data": response.get("data")}
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}
    except Exception as e:
        return {"success": False, "message": f"Python error managing XR rig: {str(e)}"}
