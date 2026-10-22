#!/usr/bin/env python3
"""Disposable, public-only W4 native OS process resource boundary experiment.

This is NOT a production sandbox, a PTY or a permit for untrusted code.
It never reads private sources, ChatGPT credentials or browser state.
Only trusted, fixed Python child fixtures are executed on disposable runners.
"""
import ctypes
import json
import os
import platform
import subprocess
import sys
import tempfile
import time

SCHEMA = "suprachat-w4-resource-boundary-rdte/v1"
MEMORY_LIMIT = 256 * 1024 * 1024
FIXTURE_TIMEOUT = 12

def child(code, *, before=None, stdin=None):
    opts = {
        "stdin": stdin if stdin is not None else subprocess.DEVNULL,
        "stdout": subprocess.PIPE,
        "stderr": subprocess.PIPE,
        "text": True,
    }
    if before is not None:
        opts["preexec_fn"] = before
    return subprocess.Popen([sys.executable, "-c", code], **opts)

def unix_probe():
    import resource
    result = {"kind": "unix_rlimit", "platform": platform.system(),
              "hard_resource_limits_available": False,
              "readiness": "EXPERIMENTAL_NOT_ADMITTED"}
    limits = {}
    for name, code, threshold, expected in (
        ("address_space", "import sys\ntry:\n b=bytearray(512*1024*1024)\n b[::4096]=b'1'*len(b[::4096])\n print('ALLOCATED')\nexcept MemoryError:\n print('MEMORY_REFUSED')\n sys.exit(77)", MEMORY_LIMIT, "MEMORY_REFUSED"),
        ("file_size", "import os,sys\ntry:\n f=open(sys.argv[0]+'.w4-owned','wb')\n f.write(b'Z'*(2*1024*1024))\n print('WROTE')\nexcept OSError:\n print('WRITE_REFUSED')\n sys.exit(77)", 1024*1024, "WRITE_REFUSED"),
    ):
        spec = resource.RLIMIT_AS if name == "address_space" else resource.RLIMIT_FSIZE
        if name == "file_size":
            with tempfile.TemporaryDirectory(prefix="suprachat-w4-owned-") as scratch:
                # No actual source file in this namespace; only fixed disposable files.
                escaped = repr(os.path.join(scratch, "fixture"))
                code = code.replace("sys.argv[0]+'.w4-owned'", escaped)
                limits[name] = run_unix_limit(spec, threshold, code, expected)
        else:
            limits[name] = run_unix_limit(spec, threshold, code, expected)
    if platform.system() == "Darwin" and hasattr(resource, "RLIMIT_DATA"):
        # RLIMIT_DATA and RLIMIT_AS are different limits: never silently
        # substitute a file-size or data-segment result for a proven RSS cap.
        same_fixed_memory_fixture = (
            "import sys\ntry:\n b=bytearray(512*1024*1024)\n"
            " b[::4096]=b'1'*len(b[::4096])\n print('ALLOCATED')\n"
            "except MemoryError:\n print('MEMORY_REFUSED')\n sys.exit(77)"
        )
        limits["data_segment"] = run_unix_limit(
            resource.RLIMIT_DATA, MEMORY_LIMIT, same_fixed_memory_fixture, "MEMORY_REFUSED"
        )
    result["observations"] = limits
    result["memory_limit_observed"] = any(
        limits[key]["result"] == "LIMIT_OBSERVED"
        for key in ("address_space", "data_segment") if key in limits
    )
    result["file_size_limit_observed"] = limits["file_size"]["result"] == "LIMIT_OBSERVED"
    result["cpu_quota_verified"] = False
    result["process_tree_containment_verified"] = False
    result["hard_resource_limits_available"] = (
        result["memory_limit_observed"] or result["file_size_limit_observed"]
    )
    result["cgroup_v2_controllers"] = []
    controller_path = "/sys/fs/cgroup/cgroup.controllers"
    if os.path.exists(controller_path):
        with open(controller_path, "rt", encoding="utf-8") as stream:
            result["cgroup_v2_controllers"] = stream.read(1024).split()
    result["cgroup_write_admitted"] = False
    return result

def run_unix_limit(kind, threshold, code, marker):
    import resource
    def enforce():
        resource.setrlimit(kind, (threshold, threshold))
    try:
        proc = child(code, before=enforce)
        try:
            stdout, stderr = proc.communicate(timeout=FIXTURE_TIMEOUT)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.communicate()
            return {"result": "TIMEOUT_KILLED"}
    except Exception as exc:
        return {"result": "SETUP_REFUSED", "error_type": type(exc).__name__}
    if marker in stdout:
        classification = "LIMIT_OBSERVED"
    elif "ALLOCATED" in stdout or "WROTE" in stdout:
        classification = "LIMIT_NOT_ENFORCED"
    else:
        classification = "INCONCLUSIVE_EXIT"
    return {"result": classification, "child_exit_code": proc.returncode,
            "fixture_stdout_label": stdout.strip()[:80],
            "stderr_was_nonempty": bool(stderr)}

