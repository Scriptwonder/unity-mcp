"""Tests for the compile_and_report / get_compile_job MCP tools.

The Unity side is stubbed at the transport seam (send_with_unity_instance),
same as test_run_tests_async.py. Scripted response sequences simulate the
compile job lifecycle, including the transient transport failures a domain
reload causes mid-poll.
"""
import pytest

from .test_helpers import DummyContext


def _start_response(job_id="cjob1", attached=False):
    return {
        "success": True,
        "message": "Compile job started.",
        "data": {"job_id": job_id, "status": "running", "attached": attached},
    }


def _job_response(job_id="cjob1", status="running", phase="compiling", **extra):
    data = {
        "job_id": job_id,
        "status": status,
        "phase": phase,
        "started_unix_ms": 1000,
        "last_update_unix_ms": 2000,
        "errors": [],
        "errors_total": 0,
        "errors_capped": False,
        "warnings_count": 0,
    }
    data.update(extra)
    return {"success": True, "message": "Compile job status retrieved.", "data": data}


def _patch_transport(monkeypatch, script):
    """Patch send_with_unity_instance with a scripted sequence.

    ``script`` is a list of responses; callables are invoked (may raise),
    everything else is returned as-is. The last entry repeats forever.
    Returns the list of (command_type, params) calls made.
    """
    import services.tools.compile_and_report as mod

    calls = []

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        calls.append((command_type, params))
        idx = min(len(calls) - 1, len(script) - 1)
        entry = script[idx]
        if callable(entry):
            return entry()
        return entry

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send)
    return calls


def _fast_poll(monkeypatch):
    import services.tools.compile_and_report as mod
    monkeypatch.setattr(mod, "_POLL_INTERVAL_S", 0.01)


@pytest.mark.asyncio
async def test_compile_and_report_success_single_response(monkeypatch):
    from services.tools.compile_and_report import compile_and_report

    _fast_poll(monkeypatch)
    calls = _patch_transport(monkeypatch, [
        _start_response(),
        _job_response(status="running", phase="compiling"),
        _job_response(status="succeeded", phase="finished",
                      finished_unix_ms=6000, duration_ms=5000, warnings_count=2,
                      finalized_by="event"),
    ])

    resp = await compile_and_report(DummyContext(), timeout=30)

    assert calls[0][0] == "compile_and_report"
    assert all(c[0] == "get_compile_job" for c in calls[1:])
    assert all(c[1] == {"job_id": "cjob1"} for c in calls[1:])
    assert resp.success is True
    assert resp.data.status == "succeeded"
    assert resp.data.duration_ms == 5000
    assert resp.data.warnings_count == 2
    assert resp.data.errors == []


@pytest.mark.asyncio
async def test_compile_and_report_failure_carries_errors(monkeypatch):
    from services.tools.compile_and_report import compile_and_report

    _fast_poll(monkeypatch)
    errors = [
        {"file": "Assets/Scripts/GameManager.cs", "line": 12, "message": "error CS1002: ; expected"},
        {"file": "Assets/Scripts/Hud.cs", "line": 3, "message": "error CS0246: type not found"},
    ]
    _patch_transport(monkeypatch, [
        _start_response(),
        _job_response(status="failed", phase="finished",
                      errors=errors, errors_total=2, duration_ms=4000,
                      error="Compilation failed with 2 error(s)"),
    ])

    resp = await compile_and_report(DummyContext(), timeout=30)

    assert resp.success is False
    assert resp.error == "compile_failed"
    assert resp.data.status == "failed"
    assert resp.data.errors_total == 2
    assert [e.file for e in resp.data.errors] == [
        "Assets/Scripts/GameManager.cs", "Assets/Scripts/Hud.cs"]
    assert resp.data.errors[0].line == 12


