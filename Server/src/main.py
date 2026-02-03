import argparse
import asyncio
import itertools
import logging
import os
import sys
import time
from contextlib import asynccontextmanager
from urllib.parse import urlparse

import uvicorn
from fastapi import FastAPI
from starlette.requests import Request
from starlette.responses import JSONResponse

from core.config import config
from services.custom_tool_service import CustomToolService, resolve_project_id_for_unity_instance
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry
from utils.console import FriendlyFormatter, log_event, supports_color

logger = logging.getLogger("mcp-for-unity-server")
_REQUEST_COUNTER = itertools.count(1)


def _configure_logging() -> None:
    level_name = str(config.log_level).upper()
    level = getattr(logging, level_name, logging.INFO)
    handler = logging.StreamHandler(stream=sys.stdout)
    handler.setFormatter(FriendlyFormatter(use_color=supports_color()))
    root = logging.getLogger()
    root.handlers = [handler]
    root.setLevel(level)

    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        uvicorn_logger = logging.getLogger(name)
        uvicorn_logger.handlers = []
        uvicorn_logger.propagate = True
        uvicorn_logger.setLevel(level)


def _next_request_id() -> str:
    return f"r{next(_REQUEST_COUNTER)}"


def _log_request(request_id: str, method: str, path: str, **fields: object) -> None:
    log_event(logger, logging.INFO, "REQUEST", f"{method} {path}", id=request_id, **fields)


def _log_response(
    request_id: str,
    method: str,
    path: str,
    status_code: int,
    started_at: float,
    **fields: object,
) -> None:
    elapsed_ms = (time.monotonic() - started_at) * 1000
    if status_code >= 500:
        level = logging.ERROR
    elif status_code >= 400:
        level = logging.WARNING
    else:
        level = logging.INFO
    log_event(
        logger,
        level,
        "RESPONSE",
        f"{method} {path}",
        id=request_id,
        status=status_code,
        elapsed=f"{elapsed_ms:.0f}ms",
        **fields,
    )


def _summarize_param_keys(params: object, limit: int = 6) -> str | None:
    if not isinstance(params, dict):
        return None
    keys = [str(key) for key in params.keys()]
    if not keys:
        return None
    keys.sort()
    if len(keys) > limit:
        return ", ".join(keys[:limit]) + f", +{len(keys) - limit}"
    return ", ".join(keys)


def _normalize_instance_token(instance_token: str | None) -> tuple[str | None, str | None]:
    if not instance_token:
        return None, None
    if "@" in instance_token:
        name_part, _, hash_part = instance_token.partition("@")
        return (name_part or None), (hash_part or None)
    return None, instance_token


@asynccontextmanager
async def server_lifespan(app: FastAPI):
    http_host = os.environ.get("UNITY_MCP_HTTP_HOST", "localhost")
    http_port = os.environ.get("UNITY_MCP_HTTP_PORT", "8080")
    log_event(
        logger,
        logging.INFO,
        "SERVER",
        "CLI bridge ready",
        host=http_host,
        port=http_port,
        url=f"http://{http_host}:{http_port}",
    )
    log_event(logger, logging.INFO, "UNITY", "Waiting for Unity Editor connection")

    registry = PluginRegistry()
    loop = asyncio.get_running_loop()
    PluginHub.configure(registry, loop)

    try:
        yield
    finally:
        log_event(logger, logging.INFO, "SERVER", "CLI bridge stopped")


