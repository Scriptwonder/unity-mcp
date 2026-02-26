from typing import Annotated, Literal, Any

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.utils import coerce_int
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry
from services.tools.preflight import preflight


@mcp_for_unity_tool(
    description=(
        "Simulates player input during Play Mode for AI game testing. "
        "Use to have an AI agent 'play' a generated game to validate it works. "
        "Actions: send_key (simulate keyboard), send_mouse_click (click at screen position), "
        "send_mouse_move (mouse delta for look/aim), send_sequence (timed input sequence), "
        "get_state (read game state snapshot including player position, UI elements, etc.). "
        "Requires Play Mode - use manage_editor action='play' first."
    ),
    annotations=ToolAnnotations(
        title="Simulate Input",
        destructiveHint=True,
    ),
)
async def simulate_input(
    ctx: Context,
    action: Annotated[Literal[
        "send_key",
        "send_mouse_click",
        "send_mouse_move",
        "send_sequence",
        "get_state",
    ], "Input simulation action to perform."],
    # --- send_key params ---
    key: Annotated[str,
                   "Key to simulate (e.g., 'W', 'Space', 'Mouse0', 'LeftShift'). Uses Unity KeyCode names."] | None = None,
    hold_duration: Annotated[int | str,
                             "How long to hold the key in milliseconds (default 100)."] | None = None,
    press: Annotated[bool,
                     "Whether to simulate key press (default true)."] | None = None,
    release: Annotated[bool,
                       "Whether to simulate key release (default true)."] | None = None,
    # --- send_mouse_click params ---
    x: Annotated[int | str,
                 "Screen X position for mouse click."] | None = None,
    y: Annotated[int | str,
                 "Screen Y position for mouse click."] | None = None,
    button: Annotated[int | str,
                      "Mouse button (0=left, 1=right, 2=middle). Default 0."] | None = None,
    # --- send_mouse_move params ---
    delta_x: Annotated[float | str,
                       "Mouse X delta for mouse move."] | None = None,
    delta_y: Annotated[float | str,
                       "Mouse Y delta for mouse move."] | None = None,
    # --- send_sequence params ---
    steps: Annotated[list[dict],
                     "Array of input steps for send_sequence. Each step: {action, key?, x?, y?, delta_x?, delta_y?, button?, duration_ms}."] | None = None,
    # --- get_state params ---
    query: Annotated[str,
                     "State query type for get_state: 'default' (player + camera + UI), 'ui' (focus on UI elements)."] | None = None,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    gate = await preflight(ctx, wait_for_no_compile=True, refresh_if_dirty=False)
    if gate is not None:
        return gate.model_dump()
    try:
        params: dict[str, Any] = {"action": action}

        if key is not None:
            params["key"] = key
        coerced_hold = coerce_int(hold_duration, default=None)
        if coerced_hold is not None:
            params["holdDuration"] = coerced_hold
        if press is not None:
            params["press"] = press
        if release is not None:
            params["release"] = release

        coerced_x = coerce_int(x, default=None)
        if coerced_x is not None:
            params["x"] = coerced_x
        coerced_y = coerce_int(y, default=None)
        if coerced_y is not None:
            params["y"] = coerced_y
        coerced_button = coerce_int(button, default=None)
        if coerced_button is not None:
            params["button"] = coerced_button

        if delta_x is not None:
            params["deltaX"] = float(delta_x)
        if delta_y is not None:
            params["deltaY"] = float(delta_y)

        if steps is not None:
            params["steps"] = steps

        if query is not None:
            params["query"] = query

        response = await send_with_unity_instance(async_send_command_with_retry, unity_instance, "simulate_input", params)

        if isinstance(response, dict) and response.get("success"):
            return {
                "success": True,
                "message": response.get("message", "Input simulation complete."),
                "data": response.get("data"),
            }
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}

    except Exception as e:
        return {"success": False, "message": f"Python error simulating input: {str(e)}"}
