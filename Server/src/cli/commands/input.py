"""Input System CLI commands for managing Unity Input Actions."""

import click
from typing import Optional, Any

from cli.utils.config import get_config
from cli.utils.output import format_output, print_error, print_success
from cli.utils.connection import run_command, handle_unity_errors
from cli.utils.parsers import parse_json_dict_or_exit
from cli.utils.constants import SEARCH_METHOD_CHOICE_BASIC


_INPUT_TOP_LEVEL_KEYS = {"action", "assetPath", "mapName", "actionName", "target", "searchMethod", "properties"}


def _normalize_input_params(params: dict[str, Any]) -> dict[str, Any]:
    params = dict(params)
    properties: dict[str, Any] = {}
    for key in list(params.keys()):
        if key in _INPUT_TOP_LEVEL_KEYS:
            continue
        properties[key] = params.pop(key)

    if properties:
        existing = params.get("properties")
        if isinstance(existing, dict):
            params["properties"] = {**properties, **existing}
        else:
            params["properties"] = properties

    return {k: v for k, v in params.items() if v is not None}


@click.group()
def input():
    """Input System operations - create and configure input actions."""
    pass


# =============================================================================
# Detect
# =============================================================================

@input.command("ping")
@handle_unity_errors
def ping():
    """Check if Input System package is available.

    \b
    Examples:
        unity-mcp input ping
    """
    config = get_config()
    result = run_command(config, "manage_input", {"action": "ping"})
    format_output(result, config)


@input.command("info")
@handle_unity_errors
def info():
    """Get Input Actions asset info and summary.

    \b
    Examples:
        unity-mcp input info
    """
    config = get_config()
    result = run_command(config, "manage_input", {"action": "get_input_actions_info"})
    format_output(result, config)


# =============================================================================
# Asset
# =============================================================================

@input.command("create")
@click.option("--path", "-p", required=True, help="Asset path for the new InputActionAsset.")
@click.option("--name", "-n", default=None, help="Display name for the asset.")
@handle_unity_errors
def create(path, name):
    """Create a new InputActionAsset.

    \b
    Examples:
        unity-mcp input create --path "Assets/Input/PlayerControls.inputactions"
        unity-mcp input create --path "Assets/Input/Controls.inputactions" --name "Player Controls"
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "create_input_actions",
        "assetPath": path,
    }
    if name:
        params["properties"] = {"name": name}

    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("add-map")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--map", "-m", required=True, help="Name for the new action map.")
@handle_unity_errors
def add_map(path, map):
    """Add an action map to an InputActionAsset.

    \b
    Examples:
        unity-mcp input add-map --path "Assets/Input/Controls.inputactions" --map "Gameplay"
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "add_action_map",
        "assetPath": path,
        "mapName": map,
    }
    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("remove-map")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--map", "-m", required=True, help="Name of the action map to remove.")
@handle_unity_errors
def remove_map(path, map):
    """Remove an action map from an InputActionAsset.

    \b
    Examples:
        unity-mcp input remove-map --path "Assets/Input/Controls.inputactions" --map "Gameplay"
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "remove_action_map",
        "assetPath": path,
        "mapName": map,
    }
    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("add-action")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--map", "-m", required=True, help="Action map name.")
@click.option("--name", "-n", required=True, help="Name for the new action.")
@click.option("--type", "-t", "action_type", default=None,
              type=click.Choice(["Button", "Value", "PassThrough"]),
              help="Action type.")
@click.option("--control-type", default=None, help="Expected control type (e.g. Vector2, Stick).")
@handle_unity_errors
def add_action(path, map, name, action_type, control_type):
    """Add an action to an action map.

    \b
    Examples:
        unity-mcp input add-action --path "Assets/Input/Controls.inputactions" --map "Gameplay" --name "Move" --type Value --control-type Vector2
        unity-mcp input add-action --path "Assets/Input/Controls.inputactions" --map "Gameplay" --name "Jump" --type Button
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "add_action",
        "assetPath": path,
        "mapName": map,
        "actionName": name,
    }
    props: dict[str, Any] = {}
    if action_type:
        props["type"] = action_type
    if control_type:
        props["expectedControlType"] = control_type
    if props:
        params["properties"] = props

    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("remove-action")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--map", "-m", required=True, help="Action map name.")
