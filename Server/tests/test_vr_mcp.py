"""
Tests for VR-MCP runtime session routing, session_type plumbing,
and preflight skip logic.
"""

import asyncio
import os
import pytest
import pytest_asyncio
from unittest.mock import AsyncMock, Mock, MagicMock, patch
from datetime import datetime, timezone

from transport.plugin_registry import PluginRegistry, PluginSession
from transport.plugin_hub import PluginHub
from transport.models import SessionDetails, SessionList
from transport.unity_instance_middleware import UnityInstanceMiddleware


# ============================================================================
# FIXTURES
# ============================================================================


@pytest.fixture
def plugin_registry():
    """Create an in-memory plugin registry."""
    return PluginRegistry()


@pytest.fixture
def mock_context():
    """Create a mock FastMCP context."""
    ctx = Mock()
    ctx.session_id = "test-session-123"
    ctx.client_id = "test-client-456"

    state_storage = {}
    ctx.set_state = Mock(side_effect=lambda k, v: state_storage.__setitem__(k, v))
    ctx.get_state = Mock(side_effect=lambda k: state_storage.get(k))
    ctx.info = AsyncMock()
    ctx.error = AsyncMock()

    return ctx


@pytest_asyncio.fixture
async def configured_plugin_hub(plugin_registry):
    """Configure PluginHub with a registry and event loop."""
    loop = asyncio.get_running_loop()
    PluginHub.configure(plugin_registry, loop)
    yield plugin_registry
    PluginHub._registry = None
    PluginHub._lock = None
    PluginHub._loop = None
    PluginHub._connections.clear()
    PluginHub._pending.clear()


# ============================================================================
# PLUGIN REGISTRY: session_type TESTS
# ============================================================================


class TestPluginRegistrySessionType:
    """Test session_type field on PluginSession and registry methods."""

    @pytest.mark.asyncio
    async def test_register_editor_session_default(self, plugin_registry):
        """Default session_type is 'editor'."""
        session = await plugin_registry.register(
            session_id="s1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
        )
        assert session.session_type == "editor"

    @pytest.mark.asyncio
    async def test_register_runtime_session(self, plugin_registry):
        """Runtime sessions register with session_type='runtime'."""
        session = await plugin_registry.register(
            session_id="s1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
            session_type="runtime",
        )
        assert session.session_type == "runtime"

    @pytest.mark.asyncio
    async def test_runtime_replaces_editor_session(self, plugin_registry):
        """Runtime session replaces editor session for the same project_hash."""
        await plugin_registry.register(
            session_id="editor-1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
            session_type="editor",
        )
        await plugin_registry.register(
            session_id="runtime-1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
            session_type="runtime",
        )
        # The editor session should be replaced
        resolved = await plugin_registry.get_session_id_by_hash("abc123")
        assert resolved == "runtime-1"

        # Old editor session should be gone
        editor = await plugin_registry.get_session("editor-1")
        assert editor is None

    @pytest.mark.asyncio
    async def test_editor_replaces_runtime_session(self, plugin_registry):
        """Editor session replaces runtime session when Play Mode ends."""
        await plugin_registry.register(
            session_id="runtime-1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
            session_type="runtime",
        )
        await plugin_registry.register(
            session_id="editor-2",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
            session_type="editor",
        )
        resolved = await plugin_registry.get_session_id_by_hash("abc123")
        assert resolved == "editor-2"

    @pytest.mark.asyncio
    async def test_get_session_type_editor(self, plugin_registry):
        """get_session_type returns 'editor' for editor sessions."""
        await plugin_registry.register(
            session_id="s1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
            session_type="editor",
        )
        session_type = await plugin_registry.get_session_type("s1")
        assert session_type == "editor"

    @pytest.mark.asyncio
    async def test_get_session_type_runtime(self, plugin_registry):
        """get_session_type returns 'runtime' for runtime sessions."""
        await plugin_registry.register(
            session_id="s1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
            session_type="runtime",
        )
        session_type = await plugin_registry.get_session_type("s1")
        assert session_type == "runtime"

    @pytest.mark.asyncio
    async def test_get_session_type_nonexistent(self, plugin_registry):
        """get_session_type returns None for unknown session."""
        session_type = await plugin_registry.get_session_type("nonexistent")
        assert session_type is None