def windows_probe():
    from ctypes import wintypes
    result = {"kind": "windows_job_object", "platform": "Windows",
              "hard_resource_limits_available": False,
              "memory_limit_observed": False,
              "file_size_limit_observed": False,
              "cpu_quota_verified": False,
              "process_tree_containment_verified": False,
              "readiness": "EXPERIMENTAL_NOT_ADMITTED"}
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    HANDLE = wintypes.HANDLE
    SIZE_T = ctypes.c_size_t

    class Basic(ctypes.Structure):
        _fields_ = [
            ("PerProcessUserTimeLimit", ctypes.c_longlong),
            ("PerJobUserTimeLimit", ctypes.c_longlong),
            ("LimitFlags", wintypes.DWORD),
            ("MinimumWorkingSetSize", SIZE_T),
            ("MaximumWorkingSetSize", SIZE_T),
            ("ActiveProcessLimit", wintypes.DWORD),
            ("Affinity", SIZE_T),
            ("PriorityClass", wintypes.DWORD),
            ("SchedulingClass", wintypes.DWORD),
        ]
    class Counters(ctypes.Structure):
        _fields_ = [(name, ctypes.c_ulonglong) for name in (
            "ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
            "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]
    class Limits(ctypes.Structure):
        _fields_ = [
            ("BasicLimitInformation", Basic),
            ("IoInfo", Counters),
            ("ProcessMemoryLimit", SIZE_T),
            ("JobMemoryLimit", SIZE_T),
            ("PeakProcessMemoryUsed", SIZE_T),
            ("PeakJobMemoryUsed", SIZE_T),
        ]
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = HANDLE
    kernel.SetInformationJobObject.argtypes = [HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.SetInformationJobObject.restype = wintypes.BOOL
    kernel.AssignProcessToJobObject.argtypes = [HANDLE, HANDLE]
    kernel.AssignProcessToJobObject.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    h = kernel.CreateJobObjectW(None, None)
    if not h:
        result["observation"] = "CREATE_REFUSED"
        return result

    fixture = None
    try:
        settings = Limits()
        # Process memory cap, single active process and kill all owned
        # members when the owning Job Object handle closes.
        settings.BasicLimitInformation.LimitFlags = 0x100 | 0x8 | 0x2000
        settings.BasicLimitInformation.ActiveProcessLimit = 1
        settings.ProcessMemoryLimit = MEMORY_LIMIT
        if not kernel.SetInformationJobObject(h, 9, ctypes.byref(settings), ctypes.sizeof(settings)):
            result["observation"] = "SET_LIMITS_REFUSED"
            result["os_error"] = ctypes.get_last_error()
            return result

        # Fixed child blocks on stdin until it is assigned. No untrusted code.
        code = ("import sys\n"
                "sys.stdin.buffer.read(1)\n"
                "try:\n"
                " b=bytearray(512*1024*1024)\n"
                " b[::4096]=b'1'*len(b[::4096])\n"
                " print('ALLOCATED',flush=True)\n"
                "except MemoryError:\n"
                " print('MEMORY_REFUSED',flush=True)\n"
                " sys.exit(77)\n")
        fixture = subprocess.Popen(
            [sys.executable, "-c", code],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True,
            creationflags=subprocess.CREATE_NO_WINDOW)
        if not kernel.AssignProcessToJobObject(h, HANDLE(int(fixture._handle))):
            result["observation"] = "ASSIGN_REFUSED"
            result["os_error"] = ctypes.get_last_error()
            return result
        fixture.stdin.write("A")
        fixture.stdin.flush()
        stdout, stderr = fixture.communicate(timeout=FIXTURE_TIMEOUT)
        if "MEMORY_REFUSED" in stdout:
            result["observation"] = "PROCESS_MEMORY_CAP_OBSERVED"
            result["hard_resource_limits_available"] = True
            result["memory_limit_observed"] = True
        elif "ALLOCATED" in stdout:
            result["observation"] = "PROCESS_MEMORY_CAP_NOT_ENFORCED"
        else:
            result["observation"] = "INCONCLUSIVE_EXIT"
        result["child_exit_code"] = fixture.returncode
        result["fixture_stdout_label"] = stdout.strip()[:80]
        result["stderr_was_nonempty"] = bool(stderr)
        return result
    except subprocess.TimeoutExpired:
        result["observation"] = "CHILD_TIMEOUT_KILLED"
        return result
    finally:
        # Kill-on-close limits residual process lifetime even after test errors.
        kernel.CloseHandle(h)
        if fixture is not None and fixture.poll() is None:
            fixture.kill()
            fixture.communicate(timeout=5)

def main():
    if os.environ.get("SUPRACHAT_W4_ALLOW_FIXED_FIXTURE") != "yes":
        raise SystemExit("Explicit hosted fixed-fixture opt-in required")
    start = time.monotonic()
    observation = windows_probe() if os.name == "nt" else unix_probe()
    output = {
        "schema": SCHEMA,
        "os": platform.system(),
        "arch": platform.machine(),
        "disposable_public_runner_only": True,
        "operator_machine_touched": False,
        "container_engine_used": False,
        "private_source_used": False,
        "real_chatgpt_access": False,
        "resource_quota_release_admitted": False,
        "operator_terminal_commands_executed": False,
        "duration_seconds": round(time.monotonic() - start, 2),
        "experiment": observation,
    }
    print(json.dumps(output, indent=2, sort_keys=True))
    if observation["readiness"] != "EXPERIMENTAL_NOT_ADMITTED":
        raise SystemExit("Unsafe admission drift")

if __name__ == "__main__":
    main()
