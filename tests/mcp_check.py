"""Exercise the MCP stdio client, with an isolated transport probe or a real CAD fixture."""

import argparse
import asyncio
import base64
from hashlib import sha256
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
from datetime import timedelta
from uuid import uuid4

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.types import ImageContent

ROOT = Path(__file__).resolve().parents[1]
TOOLS = {"search_tflex_api", "get_document_state", "set_variables", "batch_process_files", "capture_viewport"}


async def tool(session, name, **arguments):
    result = await session.call_tool(name, arguments)
    assert not result.isError, result.structuredContent or result.content
    assert result.structuredContent is not None
    return result.structuredContent


async def check_api(session):
    assert {entry.name for entry in (await session.list_tools()).tools} == TOOLS
    result = await tool(session, "search_tflex_api", query="Document.EndChanges")
    assert result["cad_version"].startswith("18.")
    assert result["results"][0]["id"] == "M:TFlex.Model.Document.EndChanges"
    assert result["results"][0]["match"] == "exact"
    query = "TFlex.Model.Document.EndChanges(System.Boolean,System.Boolean)"
    overload = (await tool(session, "search_tflex_api", query=query))["results"][0]
    assert overload["signature"] == query and overload["match"] == "exact"
    assert set(overload["parameters"]) == {"Unconditionally", "Regenerate"}
    assert (await tool(session, "search_tflex_api", query="ошибками пересчёта"))["results"]
    assert (await tool(session, "search_tflex_api", query="AABoundBox"))["results"][0]["source"] == "TFlexAPI3D.xml"
    assert (await tool(session, "search_tflex_api", query='"* (:'))["results"] == []
    return result["cad_version"]


async def png(session, name):
    result = await session.call_tool("capture_viewport", {"view_name": name})
    assert not result.isError, result.structuredContent or result.content
    images = [item for item in result.content if isinstance(item, ImageContent)]
    assert len(images) == 1 and images[0].mimeType == "image/png"
    data = base64.b64decode(images[0].data, validate=True)
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    assert data[12:16] == b"IHDR"
    width, height = struct.unpack(">II", data[16:24])
    assert width and height
    return result.structuredContent, data, (width, height)


async def check_transport(session, images):
    sample = await tool(session, "get_document_state")
    assert sample == {"sample": "Д" * 160000, "transport_check": True}
    failure = await session.call_tool("set_variables", {"document_id": "probe", "variables": {"L": "120"}})
    assert failure.isError and failure.structuredContent["error"] == {
        "code": "transport_test_error", "message": "Expected test response.", "details": {"received": {"L": "120"}}
    }
    view, image, dimensions = await png(session, "3D:1")
    assert view["view_name"] == "3D:1"
    if images:
        assert image == (images / "viewport.png").read_bytes() and dimensions == (1024, 768)
        sheet, sheet_bytes, sheet_size = await png(session, "MCP check sheet")
        assert sheet["view_name"] == "MCP check sheet" and sheet_bytes == (images / "sheet.png").read_bytes()
        assert sheet_bytes != image
        print(f"Two PNG files rendered in CAD reached the MCP client byte-for-byte: {dimensions} and {sheet_size}.")
    else:
        assert dimensions == (1, 1)
    files = [r"C:\Модели\блок.grs", r"C:\Модели\блок 2.grs"]
    output_dir = r"C:\Вывод"
    batch = await tool(session, "batch_process_files", files=files, scenario="stl", output_dir=output_dir)
    assert batch == {"request": files, "scenario": "stl", "output_dir": output_dir}
    assert (await tool(session, "get_document_state"))["transport_check"]


def check_length(state, expected):
    variable = next(v for v in state["variables"] if v["name"] == "L")
    body, = [op for op in state["operations"] if op["type"] == "Block"]
    assert abs(variable["value"] - expected) < 1e-6, variable
    assert body["regeneration_ok"] and body["bounds"]["available"], body
    assert body["bounds"]["units"] == "m"
    assert abs(body["bounds"]["size"][0] * 1000 - expected) < 1e-6, body


def verify_export(path, scenario):
    data = path.read_bytes()
    if scenario == "stl":
        assert len(data) >= 84
        count, = struct.unpack("<I", data[80:84])
        assert count >= 12 and len(data) == 84 + count * 50
    elif scenario == "step":
        assert b"ISO-10303-21;" in data[:100] and b"END-ISO-10303-21;" in data[-100:]
    elif scenario == "dxf":
        assert b"SECTION" in data and b"EOF" in data[-256:]


