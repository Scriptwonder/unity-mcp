"""Blocking compile gate: request compilation, wait, return one report.

``compile_and_report`` wraps the Unity-side async compile job (started via the
``compile_and_report`` command, polled via ``get_compile_job``) in a single MCP
response, so callers get ``{success, duration_ms, errors, warnings_count}``
without polling themselves. The Unity job is SessionState-backed (same pattern
as run_tests/get_test_job), which is what lets the report survive the domain
reload a successful compilation triggers.
"""
from __future__ import annotations

import asyncio
import logging
import time
from typing import Annotated, Any

from fastmcp import Context
from mcp.types import ToolAnnotations
from pydantic import BaseModel

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
import transport.unity_transport as unity_transport
from transport.legacy.unity_connection import async_send_command_with_retry

logger = logging.getLogger(__name__)

# Unity finalizes the job from CompilationPipeline events; 1 s keeps latency
# low without hammering the bridge while it reconnects after a domain reload.
_POLL_INTERVAL_S = 1.0

_TERMINAL_STATUSES = ("succeeded", "failed")


class CompileJobError(BaseModel):
    file: str | None = None
    line: int | None = None
    message: str | None = None


class CompileJobData(BaseModel):
    job_id: str
    status: str
    phase: str | None = None
    started_unix_ms: int | None = None
    compile_started_unix_ms: int | None = None
    finished_unix_ms: int | None = None
    last_update_unix_ms: int | None = None
    duration_ms: int | None = None
    errors: list[CompileJobError] | None = None
    errors_total: int | None = None
    errors_capped: bool | None = None
    warnings_count: int | None = None
    error: str | None = None
    finalized_by: str | None = None


class CompileJobResponse(MCPResponse):
    data: CompileJobData | None = None


@mcp_for_unity_tool(
    description=(
        "Requests a Unity script compilation, waits for it to finish, and returns one "
        "consolidated report: success, duration_ms, errors ([{file, line, message}]), and "
        "warnings_count. If Unity is already compiling on entry, the current compilation is "
        "awaited first and a fresh one is then requested. The report survives the domain "
        "reload a successful compile triggers (SessionState-backed job). On timeout the "
        "compile job keeps running — poll get_compile_job with the returned job_id."
    ),
    annotations=ToolAnnotations(
        title="Compile And Report",
        destructiveHint=True,
    ),
)
async def compile_and_report(
    ctx: Context,
    timeout: Annotated[int,
                       "Maximum seconds to wait for the compile to finish (default: 120). "
                       "On timeout the job keeps running; poll get_compile_job with the "
                       "returned job_id."] = 120,
) -> CompileJobResponse | MCPResponse:
    if timeout <= 0:
        return MCPResponse(success=False, error="timeout must be a positive integer (seconds)")

    unity_instance = await get_unity_instance_from_context(ctx)

    start_resp = await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "compile_and_report",
        {},
    )
    if not isinstance(start_resp, dict):
        return MCPResponse(success=False, error=str(start_resp))
    if not start_resp.get("success", False):
        return MCPResponse(**start_resp)

    job_id = (start_resp.get("data") or {}).get("job_id")
    if not job_id:
        return MCPResponse(
            success=False,
            error="compile_and_report: Unity did not return a job_id "
                  "(MCPForUnity package too old? Update to a version with compile_and_report).",
            data=start_resp.get("data"),
        )

    deadline = time.monotonic() + timeout
    last_data: dict[str, Any] | None = None
    last_error: str | None = None

    while True:
        try:
            poll = await unity_transport.send_with_unity_instance(
                async_send_command_with_retry,
                unity_instance,
                "get_compile_job",
                {"job_id": job_id},
            )
        except Exception as exc:
            # Transient by design: a successful compile domain-reloads Unity and
            # drops the bridge mid-poll. Keep polling until the deadline.
            logger.debug(f"get_compile_job transient failure (will retry): {exc}")
            poll = None
            last_error = str(exc)

        if isinstance(poll, dict):
            if poll.get("success", False):
                data = poll.get("data") or {}
                last_data = data
                status = data.get("status")
                if status in _TERMINAL_STATUSES:
                    compile_ok = status == "succeeded"
                    return CompileJobResponse(
                        success=compile_ok,
                        message=(
                            f"Compilation succeeded in {data.get('duration_ms')} ms "
                            f"({data.get('warnings_count') or 0} warning(s))."
                            if compile_ok
                            else f"Compilation failed with {data.get('errors_total') or 0} error(s)."
                        ),
                        error=None if compile_ok else "compile_failed",
                        data=CompileJobData(**data),
                    )
            else:
                err = poll.get("error") or poll.get("message") or ""
                if "Unknown job_id" in str(err):
                    # The editor session that owned the job is gone (restart) —
                    # not recoverable by more polling.
                    return MCPResponse(**poll)
                # Anything else (connection closed, reloading) is transient
                # during the compile/reload window; keep polling.
                last_error = str(err)

        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return MCPResponse(
                success=False,
                error="compile_timeout",
                message=(
                    f"Compile job {job_id} did not finish within {timeout}s. "
                    "The job keeps running — poll get_compile_job with this job_id."
                ),
                data={
                    "job_id": job_id,
                    "status": "timeout",
                    "last_status": (last_data or {}).get("status"),
                    "last_phase": (last_data or {}).get("phase"),
                    "last_error": last_error,
                },
            )

        await asyncio.sleep(min(_POLL_INTERVAL_S, remaining))


@mcp_for_unity_tool(
    description=(
        "Polls an async Unity compile job by job_id (returned by compile_and_report). "
        "Terminal statuses are 'succeeded' and 'failed'; the payload carries duration_ms, "
        "errors ([{file, line, message}]), errors_total, and warnings_count."
    ),
    annotations=ToolAnnotations(
        title="Get Compile Job",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def get_compile_job(
    ctx: Context,
    job_id: Annotated[str, "Job id returned by compile_and_report"],
) -> CompileJobResponse | MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)

    response = await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "get_compile_job",
        {"job_id": job_id},
    )

    if not isinstance(response, dict):
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        return MCPResponse(**response)
    return CompileJobResponse(**response)
