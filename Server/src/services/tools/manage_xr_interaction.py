from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description="Manages XR interaction components in a runtime session. Actions: add_interactable (grab/ray/poke), add_interactor, get_interactables, configure. Requires XR Interaction Toolkit package.",
    annotations=ToolAnnotations(
        title="Manage XR Interaction",
        destructiveHint=True,
    ),
)
async def manage_xr_interaction(
    ctx: Context,
    action: Annotated[Literal[
        "add_interactable",
        "add_interactor",
        "get_interactables",
        "configure",
    ], "XR interaction operation to perform."],
    target: Annotated[str, "Target GameObject name/path/id."] | None = None,
    interaction_type: Annotated[str, "Interaction type: grab, ray, poke."] | None = None,
    properties: Annotated[dict, "Additional properties to configure."] | None = None,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    try:
        params: dict[str, Any] = {"action": action}
        if target is not None:
            params["target"] = target
        if interaction_type is not None:
            params["interaction_type"] = interaction_type
        if properties is not None:
            params["properties"] = properties

        response = await send_with_unity_instance(
            async_send_command_with_retry, unity_instance, "manage_xr_interaction", params)

        if isinstance(response, dict) and response.get("success"):
            return {"success": True, "message": response.get("message", "XR interaction operation successful."), "data": response.get("data")}
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}
    except Exception as e:
        return {"success": False, "message": f"Python error managing XR interaction: {str(e)}"}
