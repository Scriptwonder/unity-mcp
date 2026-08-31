---
title: get_compile_job
sidebar_label: get_compile_job
description: "Polls an async Unity compile job by job_id (returned by compile_and_report)."
---

# `get_compile_job`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.compile_and_report`

## Description

Polls an async Unity compile job by job_id (returned by compile_and_report). Terminal statuses are 'succeeded' and 'failed'; the payload carries duration_ms, errors ([{file, line, message}]), errors_total, and warnings_count.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `job_id` | `str` | yes | Job id returned by compile_and_report |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