@click.option("--name", "-n", required=True, help="Name of the action to remove.")
@handle_unity_errors
def remove_action(path, map, name):
    """Remove an action from an action map.

    \b
    Examples:
        unity-mcp input remove-action --path "Assets/Input/Controls.inputactions" --map "Gameplay" --name "Jump"
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "remove_action",
        "assetPath": path,
        "mapName": map,
        "actionName": name,
    }
    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("set-action-props")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--map", "-m", required=True, help="Action map name.")
@click.option("--name", "-n", required=True, help="Action name.")
@click.option("--props", required=True, help="Action properties as JSON string.")
@handle_unity_errors
def set_action_props(path, map, name, props):
    """Set properties on an existing action.

    \b
    Examples:
        unity-mcp input set-action-props --path "Assets/Input/Controls.inputactions" --map "Gameplay" --name "Move" --props '{"type": "Value", "expectedControlType": "Vector2"}'
    """
    config = get_config()
    properties = parse_json_dict_or_exit(props)
    params: dict[str, Any] = {
        "action": "set_action_properties",
        "assetPath": path,
        "mapName": map,
        "actionName": name,
        "properties": properties,
    }
    result = run_command(config, "manage_input", params)
    format_output(result, config)


# =============================================================================
# Binding
# =============================================================================

@input.command("add-binding")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--map", "-m", required=True, help="Action map name.")
@click.option("--name", "-n", required=True, help="Action name to bind to.")
@click.option("--binding-path", required=True, help="Input binding path (e.g. '<Keyboard>/space').")
@click.option("--groups", default=None, help="Binding groups (e.g. 'Keyboard&Mouse').")
@click.option("--interactions", default=None, help="Interactions (e.g. 'hold(duration=0.5)').")
@click.option("--processors", default=None, help="Processors (e.g. 'normalize').")
@handle_unity_errors
def add_binding(path, map, name, binding_path, groups, interactions, processors):
    """Add a binding to an action.

    \b
    Examples:
        unity-mcp input add-binding --path "Assets/Input/Controls.inputactions" --map "Gameplay" --name "Jump" --binding-path "<Keyboard>/space"
        unity-mcp input add-binding --path "Assets/Input/Controls.inputactions" --map "Gameplay" --name "Jump" --binding-path "<Gamepad>/buttonSouth" --groups "Gamepad"
    """
    config = get_config()
    props: dict[str, Any] = {
        "bindingPath": binding_path,
    }
    if groups:
        props["groups"] = groups
    if interactions:
        props["interactions"] = interactions
    if processors:
        props["processors"] = processors

    params: dict[str, Any] = {
        "action": "add_binding",
        "assetPath": path,
        "mapName": map,
        "actionName": name,
        "properties": props,
    }
    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("add-composite")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--map", "-m", required=True, help="Action map name.")
@click.option("--name", "-n", required=True, help="Action name to bind to.")
@click.option("--composite-type", required=True, help="Composite type (e.g. '2DVector', '1DAxis').")
@click.option("--parts", required=True, help="Composite parts as JSON (e.g. '{\"up\": \"<Keyboard>/w\", \"down\": \"<Keyboard>/s\"}').")
@handle_unity_errors
def add_composite(path, map, name, composite_type, parts):
    """Add a composite binding to an action.

    \b
    Examples:
        unity-mcp input add-composite --path "Assets/Input/Controls.inputactions" --map "Gameplay" --name "Move" --composite-type "2DVector" --parts '{"up": "<Keyboard>/w", "down": "<Keyboard>/s", "left": "<Keyboard>/a", "right": "<Keyboard>/d"}'
    """
    config = get_config()
    parts_dict = parse_json_dict_or_exit(parts)
    params: dict[str, Any] = {
        "action": "add_composite_binding",
        "assetPath": path,
        "mapName": map,
        "actionName": name,
        "properties": {
            "compositeType": composite_type,
            "parts": parts_dict,
        },
    }
    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("remove-binding")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--map", "-m", required=True, help="Action map name.")
@click.option("--name", "-n", required=True, help="Action name.")
@click.option("--index", required=True, type=int, help="Binding index to remove.")
@handle_unity_errors
def remove_binding(path, map, name, index):
    """Remove a binding from an action by index.

    \b
    Examples:
        unity-mcp input remove-binding --path "Assets/Input/Controls.inputactions" --map "Gameplay" --name "Jump" --index 0
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "remove_binding",
        "assetPath": path,
        "mapName": map,
        "actionName": name,
        "properties": {
            "index": index,
        },
    }
    result = run_command(config, "manage_input", params)
    format_output(result, config)


