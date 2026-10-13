import inspect
from unittest.mock import Mock

import pytest

from models.models import ToolDefinitionModel, ToolParameterModel
from services.custom_tool_service import CustomToolService


@pytest.mark.parametrize("optional_first", [True, False])
def test_custom_tool_registration_preserves_required_and_optional_parameters(
    monkeypatch, optional_first
):
    monkeypatch.setattr(CustomToolService, "_instance", None)
    mcp = Mock()
    registered = {}

    def register(**kwargs):
        def decorator(handler):
            registered[kwargs["name"]] = handler
            return handler
        return decorator

    mcp.tool.side_effect = register
    service = CustomToolService(mcp)
    optional = ToolParameterModel(
        name="limit", type="integer", required=False, default_value="10"
    )
    required = ToolParameterModel(name="action", required=True)
    parameters = [optional, required] if optional_first else [required, optional]
    definition = ToolDefinitionModel(name="ordered_tool", parameters=parameters)

    service.register_global_tools([definition])

    assert "ordered_tool" in registered
    signature = inspect.signature(registered["ordered_tool"])
    assert signature.parameters["action"].default is inspect.Parameter.empty
    assert signature.parameters["action"].annotation is str
    assert signature.parameters["limit"].annotation is int
    assert list(signature.parameters) == ["ctx"] + [p.name for p in parameters]
    bound = signature.bind(object(), action="list")
    bound.apply_defaults()
    assert bound.arguments["limit"] == 10
    with pytest.raises(TypeError, match="action"):
        signature.bind(object())