@pytest.mark.asyncio
async def test_compile_and_report_tolerates_domain_reload_transients(monkeypatch):
    """Transport exceptions mid-poll (domain reload) must not abort the wait."""
    from services.tools.compile_and_report import compile_and_report

    _fast_poll(monkeypatch)

    def _boom():
        raise ConnectionError("Connection closed during domain reload")

    _patch_transport(monkeypatch, [
        _start_response(),
        _boom,  # bridge down mid-reload
        {"success": False, "error": "reloading", "hint": "retry"},  # rejected, transient
        _job_response(status="succeeded", phase="finished", duration_ms=9000),
    ])

    resp = await compile_and_report(DummyContext(), timeout=30)
    assert resp.success is True
    assert resp.data.status == "succeeded"


@pytest.mark.asyncio
async def test_compile_and_report_timeout_returns_job_id(monkeypatch):
    from services.tools.compile_and_report import compile_and_report

    _fast_poll(monkeypatch)
    _patch_transport(monkeypatch, [
        _start_response(),
        _job_response(status="running", phase="compiling"),  # repeats forever
    ])

    resp = await compile_and_report(DummyContext(), timeout=1)

    assert resp.success is False
    assert resp.error == "compile_timeout"
    assert resp.data["job_id"] == "cjob1"
    assert resp.data["status"] == "timeout"
    assert resp.data["last_status"] == "running"


@pytest.mark.asyncio
async def test_compile_and_report_rejects_non_positive_timeout():
    from services.tools.compile_and_report import compile_and_report

    resp = await compile_and_report(DummyContext(), timeout=0)
    assert resp.success is False
    assert "timeout" in resp.error


@pytest.mark.asyncio
async def test_compile_and_report_start_failure_passthrough(monkeypatch):
    from services.tools.compile_and_report import compile_and_report

    _patch_transport(monkeypatch, [
        {"success": False, "error": "tests_running",
         "data": {"reason": "tests_running", "retry_after_ms": 5000}},
    ])

    resp = await compile_and_report(DummyContext(), timeout=30)
    assert resp.success is False
    assert resp.error == "tests_running"


@pytest.mark.asyncio
async def test_compile_and_report_missing_job_id_is_error(monkeypatch):
    from services.tools.compile_and_report import compile_and_report

    _patch_transport(monkeypatch, [
        {"success": True, "message": "ok", "data": {}},
    ])

    resp = await compile_and_report(DummyContext(), timeout=30)
    assert resp.success is False
    assert "job_id" in resp.error


@pytest.mark.asyncio
async def test_compile_and_report_unknown_job_id_aborts_polling(monkeypatch):
    """An editor restart wipes SessionState — more polling cannot recover."""
    from services.tools.compile_and_report import compile_and_report

    _fast_poll(monkeypatch)
    _patch_transport(monkeypatch, [
        _start_response(),
        {"success": False, "error": "Unknown job_id."},
    ])

    resp = await compile_and_report(DummyContext(), timeout=30)
    assert resp.success is False
    assert "Unknown job_id" in resp.error


@pytest.mark.asyncio
async def test_get_compile_job_forwards_job_id(monkeypatch):
    from services.tools.compile_and_report import get_compile_job

    calls = _patch_transport(monkeypatch, [
        _job_response(job_id="job-9", status="running", phase="compile_requested"),
    ])

    resp = await get_compile_job(DummyContext(), job_id="job-9")

    assert calls[0][0] == "get_compile_job"
    assert calls[0][1]["job_id"] == "job-9"
    assert resp.success is True
    assert resp.data.job_id == "job-9"
    assert resp.data.status == "running"


@pytest.mark.asyncio
async def test_get_compile_job_error_passthrough(monkeypatch):
    from services.tools.compile_and_report import get_compile_job

    _patch_transport(monkeypatch, [
        {"success": False, "error": "Unknown job_id."},
    ])

    resp = await get_compile_job(DummyContext(), job_id="nope")
    assert resp.success is False
    assert "Unknown job_id" in resp.error