# =============================================================================
# Config
# =============================================================================

@input.command("add-scheme")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--scheme-name", required=True, help="Name for the control scheme.")
@click.option("--devices", required=True, help="Device requirements as JSON list (e.g. '[{\"controlPath\": \"<Keyboard>\", \"isOptional\": false}]').")
@handle_unity_errors
def add_scheme(path, scheme_name, devices):
    """Add a control scheme to an InputActionAsset.

    \b
    Examples:
        unity-mcp input add-scheme --path "Assets/Input/Controls.inputactions" --scheme-name "Keyboard&Mouse" --devices '[{"controlPath": "<Keyboard>", "isOptional": false}, {"controlPath": "<Mouse>", "isOptional": false}]'
    """
    config = get_config()
    import json
    try:
        devices_list = json.loads(devices)
    except json.JSONDecodeError as e:
        print_error(f"Invalid JSON for --devices: {e}")
        raise SystemExit(1)

    params: dict[str, Any] = {
        "action": "add_control_scheme",
        "assetPath": path,
        "properties": {
            "schemeName": scheme_name,
            "devices": devices_list,
        },
    }
    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("remove-scheme")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--scheme-name", required=True, help="Name of the control scheme to remove.")
@handle_unity_errors
def remove_scheme(path, scheme_name):
    """Remove a control scheme from an InputActionAsset.

    \b
    Examples:
        unity-mcp input remove-scheme --path "Assets/Input/Controls.inputactions" --scheme-name "Keyboard&Mouse"
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "remove_control_scheme",
        "assetPath": path,
        "properties": {
            "schemeName": scheme_name,
        },
    }
    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("setup-player-input")
@click.option("--target", "-t", required=True, help="GameObject name to attach PlayerInput to.")
@click.option("--path", "-p", required=True, help="Asset path of the InputActionAsset.")
@click.option("--default-map", default=None, help="Default action map name.")
@click.option("--search-method", "-s", type=SEARCH_METHOD_CHOICE_BASIC, default=None,
              help="Search method for finding the target GameObject.")
@handle_unity_errors
def setup_player_input(target, path, default_map, search_method):
    """Set up PlayerInput component on a GameObject.

    \b
    Examples:
        unity-mcp input setup-player-input --target "Player" --path "Assets/Input/Controls.inputactions"
        unity-mcp input setup-player-input --target "Player" --path "Assets/Input/Controls.inputactions" --default-map "Gameplay"
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "setup_player_input",
        "target": target,
        "assetPath": path,
    }
    if default_map:
        params["properties"] = {"defaultMap": default_map}
    if search_method:
        params["searchMethod"] = search_method

    result = run_command(config, "manage_input", params)
    format_output(result, config)


@input.command("preset")
@click.option("--preset-type", "-t", required=True,
              type=click.Choice(["fps", "third_person", "platformer", "vehicle", "ui", "rts"]),
              help="Preset type to create.")
@click.option("--path", "-p", default=None, help="Asset path for the preset (optional).")
@handle_unity_errors
def preset(preset_type, path):
    """Create a preset InputActionAsset with common bindings.

    \b
    Examples:
        unity-mcp input preset --preset-type fps
        unity-mcp input preset --preset-type platformer --path "Assets/Input/Platformer.inputactions"
    """
    config = get_config()
    params: dict[str, Any] = {
        "action": "create_preset",
        "properties": {
            "presetType": preset_type,
        },
    }
    if path:
        params["assetPath"] = path

    result = run_command(config, "manage_input", params)
    format_output(result, config)
