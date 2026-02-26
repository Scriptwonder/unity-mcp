from typing import Annotated, Any

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.utils import coerce_int
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry
from services.tools.preflight import preflight


@mcp_for_unity_tool(
    description=(
        "Compiles and executes C# code snippets at runtime using Roslyn. "
        "Use for scene validation, property inspection, or any programmatic check. "
        "The code must define a public class with a public static method. "
        "Returns compilation errors, captured Debug.Log output, and the method's return value. "
        "Requires Microsoft.CodeAnalysis DLLs in the Unity project (see error message if missing)."
    ),
    annotations=ToolAnnotations(
        title="Execute Code",
        destructiveHint=True,
    ),
)
async def execute_code(
    ctx: Context,
    code: Annotated[str,
                    "C# code to compile and execute. Must define a public class with a public static method. "
                    "Example: 'public class V { public static string Run() { return UnityEngine.Object.FindObjectsOfType<UnityEngine.MeshRenderer>().Length.ToString(); } }'"],
    entry_type: Annotated[str,
                          "Fully qualified type name to invoke (default 'Validator')."] | None = None,
    entry_method: Annotated[str,
                            "Static method name to call on the entry type (default 'Run')."] | None = None,
    timeout_ms: Annotated[int | str,
                          "Execution timeout in milliseconds (default 5000)."] | None = None,
) -> dict[str, Any]:
    unity_instance = get_unity_instance_from_context(ctx)
    gate = await preflight(ctx, wait_for_no_compile=True, refresh_if_dirty=True)
    if gate is not None:
        return gate.model_dump()
    try:
        params: dict[str, Any] = {"code": code}
        if entry_type:
            params["entryType"] = entry_type
        if entry_method:
            params["entryMethod"] = entry_method
        coerced_timeout = coerce_int(timeout_ms, default=None)
        if coerced_timeout is not None:
            params["timeoutMs"] = coerced_timeout

        response = await send_with_unity_instance(async_send_command_with_retry, unity_instance, "execute_code", params)

        if isinstance(response, dict) and response.get("success"):
            return {
                "success": True,
                "message": response.get("message", "Code execution complete."),
                "data": response.get("data"),
            }
        return response if isinstance(response, dict) else {"success": False, "message": str(response)}

    except Exception as e:
        return {"success": False, "message": f"Python error executing code: {str(e)}"}
