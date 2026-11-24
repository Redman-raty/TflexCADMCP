"""Create a two-sheet mounting plate via MCP and verify the files from the live macro."""

import asyncio
import argparse
import base64
from datetime import timedelta
from hashlib import sha256
import json
import os
from pathlib import Path
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.types import ImageContent
from mcp_check import check_length, tool, verify_export

ROOT = Path(__file__).resolve().parents[1]


def plate():
    def line(a, b, width=0):
        return dict(kind="line", points=[a, b], width=width)

    def outline(*points):
        return dict(kind="polyline", points=points, closed=True, width=0.5)

    def note(x, y, text):
        return dict(kind="text", points=[(x, y)], text=text)

    def circle(x, y, r):
        return dict(kind="circle", points=[(x, y)], radius=r, width=0.4)

    def arc(*points):
        return dict(kind="arc", points=points, width=0.5)

    def dimension(a, b, align, offset):
        return dict(kind="dimension", points=[a, b], align=align, offset=offset)

    def border():
        return [outline((10, 10), (287, 10), (287, 200), (10, 200)),
                line((195, 22), (287, 22)), line((195, 10), (195, 34)), line((195, 34), (287, 34))]

    front = border() + [
        note(25, 188, "ОПОРНАЯ ПЛИТА / ВИД СВЕРХУ"),
        note(203, 31, "ОП.01.001"), note(203, 19, "Лист 1 / 2"),
        outline((60, 62), (207, 62), (217, 72), (217, 147), (207, 157),
                (60, 157), (50, 147), (50, 72)),
        circle(133.5, 109.5, 13), circle(133.5, 109.5, 19),
        line((104, 101), (163, 101), 0.4), line((104, 118), (163, 118), 0.4),
        arc((104, 101), (95.5, 109.5), (104, 118)), arc((163, 101), (171.5, 109.5), (163, 118)),
        dimension((50, 72), (217, 72), "horizontal", 26),
        dimension((60, 62), (60, 157), "vertical", -24),
        dimension((69, 140), (198, 140), "horizontal", -30),
        note(233, 134, "8 отв. ⌀10"), note(233, 122, "Масштаб 1:1"),
    ]
    for x in (69, 94, 173, 198):
        for y in (79, 140):
            front += [circle(x, y, 5), line((x - 8, y), (x + 8, y), 0.15),
                      line((x, y - 8), (x, y + 8), 0.15)]
    side = border() + [
        note(25, 188, "ОПОРНАЯ ПЛИТА / УСЛОВНЫЙ РАЗРЕЗ"),
        note(203, 31, "ОП.01.001"), note(203, 19, "Лист 2 / 2"),
        outline((50, 106), (217, 106), (217, 122), (50, 122)),
        outline((103, 122), (164, 122), (164, 143), (103, 143)),
        dimension((50, 106), (217, 106), "horizontal", 21),
        dimension((217, 106), (217, 122), "vertical", 20),
        dimension((103, 143), (164, 143), "horizontal", -13),
        note(55, 63, "Кромки притупить. Размеры в мм."),
    ]
    for x in range(52, 202, 5):
        side.append(line((x, 106.5), (x + 15, 121.5), 0.15))
    return {"sheets": [{"name": "Вид сверху", "entities": front}, {"name": "Разрез", "entities": side}]}


async def check_changes(session, active_block):
    state = await tool(session, "get_document_state")
    assert Path(state["path"]).samefile(active_block), "Activate the specified test copy first."
    check_length(state, 100)
    document_id = state["document_id"]
    update = {"document_id": document_id, "variables": {"L": "120"}}
    applied = await tool(session, "set_variables", **update)
    assert applied["rebuild"] == "OK"
    check_length(await tool(session, "get_document_state"), 120)
    bad = await session.call_tool("set_variables", {
        "document_id": document_id, "variables": {"CHECK_PROBE": "777", "L": "("}
    })
    assert bad.isError and bad.structuredContent["error"]["code"] == "change_failed", bad
    after = await tool(session, "get_document_state")
    check_length(after, 120)
    assert next(v["value"] for v in after["variables"] if v["name"] == "CHECK_PROBE") == 1
    assert not after["in_change_block"]
    await tool(session, "set_variables", document_id=document_id, variables={"L": "130"})
    check_length(await tool(session, "get_document_state"), 130)
    print("Live education MCP geometry: 100 → 120 → rollback at 120 → 130 mm.")