async def check_live(session, fixtures, output, scenario):
    state = await tool(session, "get_document_state")
    active = fixtures / "mcp-block.grs"
    if (fixtures / "mcp-block-working.grs").is_file():
        active = fixtures / "mcp-block-working.grs"
    assert Path(state["path"]).samefile(active), "Open the test fixture and make it active."
    assert not state["read_only"]
    document_id = state["document_id"]
    check_length(state, 100)

    async def change(variables, target=document_id):
        return await session.call_tool("set_variables", {"document_id": target, "variables": variables})

    wrong = await change({"L": "999"}, "deliberately-wrong-id")
    assert wrong.isError and wrong.structuredContent["error"]["code"] == "document_mismatch"
    success = await change({"L": "120"})
    assert not success.isError and success.structuredContent["rebuild"] == "OK", success
    after = await tool(session, "get_document_state")
    check_length(after, 120)
    bad = await change({"CHECK_PROBE": "777", "L": "("})
    assert bad.isError and bad.structuredContent["error"]["code"] in {"change_failed", "rebuild_failed"}, bad
    after_error = await tool(session, "get_document_state")
    check_length(after_error, 120)
    probe = next(v for v in after_error["variables"] if v["name"] == "CHECK_PROBE")
    assert probe["value"] == 1 and not after_error["in_change_block"]
    next_change = await change({"L": "130"})
    assert not next_change.isError and next_change.structuredContent["rebuild"] == "OK", next_change
    current = await tool(session, "get_document_state")
    check_length(current, 130)

    output.mkdir(parents=True, exist_ok=True)
    sheet = next(page for page in current["sheets"] if page["name"] == "MCP check sheet")
    view_3d, viewport, dimensions_3d = await png(session, "3D")
    sheet_view, sheet_png, dimensions_sheet = await png(session, sheet["view_name"])
    assert view_3d["kind"] == "3d" and sheet_view["kind"] == "sheet"
    assert sheet_view["view_name"] == sheet["view_name"] and sheet_png != viewport
    (output / "mcp-viewport.png").write_bytes(viewport)
    (output / "mcp-sheet.png").write_bytes(sheet_png)

    paths = [fixtures / "mcp-block.grs", fixtures / "mcp-block-b.grs"]
    hashes = {path: sha256(path.read_bytes()).digest() for path in paths}
    # Include one missing file between valid files to verify per-file continuation.
    missing = fixtures / "does-not-exist.grs"
    paths_for_batch = [paths[0], missing, paths[1]]
    for mode in ("check", scenario):
        report = await tool(session, "batch_process_files", files=list(map(str, paths_for_batch)),
                            scenario=mode, output_dir=str(output))
        assert [entry["ok"] for entry in report["files"]] == [True, False, True], report
        for entry in report["files"]:
            assert entry["close_error"] is None
            if not entry["ok"]:
                continue
            file = Path(entry["output"])
            assert file.is_relative_to(output)
            if mode == "check":
                snapshot = json.loads(file.read_text(encoding="utf-8"))
                assert snapshot["read_only"]
                check_length(snapshot, 100)
            else:
                verify_export(file, mode)
    assert all(sha256(path.read_bytes()).digest() == hashes[path] for path in paths)
    final = await tool(session, "get_document_state")
    assert final["document_id"] == document_id
    check_length(final, 130)
    print(f"CAD geometry 100 → 120 → rollback at 120 → 130; {scenario}: 2 exports.")
    print(f"MCP PNG: 3D {dimensions_3d}; sheet {dimensions_sheet}; output {output}")


async def main(options):
    harness = None
    env = {key: os.environ[key] for key in ("TFLEX_HOME", "TFLEX_SDK_DIR", "TFLEX_API_XML",
                                            "TFLEX_MCP_TIMEOUT", "TFLEX_MCP_PIPE_NAME") if key in os.environ}
    direct_to_cad = options.fixtures is not None
    if not direct_to_cad:
        env["TFLEX_MCP_PIPE_NAME"] = "TFlexMcp.Test." + uuid4().hex
    if options.transport:
        executable = ROOT / "artifacts" / "transport" / "TransportCheck.exe"
        args = [str(executable), env["TFLEX_MCP_PIPE_NAME"]]
        if options.images:
            args.append(str(options.images.resolve()))
        harness = subprocess.Popen(args, creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        stdio = StdioServerParameters(command=sys.executable, args=[str(ROOT / "server" / "tflex_mcp.py")],
                                      env=env or None)
        async with stdio_client(stdio) as (reader, writer), ClientSession(
                reader, writer, read_timeout_seconds=timedelta(seconds=210 if direct_to_cad else 15)) as session:
            await session.initialize()
            print(f"MCP stdio, five tools, installed XML and FTS5: T-FLEX {await check_api(session)}")
            if direct_to_cad:
                await check_live(session, options.fixtures.resolve(), options.output.resolve(), options.format)
            elif options.transport:
                await check_transport(session, options.images)
                print("Real C# Named Pipe and UI dispatcher: fragmented UTF-8, error, PNG and reuse passed with test data.")
            else:
                unavailable = await session.call_tool("get_document_state")
                assert unavailable.isError and unavailable.structuredContent["error"]["code"] == "cad_unavailable"
                print("Structured error for an absent bridge passed.")
    finally:
        if harness is not None:
            harness.terminate()
            harness.wait(timeout=5)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--transport", action="store_true")
    mode.add_argument("--fixtures", type=Path)
    parser.add_argument("--images", type=Path, help="With --transport, forward the two PNGs from a native CAD check.")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "mcp-check")
    parser.add_argument("--format", choices=("stl", "step", "dxf"), default="stl")
    options = parser.parse_args()
    if options.images and not options.transport:
        parser.error("--images requires --transport")
    asyncio.run(main(options))