def create_app(project_scoped_tools: bool) -> FastAPI:
    app = FastAPI(lifespan=server_lifespan)

    custom_tool_service = CustomToolService(project_scoped_tools=project_scoped_tools)
    custom_tool_service.register_routes(app)

    @app.get("/health")
    async def health_http() -> JSONResponse:
        return JSONResponse(
            {
                "status": "healthy",
                "timestamp": time.time(),
                "message": "Unity MCP CLI bridge is running",
            }
        )

    @app.post("/api/command")
    async def cli_command_route(request: Request) -> JSONResponse:
        request_id = _next_request_id()
        started_at = time.monotonic()
        method = "POST"
        path = "/api/command"

        def _respond(payload: dict, status_code: int = 200) -> JSONResponse:
            _log_response(
                request_id,
                method,
                path,
                status_code,
                started_at,
                success=payload.get("success"),
                result_status=payload.get("status"),
                error=payload.get("error"),
                hint=payload.get("hint"),
            )
            return JSONResponse(payload, status_code=status_code)

        try:
            try:
                body = await request.json()
            except Exception:
                _log_request(request_id, method, path, type="invalid_json", instance="unknown")
                return _respond({"success": False, "error": "Invalid JSON payload"}, status_code=400)

            if not isinstance(body, dict):
                _log_request(request_id, method, path, type="invalid_payload", instance="unknown")
                return _respond(
                    {"success": False, "error": "Request payload must be a JSON object"},
                    status_code=400,
                )

            command_type = body.get("type")
            params = body.get("params", {})
            unity_instance = body.get("unity_instance")

            _log_request(
                request_id,
                method,
                path,
                type=command_type or "missing",
                instance=unity_instance or "auto",
                params=_summarize_param_keys(params),
            )

            if not command_type:
                return _respond({"success": False, "error": "Missing 'type' field"}, status_code=400)

            sessions = await PluginHub.get_sessions()
            if not sessions.sessions:
                return _respond(
                    {
                        "success": False,
                        "error": "No Unity instances connected. Make sure Unity is running with MCP plugin.",
                    },
                    status_code=503,
                )

            session_id = None
            session_details = None
            instance_name, instance_hash = _normalize_instance_token(unity_instance)
            if unity_instance:
                for sid, details in sessions.sessions.items():
                    if details.hash == instance_hash or details.project in (
                        instance_name,
                        unity_instance,
                    ):
                        session_id = sid
                        session_details = details
                        break

                if not session_id:
                    return _respond(
                        {
                            "success": False,
                            "error": f"Unity instance '{unity_instance}' not found",
                        },
                        status_code=404,
                    )
            else:
                session_id = next(iter(sessions.sessions.keys()))
                session_details = sessions.sessions.get(session_id)

            if command_type == "execute_custom_tool":
                tool_name = None
                tool_params = {}
                if isinstance(params, dict):
                    tool_name = params.get("tool_name") or params.get("name")
                    tool_params = params.get("parameters") or params.get("params") or {}

                if not tool_name:
                    return _respond(
                        {"success": False, "error": "Missing 'tool_name' for execute_custom_tool"},
                        status_code=400,
                    )
                if tool_params is None:
                    tool_params = {}
                if not isinstance(tool_params, dict):
                    return _respond(
                        {"success": False, "error": "Tool parameters must be an object/dict"},
                        status_code=400,
                    )

                unity_instance_hint = unity_instance
                if session_details and session_details.hash:
                    unity_instance_hint = session_details.hash

                project_id = resolve_project_id_for_unity_instance(unity_instance_hint)
                if not project_id:
                    return _respond(
                        {"success": False, "error": "Could not resolve project id for custom tool"},
                        status_code=400,
                    )

                service = CustomToolService.get_instance()
                result = await service.execute_tool(
                    project_id, tool_name, unity_instance_hint, tool_params
                )
                return _respond(result.model_dump())

            result = await PluginHub.send_command(session_id, command_type, params)
            return _respond(result)

        except Exception as e:
            log_event(logger, logging.ERROR, "COMMAND", "Unhandled error", error=str(e))
            if "command_type" not in locals():
                _log_request(request_id, method, path, type="invalid", instance="unknown")
            return _respond({"success": False, "error": str(e)}, status_code=500)

    @app.get("/api/custom-tools")
    async def cli_custom_tools_route(request: Request) -> JSONResponse:
        request_id = _next_request_id()
        started_at = time.monotonic()
        method = "GET"
        path = "/api/custom-tools"

        def _respond(payload: dict, status_code: int = 200) -> JSONResponse:
            _log_response(
                request_id,
                method,
                path,
                status_code,
                started_at,
                success=payload.get("success"),
                error=payload.get("error"),
                tool_count=payload.get("tool_count"),
            )
            return JSONResponse(payload, status_code=status_code)

        try:
            unity_instance = request.query_params.get("instance")
            _log_request(request_id, method, path, instance=unity_instance or "auto")
            instance_name, instance_hash = _normalize_instance_token(unity_instance)

            sessions = await PluginHub.get_sessions()
            if not sessions.sessions:
                return _respond(
                    {
                        "success": False,
                        "error": "No Unity instances connected. Make sure Unity is running with MCP plugin.",
                    },
                    status_code=503,
                )

            session_details = None
            if unity_instance:
                for _, details in sessions.sessions.items():
                    if details.hash == instance_hash or details.project in (
                        instance_name,
                        unity_instance,
                    ):
                        session_details = details
                        break
                if not session_details:
                    return _respond(
                        {"success": False, "error": f"Unity instance '{unity_instance}' not found"},
                        status_code=404,
                    )
            else:
                session_details = next(iter(sessions.sessions.values()))

            unity_instance_hint = unity_instance
            if session_details and session_details.hash:
                unity_instance_hint = session_details.hash

            project_id = resolve_project_id_for_unity_instance(unity_instance_hint)
            if not project_id:
                return _respond(
                    {"success": False, "error": "Could not resolve project id for custom tools"},
                    status_code=400,
                )

            service = CustomToolService.get_instance()
            tools = await service.list_registered_tools(project_id)
            tools_payload = [
                tool.model_dump() if hasattr(tool, "model_dump") else tool for tool in tools
            ]

            return _respond(
                {
                    "success": True,
                    "project_id": project_id,
                    "tool_count": len(tools_payload),
                    "tools": tools_payload,
                }
            )
        except Exception as e:
            log_event(logger, logging.ERROR, "TOOLS", "Custom tools error", error=str(e))
            return _respond({"success": False, "error": str(e)}, status_code=500)

    @app.get("/api/instances")
    async def cli_instances_route() -> JSONResponse:
        request_id = _next_request_id()
        started_at = time.monotonic()
        method = "GET"
        path = "/api/instances"

        def _respond(payload: dict, status_code: int = 200) -> JSONResponse:
            _log_response(
                request_id,
                method,
                path,
                status_code,
                started_at,
                success=payload.get("success"),
                error=payload.get("error"),
                instance_count=len(payload.get("instances", [])) if isinstance(payload.get("instances"), list) else None,
            )
            return JSONResponse(payload, status_code=status_code)

        try:
            _log_request(request_id, method, path)
            sessions = await PluginHub.get_sessions()
            instances = []
            for session_id, details in sessions.sessions.items():
                instances.append(
                    {
                        "session_id": session_id,
                        "project": details.project,
                        "hash": details.hash,
                        "unity_version": details.unity_version,
                        "connected_at": details.connected_at,
                    }
                )
            return _respond({"success": True, "instances": instances})
        except Exception as e:
            log_event(logger, logging.ERROR, "UNITY", "List instances failed", error=str(e))
            return _respond({"success": False, "error": str(e)}, status_code=500)

    @app.get("/plugin/sessions")
    async def plugin_sessions_route() -> JSONResponse:
        data = await PluginHub.get_sessions()
        return JSONResponse(data.model_dump())

    app.add_websocket_route("/hub/plugin", PluginHub)

    return app


