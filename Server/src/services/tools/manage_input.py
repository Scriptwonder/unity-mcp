from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry

# All possible actions grouped by category
DETECT_ACTIONS = ["ping", "get_input_actions_info"]

ASSET_ACTIONS = [
    "create_input_actions", "add_action_map", "remove_action_map",
    "add_action", "remove_action", "set_action_properties",
]

BINDING_ACTIONS = ["add_binding", "add_composite_binding", "remove_binding"]

CONFIG_ACTIONS = [
    "add_control_scheme", "remove_control_scheme",
    "setup_player_input", "create_preset",
]

ALL_ACTIONS = DETECT_ACTIONS + ASSET_ACTIONS + BINDING_ACTIONS + CONFIG_ACTIONS


@mcp_for_unity_tool(
    group="core",
    description=(
        "Manage Unity Input System assets and configuration. Requires the Input System package. "
        "Use ping to check availability.\n\n"
        "DETECT:\n"
        "- ping: Check if Input System package is installed and available\n"
        "- get_input_actions_info: List all .inputactions assets in the project\n\n"
        "ASSET:\n"
        "- create_input_actions: Create a new .inputactions asset at asset_path\n"
        "- add_action_map: Add an InputActionMap to an asset\n"
        "- remove_action_map: Remove an InputActionMap from an asset\n"
        "- add_action: Add an InputAction to a map (properties: type, expectedControlType, interactions, processors)\n"
        "- remove_action: Remove an InputAction from a map\n"
        "- set_action_properties: Update properties on an existing action\n\n"
        "BINDING:\n"
        "- add_binding: Add a binding to an action (properties: path, interactions, processors, groups)\n"
        "- add_composite_binding: Add a composite binding (properties: compositeType, parts [{name, path}])\n"
        "- remove_binding: Remove a binding by index or path\n\n"
        "CONFIG:\n"
        "- add_control_scheme: Add a control scheme (properties: name, devices [{devicePath, isOptional}])\n"
        "- remove_control_scheme: Remove a control scheme by name\n"
        "- setup_player_input: Add/configure PlayerInput component on target GameObject\n"
        "- create_preset: Create a full input preset (properties: preset = fps/third_person/platformer/vehicle/ui/rts)"
    ),
    annotations=ToolAnnotations(
        title="Manage Input",
        destructiveHint=True,
    ),
)
async def manage_input(
    ctx: Context,
    action: Annotated[str, "The input action to perform."],
    asset_path: Annotated[
        str | None,
        "Path to .inputactions asset (e.g., 'Assets/Settings/Controls.inputactions').",
    ] = None,
    map_name: Annotated[str | None, "Name of the InputActionMap."] = None,
    action_name: Annotated[str | None, "Name of the InputAction within the map."] = None,
    properties: Annotated[
        dict[str, Any] | str | None,
        "Action-specific parameters (dict or JSON string).",
    ] = None,
    target: Annotated[
        str | None, "Target GameObject (for setup_player_input)."
    ] = None,
    search_method: Annotated[
        Literal["by_id", "by_name", "by_path"] | None,
        "How to find target.",
    ] = None,
) -> dict[str, Any]:
    """Unified Input System management tool."""

    action_normalized = action.lower()

    if action_normalized not in ALL_ACTIONS:
        categories = {
            "Detect": DETECT_ACTIONS,
            "Asset": ASSET_ACTIONS,
            "Binding": BINDING_ACTIONS,
            "Config": CONFIG_ACTIONS,
        }
        category_list = "; ".join(
            f"{cat}: {', '.join(actions)}" for cat, actions in categories.items()
        )
        return {
            "success": False,
            "message": (
                f"Unknown action '{action}'. Available actions by category — {category_list}. "
                "Run with action='ping' to check Input System availability."
            ),
        }

    unity_instance = await get_unity_instance_from_context(ctx)

    params_dict: dict[str, Any] = {"action": action_normalized}
    if asset_path is not None:
        params_dict["assetPath"] = asset_path
    if map_name is not None:
        params_dict["mapName"] = map_name
    if action_name is not None:
        params_dict["actionName"] = action_name
    if properties is not None:
        params_dict["properties"] = properties
    if target is not None:
        params_dict["target"] = target
    if search_method is not None:
        params_dict["searchMethod"] = search_method

    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_input",
        params_dict,
    )

    if not isinstance(result, dict):
        return {"success": False, "message": str(result)}

    return result
