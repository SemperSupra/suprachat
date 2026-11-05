#!/usr/bin/env python3
"""Bounded offline RDTE for upstream tunnel-client's own dev proxy and MCP stub.

No OpenAI Platform endpoint, API keys, credentials, signed-in browser, public
listener, user data or external MCP implementation is involved. Linux CI only.
The only observable output is a fixed PASS marker; no ports or URLs are logged.
"""
import json
import os
import queue
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request


def capture_lines(process):
    # TextIOWrapper buffers several Go-printed lines at once; select() on the
    # file descriptor incorrectly reports no unread bytes after the first line.
    # A single dedicated reader makes each buffered line observable.
    result=queue.Queue(maxsize=80)
    def consume():
        try:
            for line in process.stdout:
                result.put(line.strip())
        finally:
            result.put(None)
    threading.Thread(target=consume,daemon=True).start()
    return result


def read_line_with_deadline(process, stream, deadline):
    while time.monotonic() < deadline:
        try:
            line=stream.get(timeout=min(0.5, max(0.01, deadline-time.monotonic())))
        except queue.Empty:
            if process.poll() is not None:
                raise RuntimeError("UPSTREAM_DEV_PROCESS_EXITED")
            continue
        if line is None:
            raise RuntimeError("UPSTREAM_DEV_STDOUT_CLOSED")
        return line
    raise RuntimeError("UPSTREAM_DEV_START_TIMEOUT")


def admitted_loopback(url):
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or not 1 <= (parsed.port or 0) <= 65535:
        raise RuntimeError("NON_LOOPBACK_DEV_ENDPOINT")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise RuntimeError("UNSAFE_DEV_ENDPOINT")
    return url


def rpc(url, method, params=None, session=None, msg_id=1):
    payload={"jsonrpc": "2.0", "method": method}
    if msg_id is not None:
        payload["id"]=msg_id
    if params is not None:
        payload["params"]=params
    headers={"Content-Type":"application/json","Accept":"application/json, text/event-stream"}
    if session:
        headers["Mcp-Session-Id"]=session
    req=urllib.request.Request(url,method="POST",headers=headers,data=json.dumps(payload).encode("utf-8"))
    with urllib.request.urlopen(req,timeout=12) as r:
        content=r.read(65536).decode("utf-8")
        response_headers=r.headers
    if not content.strip():
        return {},response_headers
    data_lines=[ln.strip()[6:] for ln in content.splitlines() if ln.startswith("data: ")]
    return json.loads(data_lines[-1] if data_lines else content),response_headers


def stop(proc):
    if proc is None or proc.poll() is not None:
        return
    proc.terminate()
    try:
        proc.wait(timeout=4)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=4)


def main(binary):
    # Do not leak or inherit live credentials even if mistakenly present.
    env=dict(os.environ)
    for k in ("CONTROL_PLANE_API_KEY","CONTROL_PLANE_TUNNEL_ID","OPENAI_ADMIN_KEY","OPENAI_API_KEY","GITHUB_TOKEN"):
        env.pop(k,None)
    stub=None
    proxy=None
    try:
        stub=subprocess.Popen([binary,"dev","mcp-stub","--listen-addr","127.0.0.1:0"],
                              stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,
                              text=True,bufsize=1,env=env)
        stub_lines=capture_lines(stub)
        stub_url=None
        deadline=time.monotonic()+20
        for _ in range(16):
            line=read_line_with_deadline(stub,stub_lines,deadline)
            if line.startswith("MCP URL: "):
                stub_url=admitted_loopback(line[len("MCP URL: "):])
                break
        if stub_url is None:
            raise RuntimeError("UPSTREAM_STUB_MCP_URL_MISSING")
        proxy=subprocess.Popen([
            binary,"dev","proxy","--mcp-server-url",stub_url,
            "--listen","127.0.0.1:0","--print-json",
            "--backend","go","--engine-queue-backend","inmem",
            "--duration","32s","--readiness-timeout","12s",
        ],stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,bufsize=1,env=env)
        proxy_lines=capture_lines(proxy)
        chunks=[]
        connection=None
        deadline=time.monotonic()+23
        for _ in range(50):
            chunks.append(read_line_with_deadline(proxy,proxy_lines,deadline))
            try:
                connection=json.loads("\n".join(chunks))
                break
            except json.JSONDecodeError:
                if sum(len(v) for v in chunks)>8192:
                    raise RuntimeError("UNBOUNDED_UPSTREAM_DEV_PROJECTION")
        if connection is None:
            raise RuntimeError("UPSTREAM_PROXY_PROJECTION_MISSING")
        url=admitted_loopback(connection.get("mcp_url",""))
        if connection.get("backend")!="go-in-memory":
            raise RuntimeError("UNEXPECTED_UPSTREAM_PROXY_BACKEND")
        initialized,headers=rpc(url,"initialize",{
            "protocolVersion":"2025-06-18",
            "capabilities":{"sampling":{},"roots":{"listChanged":True}},
            "clientInfo":{"name":"oec-offline-synthetic-rdte","version":"0.1.0"}
        },msg_id="init")
        if not isinstance(initialized.get("result"),dict):
            raise RuntimeError("MCP_INITIALIZE_FAILED")
        session=headers.get("Mcp-Session-Id")
        if not session or len(session)>256:
            raise RuntimeError("MCP_SESSION_HEADER_UNAVAILABLE")
        rpc(url,"notifications/initialized",{},session,msg_id=None)
        tools,_=rpc(url,"tools/list",{},session,msg_id="list")
        names={x.get("name") for x in tools.get("result",{}).get("tools",[])}
        if names!={"server_info","echo","uppercase"}:
            raise RuntimeError("UNEXPECTED_MCP_TOOL_CATALOG")
        sentinel="PUBLIC-SYNTHETIC-TUNNEL-PROXY-QUALIFIED"
        answer,_=rpc(url,"tools/call",{"name":"echo","arguments":{"input":sentinel}},session,msg_id="call")
        result=answer.get("result",{})
        contents=result.get("content",[])
        echoed=(result.get("structuredContent") or {}).get("echoed")
        if echoed!=sentinel and not any(x.get("text")==sentinel for x in contents):
            raise RuntimeError("LOCAL_PROXY_ECHO_MISMATCH")
        print("QUALIFIED_UPSTREAM_LOCAL_INMEM_PROXY mcp_init=true tool_catalog=true echo=true external_connection=false")
    finally:
        stop(proxy)
        stop(stub)


if __name__=="__main__":
    if len(sys.argv)!=2:
        raise SystemExit("BINARY_REQUIRED")
    main(sys.argv[1])
