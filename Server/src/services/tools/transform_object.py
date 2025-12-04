"""
Defines the transform_object tool for transforming/replacing scene objects.
Supports finding existing assets or generating new ones via Trellis.
"""
import asyncio
from typing import Annotated, Any, Literal

from fastmcp import Context
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


@mcp_for_unity_tool(
    description="""Transforms/replaces a scene object with another model.
    
    Workflow:
    1. Finds the source object in scene by name/path
    2. Searches for existing assets matching target_name (substring match)
    3. If no asset found and generate_if_missing=True, generates via Trellis
    4. Instantiates the new model with scale fitted to original bounding box
    5. Disables the original object (preserves history for revert)
    
    Examples:
    - transform_object(source_object="Beehive", target_name="sprinkler")
    - transform_object(action="revert", target="sprinkler")
    - transform_object(action="list_history")"""
)
async def transform_object(
    ctx: Context,
    action: Annotated[
        Literal["transform", "status", "revert", "revert_original", "list_history"],
        """Action to perform:
        - transform: Replace source_object with target_name model
        - status: Check status of ongoing generation (polling)
        - revert: Revert target object to previous state
        - revert_original: Revert target to original state (full chain)
        - list_history: List all objects with transform history"""
    ] = "transform",
    source_object: Annotated[
        str,
        "Name or path of the scene object to transform/replace"
    ] | None = None,
    target_name: Annotated[
        str,
        "Name of what to transform into (e.g., 'sprinkler', 'tree'). Used to search existing assets and as Trellis prompt."
    ] | None = None,
    search_existing: Annotated[
        bool,
        "Whether to search for existing assets matching target_name before generating"
    ] = True,
    generate_if_missing: Annotated[
        bool,
        "Whether to generate a new model via Trellis if no existing asset found"
    ] = True,
    target: Annotated[
        str,
        "Target object name/path for revert actions"
    ] | None = None,
) -> dict[str, Any]:
    """Transform/replace a scene object with another model asset."""
    
    unity_instance = get_unity_instance_from_context(ctx)
    
    # Validate parameters based on action
    if action == "transform":
        if not source_object:
            return {
                "success": False,
                "message": "For 'transform' action, 'source_object' parameter is required."
            }
        if not target_name:
            return {
                "success": False,
                "message": "For 'transform' action, 'target_name' parameter is required."
            }
    elif action in ["revert", "revert_original"]:
        if not target:
            return {
                "success": False,
                "message": f"For '{action}' action, 'target' parameter is required."
            }

    # Prepare parameters for the C# handler
    params_dict = {
        "action": action,
        "source_object": source_object,
        "target_name": target_name,
        "search_existing": search_existing,
        "generate_if_missing": generate_if_missing,
        "target": target,
    }

    # Remove None values
    params_dict = {k: v for k, v in params_dict.items() if v is not None}

    # Get the current asyncio event loop
    loop = asyncio.get_running_loop()

    # Send command to Unity
    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "transform_object",
        params_dict,
        loop=loop
    )
    
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
