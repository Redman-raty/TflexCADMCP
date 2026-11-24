"""Read the installed T-FLEX XML help into a versioned SQLite FTS5 index."""

from __future__ import annotations

import ctypes
from contextlib import closing
from ctypes import wintypes
import json
import os
from pathlib import Path
import re
import sqlite3
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
INDEX_SCHEMA = 1


def api_xml() -> Path:
    if path := os.environ.get("TFLEX_API_XML"):
        return Path(path).resolve(strict=True)
    home = os.environ.get("TFLEX_HOME")
    if not home and (sdk := os.environ.get("TFLEX_SDK_DIR")):
        home = str(Path(sdk).resolve().parent)
    if home:
        return (Path(home) / "Program" / "TFlexAPI.xml").resolve(strict=True)
    program_files = Path(os.environ.get("ProgramFiles", os.environ.get("SYSTEMDRIVE", "C:") + "/Program Files"))
    files = list(program_files.glob("T-FLEX CAD*18*/Program/TFlexAPI.xml"))
    if len(files) != 1:
        raise ValueError("Set TFLEX_HOME or TFLEX_API_XML to the installed T-FLEX 18 SDK.")
    return files[0].resolve()


def file_version(path: Path) -> str:
    dll = ctypes.WinDLL("version", use_last_error=True)
    dll.GetFileVersionInfoSizeW.argtypes = (wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD))
    dll.GetFileVersionInfoSizeW.restype = wintypes.DWORD
    dll.GetFileVersionInfoW.argtypes = (wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p)
    dll.GetFileVersionInfoW.restype = wintypes.BOOL
    dll.VerQueryValueW.argtypes = (ctypes.c_void_p, wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_void_p),
                                  ctypes.POINTER(wintypes.UINT))
    dll.VerQueryValueW.restype = wintypes.BOOL
    size = dll.GetFileVersionInfoSizeW(str(path), None)
    if not size:
        raise ctypes.WinError(ctypes.get_last_error())
    buffer = ctypes.create_string_buffer(size)
    pointer, length = ctypes.c_void_p(), wintypes.UINT()
    if not dll.GetFileVersionInfoW(str(path), 0, size, buffer) or not dll.VerQueryValueW(
            buffer, "\\", ctypes.byref(pointer), ctypes.byref(length)):
        raise ctypes.WinError(ctypes.get_last_error())
    info = ctypes.cast(pointer, ctypes.POINTER(wintypes.DWORD))
    if length.value < 52 or info[0] != 0xFEEF04BD:
        raise ValueError(f"Invalid file version resource in {path}")
    return ".".join(str(number) for number in (info[2] >> 16, info[2] & 65535, info[3] >> 16, info[3] & 65535))


def doc_text(element: ET.Element | None) -> str:
    if element is None:
        return ""
    text = element.text or ""
    for child in element:
        child_text = doc_text(child)
        text += child_text or child.get("cref", child.get("сref", child.get("name", "")))
        text += child.tail or ""
    return " ".join(text.split())


def members(path: Path):
    for member in ET.parse(path).findall("./members/member"):
        identifier = member.get("name", "")
        # The XML also records native C++ internals. Keep the public .NET namespace.
        if not re.match(r"^[TMPFE]:TFlex\.", identifier):
            continue
        qualified = re.split(r"[\(~]", identifier[2:], maxsplit=1)[0]
        parent, _, short = qualified.rpartition(".")
        summary = next(filter(None, map(doc_text, member.findall("summary"))), "")
        parameters = {param.get("name"): doc_text(param) for param in member.findall("param") if param.get("name")}
        yield (identifier, identifier.casefold(), qualified.casefold(),
               (parent.rsplit(".", 1)[-1] + "." + short).casefold(), short.casefold(), summary,
               json.dumps(parameters, ensure_ascii=False), doc_text(member.find("returns")), path.name)