async def check_batch(session, sources, out):
    paths = [path.resolve(strict=True) for path in sources]
    digests = [sha256(path.read_bytes()).digest() for path in paths]
    for scenario in ("check", "stl"):
        report = await tool(session, "batch_process_files", files=list(map(str, paths)),
                            scenario=scenario, output_dir=str(out))
        assert len(report["files"]) == len(paths) and all(row["ok"] for row in report["files"]), report
        for row in report["files"]:
            file = Path(row["output"])
            if scenario == "check":
                state = json.loads(file.read_text(encoding="utf-8"))
                assert state["read_only"]
                check_length(state, 100)
            else:
                verify_export(file, scenario)
    assert [sha256(path.read_bytes()).digest() for path in paths] == digests
    print(f"Live education MCP read-only check and STL: {len(paths)} inputs.")


async def main(options):
    out = ROOT / "artifacts" / "education" / "drawings"
    out.mkdir(parents=True, exist_ok=True)
    spec = plate()
    (out / "plate-spec.json").write_text(json.dumps(spec, indent=2, ensure_ascii=False), encoding="utf-8")
    env = {"TFLEX_MCP_MODE": "education", "TFLEX_MCP_TIMEOUT": "45"}
    for key in ("TFLEX_HOME", "TFLEX_API_XML", "TFLEX_MCP_PIPE_NAME"):
        if key in os.environ:
            env[key] = os.environ[key]
    params = StdioServerParameters(command=sys.executable, args=[str(ROOT / "server" / "tflex_mcp.py")], env=env)
    async with stdio_client(params) as (rx, tx), ClientSession(rx, tx, read_timeout_seconds=timedelta(seconds=55)) as session:
        await session.initialize()
        names = {item.name for item in (await session.list_tools()).tools}
        assert len(names) == 6 and "create_drawing" in names
        search = await session.call_tool("search_tflex_api", {"query": "LinearDimension.SetTwoNodes"})
        assert not search.isError and search.structuredContent["results"][0]["match"] == "exact"
        if options.active_block:
            await check_changes(session, options.active_block)
        if options.stl_source:
            await check_batch(session, options.stl_source, out)
        result = await session.call_tool("create_drawing", {"drawing": spec, "output_dir": str(out)})
        assert not result.isError, result.structuredContent or result.content
        info = result.structuredContent
        print(json.dumps(info, ensure_ascii=False, indent=2))
        assert info["rebuild"] == "OK"
        path = Path(info["path"])
        assert path.is_file() and path.stat().st_size > 1000
        assert len(info["sheets"]) == 2 and sum(s["entities"] for s in info["sheets"]) >= 65
        for sheet, expected in zip(info["sheets"], ([167, 95, 129], [167, 16, 61])):
            assert not sheet["preview_error"], sheet
            preview = Path(sheet["preview"]).read_bytes()
            assert preview.startswith(b"\x89PNG\r\n\x1a\n") and len(preview) > 1000
            assert len(sheet["dimension_values_mm"]) == len(expected)
            assert all(abs(a - b) < 1e-5 for a, b in zip(sheet["dimension_values_mm"], expected)), sheet
        state = await session.call_tool("get_document_state")
        assert not state.isError, state.structuredContent
        print("Active document:", state.structuredContent["path"])
        assert Path(state.structuredContent["path"]).samefile(path)
        for sheet in info["sheets"]:
            capture = await session.call_tool("capture_viewport", {"view_name": sheet["view_name"]})
            assert not capture.isError, capture.structuredContent
            image = next(i for i in capture.content if isinstance(i, ImageContent))
            assert image.mimeType == "image/png"
            assert base64.b64decode(image.data).startswith(b"\x89PNG\r\n\x1a\n")
        (path.parent / "mcp-report.json").write_text(json.dumps(info, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Education MCP drawing, six native dimensions and both sheet captures passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--active-block", type=Path)
    parser.add_argument("--stl-source", type=Path, nargs="+")
    asyncio.run(main(parser.parse_args()))
