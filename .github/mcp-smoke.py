#!/usr/bin/env python3
"""Linux/macOS 无头 MCP 冒烟：stdio 握手、文件级工程工具、PNG 导出与实时画布平台守卫。"""
import json
import os
import select
import struct
import subprocess
import sys
import tempfile

if len(sys.argv) != 2:
    raise SystemExit("usage: mcp-smoke.py <PixelCanvas.Mcp.dll>")

dll = os.path.abspath(sys.argv[1])
if not os.path.isfile(dll):
    raise SystemExit(f"MCP dll 不存在：{dll}")

work = tempfile.mkdtemp(prefix="pixelcanvas-mcp-")
proc = subprocess.Popen(
    ["dotnet", dll],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    cwd=work,
)


def call(request_id, method, params=None):
    request = {"jsonrpc": "2.0", "id": request_id, "method": method}
    if params is not None:
        request["params"] = params
    proc.stdin.write((json.dumps(request, ensure_ascii=False) + "\n").encode("utf-8"))
    proc.stdin.flush()
    while True:
        ready, _, _ = select.select([proc.stdout], [], [], 20)
        if not ready:
            raise RuntimeError(f"{method} 超过 20 秒无响应")
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError(f"MCP 提前退出；stderr={proc.stderr.read().decode('utf-8', 'replace')}")
        message = json.loads(line)
        if message.get("id") == request_id:
            if "error" in message:
                raise RuntimeError(f"{method} 返回错误：{message['error']}")
            return message["result"]


try:
    init = call(1, "initialize", {
        "protocolVersion": "2025-06-18",
        "capabilities": {},
        "clientInfo": {"name": "linux-smoke", "version": "1.0"},
    })
    assert init["serverInfo"]["name"] == "pixelcanvas", init

    tools = call(2, "tools/list", {})["tools"]
    names = {tool["name"] for tool in tools}
    assert {"pixelcanvas_project", "pixelcanvas_canvas"} <= names, names

    project = os.path.join(work, "smoke.pxproj")
    created = call(3, "tools/call", {
        "name": "pixelcanvas_project",
        "arguments": {"action": "project_create", "arguments": {"path": project, "width": 8, "height": 8}},
    })
    assert not created.get("isError"), created
    assert os.path.isfile(project), "工程文件未生成"

    png = os.path.join(work, "smoke.png")
    exported = call(4, "tools/call", {
        "name": "pixelcanvas_project",
        "arguments": {"action": "project_export_png", "arguments": {"path": project, "output_path": png, "scale": 2}},
    })
    assert not exported.get("isError"), exported
    with open(png, "rb") as handle:
        header = handle.read(33)
    assert header[:8] == b"\x89PNG\r\n\x1a\n", "PNG 签名不正确"
    width, height = struct.unpack(">II", header[16:24])
    assert (width, height) == (16, 16), (width, height)

    guarded = call(5, "tools/call", {
        "name": "pixelcanvas_canvas",
        "arguments": {"action": "open_canvas", "arguments": {}},
    })
    assert guarded.get("isError"), guarded
    assert "Windows" in guarded["content"][0]["text"], guarded

    print(f"MCP smoke OK: {len(names)} tools, PNG {width}x{height}, realtime canvas guarded")
finally:
    proc.terminate()
    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()
