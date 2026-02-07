from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description="Manages XR passthrough (mixed reality) in a runtime session. Actions: enable, disable, get_status, configure (opacity, edge rendering), set_style (default, stylized, dimmed, bright). Supports Meta XR SDK and generic transparent camera fallback.",
    annotations=ToolAnnotations(
        title="Manage XR Passthrough",
        destructiveHint=True,
    ),
)
async def manage_xr_passthrough(
    ctx: Context,
    action: Annotated[Literal[
        "enable",
        "disable",
        "get_status",
        "configure",
        "set_style",
    ], "Passthrough operation to perform."],
    opacity: Annotated[float, "Passthrough opacity 0-1 (for configure)."] | None = None,
    edge_rendering: Annotated[bool, "Enable edge rendering (for configure)."] | None = None,
    overlay_type: Annotated[str, "Overlay type: 'underlay' or 'overlay' (for configure)."] | None = None,
    style: Annotated[str, "Passthrough style: default, stylized, dimmed, bright (for set_style)."] | None = None,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    try:
        params: dict[str, Any] = {"action": action}
        if opacity is not None:
            params["opacity"] = opacity
        if edge_rendering is not None:
            params["edge_rendering"] = edge_rendering
        if overlay_type is not None:
            params["overlay_type"] = overlay_type
        if style is not None:
            params["style"] = style

        response = await send_with_unity_instance(
            async_send_command_with_retry, unity_instance, "manage_xr_passthrough", params)

        if isinstance(response, dict) and response.get("success"):
            return {"success": True, "message": response.get("message", "XR passthrough operation successful."), "data": response.get("data")}
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}
    except Exception as e:
        return {"success": False, "message": f"Python error managing XR passthrough: {str(e)}"}
