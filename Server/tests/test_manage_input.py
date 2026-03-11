from __future__ import annotations

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.tools.manage_input import (
    manage_input,
    ALL_ACTIONS,
    DETECT_ACTIONS,
    ASSET_ACTIONS,
    BINDING_ACTIONS,
    CONFIG_ACTIONS,
)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture
def mock_unity(monkeypatch):
    """Patch Unity transport layer and return captured call dict."""
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.tools.manage_input.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_input.send_with_unity_instance",
        fake_send,
    )
    return captured


# ---------------------------------------------------------------------------
# Action list completeness
# ---------------------------------------------------------------------------

def test_all_actions_is_union_of_sub_lists():
    expected = set(DETECT_ACTIONS + ASSET_ACTIONS + BINDING_ACTIONS + CONFIG_ACTIONS)
    assert set(ALL_ACTIONS) == expected


def test_no_duplicate_actions():
    assert len(ALL_ACTIONS) == len(set(ALL_ACTIONS))


def test_all_actions_count():
    assert len(ALL_ACTIONS) == 15


# ---------------------------------------------------------------------------
# Invalid / missing action
# ---------------------------------------------------------------------------

def test_unknown_action_returns_error(mock_unity):
    result = asyncio.run(
        manage_input(SimpleNamespace(), action="nonexistent")
    )
    assert result["success"] is False
    assert "Unknown action" in result["message"]
    assert "tool_name" not in mock_unity


def test_empty_action_returns_error(mock_unity):
    result = asyncio.run(
        manage_input(SimpleNamespace(), action="")
    )
    assert result["success"] is False


# ---------------------------------------------------------------------------
# Detect actions
# ---------------------------------------------------------------------------

def test_ping_sends_correct_params(mock_unity):
    result = asyncio.run(
        manage_input(SimpleNamespace(), action="ping")
    )
    assert result["success"] is True
    assert mock_unity["tool_name"] == "manage_input"
    assert mock_unity["params"]["action"] == "ping"


def test_get_input_actions_info_sends_correct_params(mock_unity):
    result = asyncio.run(
        manage_input(SimpleNamespace(), action="get_input_actions_info")
    )
    assert result["success"] is True
    assert mock_unity["tool_name"] == "manage_input"
    assert mock_unity["params"]["action"] == "get_input_actions_info"


# ---------------------------------------------------------------------------
# Asset actions
# ---------------------------------------------------------------------------

