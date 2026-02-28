"""Input simulation CLI commands."""

import sys
import json
import click
from typing import Optional, Any

from cli.utils.config import get_config
from cli.utils.output import format_output, print_error, print_success
from cli.utils.connection import run_command, handle_unity_errors


@click.group()
def input_sim():
    """Input simulation - send keys, mouse clicks, sequences during play mode."""
    pass


@input_sim.command("key")
@click.argument("key")
@click.option(
    "--hold", "-d",
    default=100,
    type=int,
    help="Hold duration in milliseconds (default: 100)."
)
@handle_unity_errors
def send_key(key: str, hold: int):
    """Simulate a key press.

    \b
    Examples:
        unity-mcp input key W --hold 1000
        unity-mcp input key Space
        unity-mcp input key Mouse0
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "send_key",
        "key": key,
        "holdDuration": hold,
    }
    result = run_command("simulate_input", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Key '{key}' simulated")


@input_sim.command("click")
@click.option("--x", "-x", default=0, type=int, help="Screen X position.")
@click.option("--y", "-y", default=0, type=int, help="Screen Y position.")
@click.option("--button", "-b", default=0, type=int, help="Mouse button (0=left, 1=right, 2=middle).")
@handle_unity_errors
def send_click(x: int, y: int, button: int):
    """Simulate a mouse click.

    \b
    Examples:
        unity-mcp input click --x 400 --y 300
        unity-mcp input click --x 100 --y 200 --button 1
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "send_mouse_click",
        "x": x,
        "y": y,
        "button": button,
    }
    result = run_command("simulate_input", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Mouse click at ({x}, {y})")


@input_sim.command("move")
@click.option("--dx", default=0, type=float, help="Mouse X delta.")
@click.option("--dy", default=0, type=float, help="Mouse Y delta.")
@handle_unity_errors
def send_move(dx: float, dy: float):
    """Simulate mouse movement.

    \b
    Examples:
        unity-mcp input move --dx 50 --dy 0
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "send_mouse_move",
        "deltaX": dx,
        "deltaY": dy,
    }
    result = run_command("simulate_input", params, config)
    click.echo(format_output(result, config.format))


@input_sim.command("sequence")
@click.argument("steps_json")
@handle_unity_errors
def send_sequence(steps_json: str):
    """Send a timed input sequence.

    STEPS_JSON is a JSON array of step objects.

    \b
    Examples:
        unity-mcp input sequence '[{"action":"key","key":"W","duration_ms":2000},{"action":"wait","duration_ms":500},{"action":"key","key":"Space","duration_ms":100}]'
    """
    config = get_config()
    try:
        steps = json.loads(steps_json)
    except json.JSONDecodeError as e:
        print_error(f"Invalid JSON: {e}")
        sys.exit(1)

    params: dict[str, Any] = {
        "action": "send_sequence",
        "steps": steps,
    }
    result = run_command("simulate_input", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        data = result.get("data", {})
        print_success(f"Queued {data.get('queued', '?')} input steps")


@input_sim.command("state")
@click.option(
    "--query", "-q",
    default="default",
    help="State query: 'default' or 'ui'."
)
@handle_unity_errors
def get_state(query: str):
    """Read game state snapshot.

    Returns player position, camera info, active UI elements, etc.

    \b
    Examples:
        unity-mcp input state
        unity-mcp input state --query ui
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "get_state",
        "query": query,
    }
    result = run_command("simulate_input", params, config)
    click.echo(format_output(result, config.format))
