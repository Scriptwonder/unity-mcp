from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description="Manages XR hand tracking in a runtime session. Actions: get_hand_data (joint positions for left/right hand), get_status (tracking availability), get_gestures. Requires XR Hands package (com.unity.xr.hands).",
    annotations=ToolAnnotations(
        title="Manage XR Hand Tracking",
        readOnlyHint=True,
    ),
)
async def manage_xr_hand_tracking(
    ctx: Context,
    action: Annotated[Literal[
        "get_hand_data",
        "get_status",
        "get_gestures",
    ], "Hand tracking operation to perform."],
    hand: Annotated[str, "Which hand: 'left' or 'right'."] | None = None,
    include_joint_positions: Annotated[bool, "Include all joint positions in response."] | None = None,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    try:
        params: dict[str, Any] = {"action": action}
        if hand is not None:
            params["hand"] = hand
        if include_joint_positions is not None:
            params["include_joint_positions"] = include_joint_positions

        response = await send_with_unity_instance(
            async_send_command_with_retry, unity_instance, "manage_xr_hand_tracking", params)

        if isinstance(response, dict) and response.get("success"):
            return {"success": True, "message": response.get("message", "XR hand tracking operation successful."), "data": response.get("data")}
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}
    except Exception as e:
        return {"success": False, "message": f"Python error managing XR hand tracking: {str(e)}"}
