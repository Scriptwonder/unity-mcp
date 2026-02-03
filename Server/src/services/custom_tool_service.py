import asyncio
import logging
import time

from pydantic import BaseModel, ValidationError
from starlette.requests import Request
from starlette.responses import JSONResponse

from models.models import MCPResponse, ToolDefinitionModel
from transport.plugin_hub import PluginHub
from utils.console import log_event

logger = logging.getLogger("mcp-for-unity-server")

_DEFAULT_POLL_INTERVAL = 1.0
_MAX_POLL_SECONDS = 600


class RegisterToolsPayload(BaseModel):
    project_id: str
    project_hash: str | None = None
    tools: list[ToolDefinitionModel]


class ToolRegistrationResponse(BaseModel):
    success: bool
    registered: list[str]
    replaced: list[str]
    message: str


class CustomToolService:
    _instance: "CustomToolService | None" = None

    def __init__(self, project_scoped_tools: bool = True):
        CustomToolService._instance = self
        self._project_scoped_tools = project_scoped_tools
        self._project_tools: dict[str, dict[str, ToolDefinitionModel]] = {}
        self._hash_to_project: dict[str, str] = {}

    @classmethod
    def get_instance(cls) -> "CustomToolService":
        if cls._instance is None:
            raise RuntimeError("CustomToolService has not been initialized")
        return cls._instance

    def register_routes(self, app) -> None:
        @app.post("/register-tools")
        async def register_tools(request: Request) -> JSONResponse:
            try:
                payload = RegisterToolsPayload.model_validate(await request.json())
            except ValidationError as exc:
                log_event(
                    logger,
                    logging.WARNING,
                    "TOOLS",
                    "Invalid registration payload",
                    error_count=len(exc.errors()),
                )
                return JSONResponse({"success": False, "error": exc.errors()}, status_code=400)

            registered, replaced = self._register_project_tools(
                payload.project_id,
                payload.tools,
                project_hash=payload.project_hash,
            )

            message = f"Registered {len(registered)} tool(s)"
            if replaced:
                message += f" (replaced: {', '.join(replaced)})"

            log_event(
                logger,
                logging.INFO,
                "TOOLS",
                "Registered",
                project=payload.project_id,
                hash=payload.project_hash,
                count=len(registered),
                replaced=len(replaced),
            )

            response = ToolRegistrationResponse(
                success=True,
                registered=registered,
                replaced=replaced,
                message=message,
            )
            return JSONResponse(response.model_dump())

    async def list_registered_tools(self, project_id: str) -> list[ToolDefinitionModel]:
        legacy = list(self._project_tools.get(project_id, {}).values())
        hub_tools = await PluginHub.get_tools_for_project(project_id)
        return legacy + hub_tools

    async def get_tool_definition(
        self,
        project_id: str,
        tool_name: str,
    ) -> ToolDefinitionModel | None:
        tool = self._project_tools.get(project_id, {}).get(tool_name)
        if tool:
            return tool
        return await PluginHub.get_tool_definition(project_id, tool_name)

    async def execute_tool(
        self,
        project_id: str,
        tool_name: str,
        unity_instance: str | None,
        params: dict[str, object] | None = None,
    ) -> MCPResponse:
        params = params or {}
        started_at = time.monotonic()
        param_keys = self._summarize_param_keys(params)
        log_event(
            logger,
            logging.INFO,
            "TOOLS",
            "Execute",
            tool=tool_name,
            project=project_id,
            instance=unity_instance or "auto",
            params=param_keys,
        )

        definition = await self.get_tool_definition(project_id, tool_name)
        if definition is None:
            log_event(
                logger,
                logging.WARNING,
                "TOOLS",
                "Tool not found",
                tool=tool_name,
                project=project_id,
            )
            return MCPResponse(
                success=False,
                message=f"Tool '{tool_name}' not found for project {project_id}",
            )

        response = await PluginHub.send_command_for_instance(
            unity_instance,
            tool_name,
            params,
        )

        if not definition.requires_polling:
            result = self._normalize_response(response)
            self._log_tool_result(tool_name, result, started_at)
            return result

        result = await self._poll_until_complete(
            tool_name,
            unity_instance,
            params,
            response,
            definition.poll_action or "status",
        )
        self._log_tool_result(tool_name, result, started_at)
        return result

    def _register_project_tools(
        self,
        project_id: str,
        tools: list[ToolDefinitionModel],
        project_hash: str | None = None,
    ) -> tuple[list[str], list[str]]:
        registered: list[str] = []
        replaced: list[str] = []
        for tool in tools:
            if tool.name in self._project_tools.get(project_id, {}):
                replaced.append(tool.name)
            self._project_tools.setdefault(project_id, {})[tool.name] = tool
            registered.append(tool.name)

        if project_hash:
            self._hash_to_project[project_hash.lower()] = project_id

        return registered, replaced

    def get_project_id_for_hash(self, project_hash: str | None) -> str | None:
        if not project_hash:
            return None
        return self._hash_to_project.get(project_hash.lower())

    async def _poll_until_complete(
        self,
        tool_name: str,
        unity_instance: str | None,
        initial_params: dict[str, object],
        initial_response,
        poll_action: str,
    ) -> MCPResponse:
        poll_params = dict(initial_params)
        poll_params["action"] = poll_action or "status"

        deadline = time.time() + _MAX_POLL_SECONDS
        response = initial_response

        while True:
            status, poll_interval = self._interpret_status(response)

            if status in ("complete", "error", "final"):
                return self._normalize_response(response)

            if time.time() > deadline:
                return MCPResponse(
                    success=False,
                    message=f"Timeout waiting for {tool_name} to complete",
                    data=self._safe_response(response),
                )

            await asyncio.sleep(poll_interval)

            try:
                response = await PluginHub.send_command_for_instance(
                    unity_instance,
                    tool_name,
                    poll_params,
                )
            except Exception as exc:  # pragma: no cover - network/domain reload variability
                logger.debug("Polling %s failed, will retry: %s", tool_name, exc)
                response = {
                    "_mcp_status": "pending",
                    "_mcp_poll_interval": min(max(poll_interval * 2, _DEFAULT_POLL_INTERVAL), 5.0),
                    "message": f"Retrying after transient error: {exc}",
                }

    def _interpret_status(self, response) -> tuple[str, float]:
        if response is None:
            return "pending", _DEFAULT_POLL_INTERVAL

        if not isinstance(response, dict):
            return "final", _DEFAULT_POLL_INTERVAL

        status = response.get("_mcp_status")
        if status is None:
            if len(response.keys()) == 0:
                return "pending", _DEFAULT_POLL_INTERVAL
            return "final", _DEFAULT_POLL_INTERVAL

        if status == "pending":
            interval_raw = response.get(
                "_mcp_poll_interval", _DEFAULT_POLL_INTERVAL)
            try:
                interval = float(interval_raw)
            except (TypeError, ValueError):
                interval = _DEFAULT_POLL_INTERVAL

            interval = max(0.1, min(interval, 5.0))
            return "pending", interval

        if status == "complete":
            return "complete", _DEFAULT_POLL_INTERVAL

        if status == "error":
            return "error", _DEFAULT_POLL_INTERVAL

        return "final", _DEFAULT_POLL_INTERVAL

    def _normalize_response(self, response) -> MCPResponse:
        if isinstance(response, MCPResponse):
            return response
        if isinstance(response, dict):
            return MCPResponse(
                success=response.get("success", True),
                message=response.get("message"),
                error=response.get("error"),
                data=response.get("data", response) if "data" not in response else response["data"],
                hint=response.get("hint"),
            )

        return MCPResponse(success=True, message=str(response))

    def _safe_response(self, response):
        if isinstance(response, dict):
            return response
        if response is None:
            return None
        return {"message": str(response)}

    def _summarize_param_keys(self, params: dict[str, object], limit: int = 6) -> str | None:
        keys = [str(key) for key in params.keys()] if isinstance(params, dict) else []
        if not keys:
            return None
        keys.sort()
        if len(keys) > limit:
            return ", ".join(keys[:limit]) + f", +{len(keys) - limit}"
        return ", ".join(keys)

    def _log_tool_result(self, tool_name: str, result: MCPResponse, started_at: float) -> None:
        elapsed_ms = (time.monotonic() - started_at) * 1000
        level = logging.INFO if result.success else logging.WARNING
        log_event(
            logger,
            level,
            "TOOLS",
            "Completed",
            tool=tool_name,
            success=result.success,
            error=result.error,
            message=result.message,
            hint=result.hint,
            elapsed=f"{elapsed_ms:.0f}ms",
        )


def resolve_project_id_for_unity_instance(unity_instance: str | None) -> str | None:
    if not unity_instance:
        return None

    if "@" in unity_instance:
        _, _, hash_part = unity_instance.partition("@")
        unity_instance = hash_part or unity_instance

    lowered = unity_instance.lower()
    try:
        service = CustomToolService.get_instance()
        mapped = service.get_project_id_for_hash(lowered)
        return mapped or lowered
    except RuntimeError:
        return lowered