def main():
    parser = argparse.ArgumentParser(
        description="Unity MCP CLI bridge",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Environment Variables:
  UNITY_MCP_HTTP_URL   HTTP server URL (default: http://localhost:8080)
  UNITY_MCP_HTTP_HOST  HTTP server host (overrides URL host)
  UNITY_MCP_HTTP_PORT  HTTP server port (overrides URL port)
  UNITY_MCP_PROJECT_SCOPED_TOOLS  Enable project-scoped custom tools (default: true)
        """,
    )
    parser.add_argument(
        "--transport",
        type=str,
        choices=["http"],
        default="http",
        help="Transport protocol (HTTP only). Kept for compatibility.",
    )
    parser.add_argument(
        "--http-url",
        type=str,
        default="http://localhost:8080",
        metavar="URL",
        help="HTTP server URL (default: http://localhost:8080). "
        "Can also set via UNITY_MCP_HTTP_URL environment variable.",
    )
    parser.add_argument(
        "--http-host",
        type=str,
        default=None,
        metavar="HOST",
        help="HTTP server host (overrides URL host). "
        "Overrides UNITY_MCP_HTTP_HOST environment variable.",
    )
    parser.add_argument(
        "--http-port",
        type=int,
        default=None,
        metavar="PORT",
        help="HTTP server port (overrides URL port). "
        "Overrides UNITY_MCP_HTTP_PORT environment variable.",
    )
    parser.add_argument(
        "--unity-instance-token",
        type=str,
        default=None,
        metavar="TOKEN",
        help="Optional per-launch token set by Unity for deterministic lifecycle management.",
    )
    parser.add_argument(
        "--pidfile",
        type=str,
        default=None,
        metavar="PATH",
        help="Optional path where the server will write its PID on startup.",
    )
    parser.add_argument(
        "--project-scoped-tools",
        action="store_true",
        help="Keep custom tools scoped to the active Unity project.",
    )

    args = parser.parse_args()

    _configure_logging()

    http_url = os.environ.get("UNITY_MCP_HTTP_URL", args.http_url)
    parsed_url = urlparse(http_url)

    http_host = args.http_host or os.environ.get("UNITY_MCP_HTTP_HOST") or parsed_url.hostname or "localhost"

    _env_port = os.environ.get("UNITY_MCP_HTTP_PORT")
    try:
        env_port = int(_env_port) if _env_port is not None else None
    except ValueError:
        log_event(
            logger,
            logging.WARNING,
            "SERVER",
            "Invalid UNITY_MCP_HTTP_PORT",
            value=_env_port,
        )
        env_port = None

    http_port = args.http_port or env_port or parsed_url.port or 8080

    os.environ["UNITY_MCP_HTTP_HOST"] = http_host
    os.environ["UNITY_MCP_HTTP_PORT"] = str(http_port)

    if args.unity_instance_token:
        os.environ["UNITY_MCP_INSTANCE_TOKEN"] = args.unity_instance_token
    if args.pidfile:
        try:
            pid_dir = os.path.dirname(args.pidfile)
            if pid_dir:
                os.makedirs(pid_dir, exist_ok=True)
            with open(args.pidfile, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
        except Exception as exc:
            log_event(
                logger,
                logging.WARNING,
                "SERVER",
                "Failed to write pidfile",
                path=args.pidfile,
                error=str(exc),
            )

    project_scoped_tools = args.project_scoped_tools or os.environ.get(
        "UNITY_MCP_PROJECT_SCOPED_TOOLS", "true"
    ).lower() in ("1", "true", "yes", "on")

    app = create_app(project_scoped_tools=project_scoped_tools)

    log_event(
        logger,
        logging.INFO,
        "SERVER",
        "Starting CLI bridge",
        host=http_host,
        port=http_port,
        url=f"http://{http_host}:{http_port}",
        project_scoped_tools=project_scoped_tools,
    )
    uvicorn.run(
        app,
        host=http_host,
        port=http_port,
        log_level=str(config.log_level).lower(),
        log_config=None,
        access_log=False,
    )


if __name__ == "__main__":
    main()