def test_create_input_actions_sends_asset_path(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="create_input_actions",
            asset_path="Assets/Input.inputactions",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["assetPath"] == "Assets/Input.inputactions"


def test_add_action_map_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_action_map",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["assetPath"] == "Assets/Input.inputactions"
    assert mock_unity["params"]["mapName"] == "Gameplay"


def test_remove_action_map_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="remove_action_map",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["assetPath"] == "Assets/Input.inputactions"
    assert mock_unity["params"]["mapName"] == "Gameplay"


def test_add_action_sends_all_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_action",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
            action_name="Fire",
            properties={"type": "Button"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["assetPath"] == "Assets/Input.inputactions"
    assert mock_unity["params"]["mapName"] == "Gameplay"
    assert mock_unity["params"]["actionName"] == "Fire"
    assert mock_unity["params"]["properties"]["type"] == "Button"


def test_remove_action_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="remove_action",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
            action_name="Fire",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["assetPath"] == "Assets/Input.inputactions"
    assert mock_unity["params"]["mapName"] == "Gameplay"
    assert mock_unity["params"]["actionName"] == "Fire"


def test_set_action_properties_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="set_action_properties",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
            action_name="Move",
            properties={"expectedControlType": "Vector2"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["actionName"] == "Move"
    assert mock_unity["params"]["properties"]["expectedControlType"] == "Vector2"


def test_create_input_actions_with_properties(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="create_input_actions",
            asset_path="Assets/Input.inputactions",
            properties={"name": "PlayerControls"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["assetPath"] == "Assets/Input.inputactions"
    assert mock_unity["params"]["properties"]["name"] == "PlayerControls"


def test_add_action_map_minimal(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_action_map",
            asset_path="Assets/Input.inputactions",
            map_name="UI",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["assetPath"] == "Assets/Input.inputactions"
    assert mock_unity["params"]["mapName"] == "UI"


# ---------------------------------------------------------------------------
# Binding actions
# ---------------------------------------------------------------------------

def test_add_binding_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_binding",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
            action_name="Fire",
            properties={"path": "<Keyboard>/space", "groups": "Keyboard&Mouse"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["mapName"] == "Gameplay"
    assert mock_unity["params"]["actionName"] == "Fire"
    assert mock_unity["params"]["properties"]["path"] == "<Keyboard>/space"
    assert mock_unity["params"]["properties"]["groups"] == "Keyboard&Mouse"


def test_add_composite_binding_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_composite_binding",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
            action_name="Move",
            properties={
                "compositeType": "2DVector",
                "parts": {
                    "up": "<Keyboard>/w",
                    "down": "<Keyboard>/s",
                    "left": "<Keyboard>/a",
                    "right": "<Keyboard>/d",
                },
            },
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["actionName"] == "Move"
    props = mock_unity["params"]["properties"]
    assert props["compositeType"] == "2DVector"
    assert props["parts"]["up"] == "<Keyboard>/w"
    assert props["parts"]["right"] == "<Keyboard>/d"


def test_remove_binding_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="remove_binding",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
            action_name="Fire",
            properties={"index": 0},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["actionName"] == "Fire"
    assert mock_unity["params"]["properties"]["index"] == 0


def test_add_binding_with_interactions(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_binding",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
            action_name="Fire",
            properties={"path": "<Keyboard>/space", "interactions": "hold(duration=0.5)"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["properties"]["interactions"] == "hold(duration=0.5)"


def test_add_binding_with_processors(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_binding",
            asset_path="Assets/Input.inputactions",
            map_name="Gameplay",
            action_name="Move",
            properties={"path": "<Gamepad>/leftStick", "processors": "stickDeadzone(min=0.125)"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["properties"]["processors"] == "stickDeadzone(min=0.125)"


# ---------------------------------------------------------------------------
# Config actions
# ---------------------------------------------------------------------------

def test_add_control_scheme_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_control_scheme",
            asset_path="Assets/Input.inputactions",
            properties={
                "schemeName": "Keyboard&Mouse",
                "devices": [
                    {"devicePath": "<Keyboard>", "required": True},
                    {"devicePath": "<Mouse>", "required": True},
                ],
            },
        )
    )
    assert result["success"] is True
    props = mock_unity["params"]["properties"]
    assert props["schemeName"] == "Keyboard&Mouse"
    assert len(props["devices"]) == 2
    assert props["devices"][0]["devicePath"] == "<Keyboard>"
    assert props["devices"][0]["required"] is True


def test_remove_control_scheme_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="remove_control_scheme",
            asset_path="Assets/Input.inputactions",
            properties={"schemeName": "Keyboard&Mouse"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["properties"]["schemeName"] == "Keyboard&Mouse"


def test_setup_player_input_sends_params(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="setup_player_input",
            target="Player",
            asset_path="Assets/Input.inputactions",
            properties={"defaultMap": "Gameplay"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["target"] == "Player"
    assert mock_unity["params"]["assetPath"] == "Assets/Input.inputactions"
    assert mock_unity["params"]["properties"]["defaultMap"] == "Gameplay"


def test_setup_player_input_with_search_method(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="setup_player_input",
            target="Player",
            search_method="by_name",
            asset_path="Assets/Input.inputactions",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["target"] == "Player"
    assert mock_unity["params"]["searchMethod"] == "by_name"


def test_create_preset_fps(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="create_preset",
            properties={"preset": "fps"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "create_preset"
    assert mock_unity["params"]["properties"]["preset"] == "fps"


def test_create_preset_with_path(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="create_preset",
            asset_path="Assets/Custom.inputactions",
            properties={"preset": "platformer"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["assetPath"] == "Assets/Custom.inputactions"
    assert mock_unity["params"]["properties"]["preset"] == "platformer"


def test_create_preset_vehicle(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="create_preset",
            properties={"preset": "vehicle"},
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["properties"]["preset"] == "vehicle"


# ---------------------------------------------------------------------------
# Parameter handling
# ---------------------------------------------------------------------------

def test_asset_path_sent_as_camelcase(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="create_input_actions",
            asset_path="Assets/Test.inputactions",
        )
    )
    assert result["success"] is True
    assert "assetPath" in mock_unity["params"]
    assert "asset_path" not in mock_unity["params"]


def test_map_name_sent_as_camelcase(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_action_map",
            asset_path="Assets/Test.inputactions",
            map_name="Gameplay",
        )
    )
    assert result["success"] is True
    assert "mapName" in mock_unity["params"]
    assert "map_name" not in mock_unity["params"]


def test_action_name_sent_as_camelcase(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="add_action",
            asset_path="Assets/Test.inputactions",
            map_name="Gameplay",
            action_name="Jump",
        )
    )
    assert result["success"] is True
    assert "actionName" in mock_unity["params"]
    assert "action_name" not in mock_unity["params"]


def test_search_method_passed_through(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="setup_player_input",
            target="12345",
            search_method="by_id",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["searchMethod"] == "by_id"


def test_none_params_omitted(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="ping",
            asset_path=None,
            map_name=None,
            action_name=None,
            properties=None,
            target=None,
            search_method=None,
        )
    )
    assert result["success"] is True
    assert "assetPath" not in mock_unity["params"]
    assert "mapName" not in mock_unity["params"]
    assert "actionName" not in mock_unity["params"]
    assert "properties" not in mock_unity["params"]
    assert "target" not in mock_unity["params"]
    assert "searchMethod" not in mock_unity["params"]


def test_string_properties_passed_through(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="create_input_actions",
            asset_path="Assets/Test.inputactions",
            properties='{"name": "PlayerControls"}',
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["properties"] == '{"name": "PlayerControls"}'


def test_target_passed_through(mock_unity):
    result = asyncio.run(
        manage_input(
            SimpleNamespace(),
            action="setup_player_input",
            target="Player",
        )
    )
    assert result["success"] is True
    assert mock_unity["params"]["target"] == "Player"


def test_action_case_insensitive(mock_unity):
    result = asyncio.run(
        manage_input(SimpleNamespace(), action="Create_Input_Actions")
    )
    assert result["success"] is True
    assert mock_unity["params"]["action"] == "create_input_actions"


# ---------------------------------------------------------------------------
# Non-dict response
# ---------------------------------------------------------------------------

def test_non_dict_response_wrapped(monkeypatch):
    """When Unity returns a non-dict, it should be wrapped."""
    monkeypatch.setattr(
        "services.tools.manage_input.get_unity_instance_from_context",
        AsyncMock(return_value="unity-1"),
    )

    async def fake_send(send_fn, unity_instance, tool_name, params):
        return "unexpected string response"

    monkeypatch.setattr(
        "services.tools.manage_input.send_with_unity_instance",
        fake_send,
    )

    result = asyncio.run(
        manage_input(SimpleNamespace(), action="ping")
    )
    assert result["success"] is False
    assert "unexpected string response" in result["message"]


# ---------------------------------------------------------------------------
# All valid actions forward correctly
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("action_name", ALL_ACTIONS)
def test_all_actions_forward_to_unity(mock_unity, action_name):
    result = asyncio.run(
        manage_input(SimpleNamespace(), action=action_name)
    )
    assert result["success"] is True
    assert mock_unity["tool_name"] == "manage_input"
    assert mock_unity["params"]["action"] == action_name
