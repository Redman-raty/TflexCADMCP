from __future__ import annotations

from functools import partial
import json
import os
from typing import Annotated, Literal

from anyio import Lock, to_thread
from mcp.server.fastmcp import FastMCP
from mcp.types import CallToolResult, ImageContent, TextContent, ToolAnnotations
from pydantic import Field

from api_index import ApiIndex
from drawing_spec import Drawing
from pipe_client import BridgeError, call

mcp = FastMCP("T-FLEX CAD")
cad_requests = Lock()


def text_result(data: dict, *, failed: bool = False) -> CallToolResult:
    return CallToolResult(
        content=[TextContent(type="text", text=json.dumps(data, ensure_ascii=False, allow_nan=False))],
        structuredContent=data, isError=failed,
    )


async def run(action, error_code: str) -> CallToolResult:
    try:
        data = await to_thread.run_sync(action)
        if "png_base64" in data:
            png = data.pop("png_base64")
            result = text_result(data)
            result.content.append(ImageContent(type="image", mimeType="image/png", data=png))
            return result
        return text_result(data)
    except BridgeError as exc:
        return text_result({"error": exc.payload}, failed=True)
    except Exception as exc:
        return text_result({"error": {"code": error_code, "message": str(exc)}}, failed=True)


async def cad(method: str, **arguments) -> CallToolResult:
    async with cad_requests:
        return await run(partial(call, method, **arguments), "bridge_io_error")


@mcp.tool(annotations=ToolAnnotations(readOnlyHint=True, openWorldHint=False))
async def search_tflex_api(query: Annotated[str, Field(min_length=1, max_length=500)]) -> CallToolResult:
    """Search the installed T-FLEX Open API XML. Exact names precede full-text matches."""
    return await run(lambda: ApiIndex().search(query), "api_index_error")


@mcp.tool(annotations=ToolAnnotations(readOnlyHint=True, openWorldHint=False))
async def get_document_state() -> CallToolResult:
    """Read the active CAD document, variables, operation dependencies, geometry bounds, sheets and diagnostics."""
    return await cad("get_document_state")


@mcp.tool(annotations=ToolAnnotations(idempotentHint=True, openWorldHint=False))
async def set_variables(
    document_id: Annotated[str, Field(min_length=1)],
    variables: Annotated[dict[str, str], Field(min_length=1, max_length=200)],
) -> CallToolResult:
    """Set expressions on the identified active document and rebuild in a single CAD change block."""
    return await cad("set_variables", document_id=document_id, variables=variables)


@mcp.tool(annotations=ToolAnnotations(destructiveHint=False, openWorldHint=False))
async def batch_process_files(
    files: Annotated[list[str], Field(min_length=1, max_length=100)],
    scenario: Literal["check", "step", "dxf", "stl"],
    output_dir: Annotated[str, Field(min_length=1)],
) -> CallToolResult:
    """Process explicit absolute .grs paths read-only. Write outputs to a new subfolder in output_dir."""
    return await cad("batch_process_files", files=files, scenario=scenario, output_dir=output_dir)


@mcp.tool(annotations=ToolAnnotations(readOnlyHint=True, openWorldHint=False))
async def capture_viewport(view_name: Annotated[str, Field(min_length=1, max_length=500)]) -> CallToolResult:
    """Return PNG of an open 3D viewport (3D or 3D:n) or a sheet (exact name or sheet ID)."""
    return await cad("capture_viewport", view_name=view_name)


if os.environ.get("TFLEX_MCP_MODE") == "education":
    def build_drawing(drawing: Drawing, output_dir: str) -> dict:
        result = call("create_drawing", drawing=drawing.model_dump(mode="json"), output_dir=output_dir)
        for sheet in result["sheets"]:
            # Dimension.Value is a CAD Parameter; its Value is the numeric measurement.
            sheet["dimension_values_mm"] = [value["Value"] for value in sheet["dimension_values_mm"]]
        return result

    @mcp.tool(annotations=ToolAnnotations(destructiveHint=False, openWorldHint=False))
    async def create_drawing(
        drawing: Drawing,
        output_dir: Annotated[str, Field(min_length=1)],
    ) -> CallToolResult:
        """Create a new editable GRS with 1–12 sheets. Coordinates and linear dimensions are in millimeters."""
        async with cad_requests:
            return await run(partial(build_drawing, drawing, output_dir), "bridge_io_error")


if __name__ == "__main__":
    mcp.run(transport="stdio")