class ApiIndex:
    def __init__(self, xml: Path | None = None, db: Path | None = None):
        self.xml = (xml or api_xml()).resolve(strict=True)
        self.db = db or Path(os.environ.get("TFLEX_MCP_INDEX", ROOT / ".tflex-mcp" / "api.sqlite3"))

    def search(self, query: str, limit: int = 12) -> dict:
        version = file_version(self.xml.with_suffix(".dll"))
        sources = [self.xml]
        sibling = self.xml.with_name("TFlexAPI3D.xml")
        if self.xml.name.lower() == "tflexapi.xml" and sibling.is_file():
            sources.append(sibling)
        fingerprint = json.dumps([INDEX_SCHEMA, version, [
            [str(path), stat.st_size, stat.st_mtime_ns] for path in sources for stat in [path.stat()]
        ]])
        self.db.parent.mkdir(parents=True, exist_ok=True)
        with closing(sqlite3.connect(self.db, timeout=30)) as conn, conn:
            conn.row_factory = sqlite3.Row
            conn.execute("BEGIN IMMEDIATE")
            conn.execute("CREATE TABLE IF NOT EXISTS meta (fingerprint TEXT NOT NULL)")
            cached = conn.execute("SELECT fingerprint FROM meta").fetchone()
            if cached is None or cached[0] != fingerprint:
                conn.execute("DROP TABLE IF EXISTS members")
                conn.execute("DROP TABLE IF EXISTS member_fts")
                conn.execute("""CREATE TABLE members (
                    id TEXT NOT NULL UNIQUE, id_fold TEXT, qualified TEXT, type_member TEXT, short TEXT,
                    summary TEXT, parameters TEXT, returns_text TEXT, source TEXT)""")
                for key in ("id_fold", "qualified", "type_member", "short"):
                    conn.execute(f"CREATE INDEX ix_{key} ON members({key})")
                conn.execute("""CREATE VIRTUAL TABLE member_fts USING fts5(
                    id, summary, parameters, tokenize='unicode61 remove_diacritics 2')""")
                for path in sources:
                    conn.executemany("INSERT OR IGNORE INTO members VALUES (?,?,?,?,?,?,?,?,?)", members(path))
                conn.execute("""INSERT INTO member_fts (rowid,id,summary,parameters)
                                SELECT rowid,id,summary,parameters FROM members""")
                conn.execute("DELETE FROM meta")
                conn.execute("INSERT INTO meta VALUES (?)", (fingerprint,))

            exact = query.strip().casefold()
            exact_hits = conn.execute("""
                SELECT * FROM members WHERE ? IN (id_fold,qualified,type_member,short)
                    OR id_fold IN ('m:' || ?, 'p:' || ?, 't:' || ?, 'f:' || ?, 'e:' || ?)
                ORDER BY CASE WHEN id_fold = ? OR substr(id_fold,3) = ? THEN 0 WHEN qualified = ? THEN 1
                              WHEN type_member = ? THEN 2 ELSE 3 END, length(id), id
                LIMIT ?
                """, (exact,) * 10 + (limit,)).fetchall()
            results = dict.fromkeys(row["id"] for row in exact_hits)
            rows = list(exact_hits)
            tokens = re.findall(r"[^\W_]+", query, re.UNICODE)[:24]
            if len(rows) < limit and tokens:
                match = " AND ".join(f'"{token}"' for token in tokens)
                for row in conn.execute("""
                    SELECT members.* FROM member_fts JOIN members ON members.rowid = member_fts.rowid
                    WHERE member_fts MATCH ? ORDER BY bm25(member_fts, 8.0, 2.0, 1.0), members.id LIMIT ?
                    """, (match, limit * 2)):
                    if row["id"] not in results:
                        rows.append(row)
                        if len(rows) == limit:
                            break
        return {"cad_version": version, "sources": [str(path) for path in sources], "results": [
            {"id": row["id"], "signature": row["id"][2:], "description": row["summary"],
             "parameters": json.loads(row["parameters"]), "returns": row["returns_text"],
             "source": row["source"], "match": "exact" if row["id"] in results else "fts"}
            for row in rows
        ]}
