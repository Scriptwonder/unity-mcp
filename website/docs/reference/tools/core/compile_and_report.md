---
title: compile_and_report
sidebar_label: compile_and_report
description: "Requests a Unity script compilation, waits for it to finish, and returns one consolidated report: success, duration_ms, errors ([{file, line, message}]), and warnings_count."
---

# `compile_and_report`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.compile_and_report`

## Description

Requests a Unity script compilation, waits for it to finish, and returns one consolidated report: success, duration_ms, errors ([{file, line, message}]), and warnings_count. If Unity is already compiling on entry, the current compilation is awaited first and a fresh one is then requested. The report survives the domain reload a successful compile triggers (SessionState-backed job). On timeout the compile job keeps running — poll get_compile_job with the returned job_id.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `timeout` | `int` | — | Maximum seconds to wait for the compile to finish (default: 120). On timeout the job keeps running; poll get_compile_job with the returned job_id. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