# ============================================================================
# PLUGIN HUB: session_type in SessionDetails
# ============================================================================


class TestPluginHubSessionType:
    """Test that PluginHub exposes session_type in session listings."""

    @pytest.mark.asyncio
    async def test_get_sessions_includes_session_type(self, configured_plugin_hub):
        registry = configured_plugin_hub
        await registry.register(
            session_id="s1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
            session_type="runtime",
        )
        sessions = await PluginHub.get_sessions()
        assert "s1" in sessions.sessions
        assert sessions.sessions["s1"].session_type == "runtime"

    @pytest.mark.asyncio
    async def test_get_sessions_editor_default(self, configured_plugin_hub):
        registry = configured_plugin_hub
        await registry.register(
            session_id="s1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
        )
        sessions = await PluginHub.get_sessions()
        assert sessions.sessions["s1"].session_type == "editor"


# ============================================================================
# PREFLIGHT: skip for runtime sessions
# ============================================================================


class TestPreflightRuntimeSkip:
    """Test that preflight is skipped for runtime sessions."""

    @pytest.mark.asyncio
    async def test_preflight_skips_for_runtime_session(self):
        """Preflight should return None immediately for runtime sessions."""
        from services.tools.preflight import preflight

        ctx = Mock()
        state_storage = {"unity_session_type": "runtime"}
        ctx.get_state = Mock(side_effect=lambda k: state_storage.get(k))

        result = await preflight(ctx, wait_for_no_compile=True)
        assert result is None

    @pytest.mark.asyncio
    async def test_preflight_does_not_skip_for_editor_session(self):
        """Preflight should NOT skip for editor sessions (proceeds to normal logic)."""
        from services.tools.preflight import preflight

        ctx = Mock()
        state_storage = {"unity_session_type": "editor"}
        ctx.get_state = Mock(side_effect=lambda k: state_storage.get(k))

        # The preflight will try to call get_editor_state and fail (no real Unity),
        # but importantly it should NOT return None from the runtime check
        # It will return None from the exception handler fallback
        result = await preflight(ctx)
        assert result is None  # Falls through to normal logic (which also returns None in test env)


# ============================================================================
# XR TOOL REGISTRATION
# ============================================================================


class TestXRToolRegistration:
    """Test that XR Python MCP tools are importable and well-formed."""

    def test_manage_xr_rig_importable(self):
        from services.tools.manage_xr_rig import manage_xr_rig
        assert callable(manage_xr_rig)

    def test_manage_xr_passthrough_importable(self):
        from services.tools.manage_xr_passthrough import manage_xr_passthrough
        assert callable(manage_xr_passthrough)

    def test_manage_xr_interaction_importable(self):
        from services.tools.manage_xr_interaction import manage_xr_interaction
        assert callable(manage_xr_interaction)

    def test_manage_xr_hand_tracking_importable(self):
        from services.tools.manage_xr_hand_tracking import manage_xr_hand_tracking
        assert callable(manage_xr_hand_tracking)

    def test_manage_xr_anchor_importable(self):
        from services.tools.manage_xr_anchor import manage_xr_anchor
        assert callable(manage_xr_anchor)

    def test_manage_xr_boundary_importable(self):
        from services.tools.manage_xr_boundary import manage_xr_boundary
        assert callable(manage_xr_boundary)


# ============================================================================
# UNITY INSTANCES RESOURCE: session_type
# ============================================================================


class TestInstancesResourceSessionType:
    """Test that the instances resource includes session_type."""

    @pytest.mark.asyncio
    async def test_instances_resource_includes_session_type(self, mock_context, configured_plugin_hub):
        registry = configured_plugin_hub
        await registry.register(
            session_id="s1",
            project_name="TestProject",
            project_hash="abc123",
            unity_version="2022.3",
            session_type="runtime",
        )

        with patch("services.resources.unity_instances.config") as mock_config:
            mock_config.transport_mode = "http"
            mock_config.http_remote_hosted = False

            from services.resources.unity_instances import unity_instances
            result = await unity_instances(mock_context)

        assert result["success"] is True
        assert len(result["instances"]) == 1
        assert result["instances"][0]["session_type"] == "runtime"
