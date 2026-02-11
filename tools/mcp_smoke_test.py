import argparse
import json
import os
import queue
import subprocess
import sys
import threading
import time
import urllib.request


class McpSseClient:
    def __init__(self, host: str, port: int):
        self.host = host
        self.port = port
        self.session_endpoint = None
        self._resp = None
        self._queue: "queue.Queue[dict]" = queue.Queue()
        self._reader_thread = None
        self._running = False

    def connect(self, timeout_s: float = 5.0):
        url = f"http://{self.host}:{self.port}/sse/"
        self._resp = urllib.request.urlopen(url, timeout=timeout_s)

        event = None
        data = None
        deadline = time.time() + timeout_s
        while time.time() < deadline:
            raw = self._resp.readline()
            if not raw:
                break
            line = raw.decode("utf-8", errors="replace").strip()
            if line == "":
                if event == "endpoint" and data:
                    self.session_endpoint = data
                    break
                event = None
                data = None
                continue
            if line.startswith("event:"):
                event = line.split(":", 1)[1].strip()
            elif line.startswith("data:"):
                data = line.split(":", 1)[1].strip()

        if not self.session_endpoint:
            raise RuntimeError("Timed out waiting for SSE endpoint")

        self._running = True
        self._reader_thread = threading.Thread(target=self._read_loop, daemon=True)
        self._reader_thread.start()

    def close(self):
        self._running = False
        try:
            if self._resp:
                self._resp.close()
        except Exception:
            pass

    def _read_loop(self):
        event = None
        data = None
        while self._running:
            try:
                raw = self._resp.readline()
            except TimeoutError:
                continue
            except Exception:
                break
            if not raw:
                break
            line = raw.decode("utf-8", errors="replace").strip()
            if line == "":
                if data is not None:
                    self._queue.put({"event": event, "data": data})
                event = None
                data = None
                continue
            if line.startswith("event:"):
                event = line.split(":", 1)[1].strip()
            elif line.startswith("data:"):
                data = line.split(":", 1)[1].strip()

    def _post(self, payload: dict):
        if not self.session_endpoint:
            raise RuntimeError("No session endpoint established")
        url = f"http://{self.host}:{self.port}{self.session_endpoint}"
        body = json.dumps(payload).encode("utf-8")
        req = urllib.request.Request(url, data=body, method="POST")
        req.add_header("Content-Type", "application/json")
        with urllib.request.urlopen(req) as resp:
            resp.read()

    def rpc(self, method: str, params: dict | None = None, timeout_s: float = 10.0):
        req_id = int(time.time() * 1000)  # good enough for a smoke test
        payload = {"jsonrpc": "2.0", "id": req_id, "method": method}
        if params is not None:
            payload["params"] = params
        self._post(payload)
        deadline = time.time() + timeout_s
        while time.time() < deadline:
            try:
                msg = self._queue.get(timeout=timeout_s)
            except queue.Empty:
                continue
            if msg.get("event") == "endpoint":
                continue
            data = msg.get("data")
            if not data:
                continue
            try:
                obj = json.loads(data)
            except json.JSONDecodeError:
                continue
            if obj.get("id") == req_id:
                return obj
        raise TimeoutError(f"Timed out waiting for response to {method} (id={req_id})")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=3003)
    ap.add_argument("--spawn-debug-target", action="store_true")
    ap.add_argument("--debug-target-exe", default="tools/McpDebugTarget/bin/Debug/net48/McpDebugTarget.exe")
    ap.add_argument("--delay-seconds", type=int, default=5)
    ap.add_argument("--iterations", type=int, default=0)
    ap.add_argument("--timeout-seconds", type=int, default=30)
    ap.add_argument("--set-breakpoint-retries", type=int, default=8)
    ap.add_argument("--simulate-agent-delay", action="store_true", help="Sleep between MCP calls to simulate agent thinking.")
    ap.add_argument("--agent-delay-seconds", type=float, default=1.0, help="Seconds to sleep between MCP calls when --simulate-agent-delay is enabled.")
    args = ap.parse_args()

    proc = None
    pid = None
    debug_target_exe = os.path.abspath(args.debug_target_exe)
    if args.spawn_debug_target:
        cmd = [
            debug_target_exe,
            f"--delay-seconds={args.delay_seconds}",
            f"--iterations={args.iterations}",
        ]
        proc = subprocess.Popen(cmd)
        pid = proc.pid

    c = McpSseClient(args.host, args.port)
    c.connect()
    first_rpc = True

    def rpc(method: str, params: dict | None = None, timeout_s: float = 10.0):
        nonlocal first_rpc
        if args.simulate_agent_delay and not first_rpc:
            delay_s = max(0.0, args.agent_delay_seconds)
            if delay_s > 0:
                time.sleep(delay_s)
        first_rpc = False
        return c.rpc(method, params, timeout_s=timeout_s)

    try:
        rpc("initialize", {"capabilities": {}}, timeout_s=10)
        tools = rpc("tools/list", {}, timeout_s=10)["result"]["tools"]
        tool_names = {t["name"] for t in tools}

        def call_tool(name: str, arguments: dict, timeout_s: float = 10.0):
            r = rpc("tools/call", {"name": name, "arguments": arguments}, timeout_s=timeout_s)
            result = r.get("result", {})
            if isinstance(result, dict):
                if "structuredContent" in result:
                    return result["structuredContent"]
                content = result.get("content")
                if isinstance(content, list) and len(content) > 0 and isinstance(content[0], dict):
                    if "json" in content[0]:
                        return content[0]["json"]
                    if "text" in content[0]:
                        txt = content[0]["text"]
                        if isinstance(txt, str):
                            try:
                                return json.loads(txt)
                            except Exception:
                                return txt
            return result

        def tname(base: str) -> str:
            if base in tool_names:
                return base
            legacy = f"dnspy.{base}"
            if legacy in tool_names:
                return legacy
            return base

        required = {
            tname("attach"),
            tname("break_all"),
            tname("continue"),
            tname("get_status"),
            tname("get_stack_trace"),
            tname("get_variables"),
            tname("evaluate_expression"),
            tname("get_method_debug_map"),
            tname("set_breakpoint_text"),
            tname("list_breakpoints"),
        }
        missing = sorted([n for n in required if n not in tool_names])
        if missing:
            print("Missing required tools:", missing)
            return 2

        def clear_all_breakpoints():
            list_bp = tname("list_breakpoints")
            remove_bp = tname("remove_breakpoint")
            if list_bp not in tool_names or remove_bp not in tool_names:
                return
            bp_json = call_tool(list_bp, {}, timeout_s=10)
            if not isinstance(bp_json, list):
                return
            removed = 0
            for bp in bp_json:
                if not isinstance(bp, dict):
                    continue
                bp_id = bp.get("id")
                if not isinstance(bp_id, int):
                    continue
                call_tool(remove_bp, {"id": bp_id}, timeout_s=10)
                removed += 1
            if removed:
                print("cleared_breakpoints:", removed)

        def module_loaded(target_module_name: str) -> bool:
            list_mods = tname("list_modules")
            if list_mods not in tool_names:
                return False
            mods = call_tool(list_mods, {}, timeout_s=10)
            if not isinstance(mods, list):
                return False
            target = target_module_name.lower()
            for m in mods:
                if not isinstance(m, dict):
                    continue
                mod_name = str(m.get("module_name") or "").lower()
                if mod_name == target:
                    return True
            return False

        clear_all_breakpoints()

        if pid is not None:
            attach_res = call_tool(tname("attach"), {"process_id": pid}, timeout_s=20)
            print("attach:", attach_res)
            clear_all_breakpoints()

            open_all_modules_error = None
            open_all = tname("open_all_modules")
            if open_all in tool_names:
                for _ in range(3):
                    open_all_modules_res = call_tool(open_all, {"process_id": pid, "wait_ms": 5000}, timeout_s=30)
                    print("open_all_modules:", open_all_modules_res)
                    if isinstance(open_all_modules_res, dict) and open_all_modules_res.get("error"):
                        open_all_modules_error = open_all_modules_res.get("error")
                    if module_loaded("McpDebugTarget.exe"):
                        open_all_modules_error = None
                        break
                    time.sleep(0.4)
            open_file = tname("open_file")
            if (open_all_modules_error is not None or open_all not in tool_names) and open_file in tool_names:
                print("open_file:", call_tool(open_file, {"path": debug_target_exe}, timeout_s=20))
            elif open_all not in tool_names:
                print(
                    "note: server does not expose dnspy.open_all_modules or dnspy.open_file; "
                    "dnspy.set_breakpoint_text searches dnSpy's Assembly Explorer tree. "
                    "If you get 'Method not found', open the target exe/dll in dnSpy UI (File -> Open) "
                    "or restart dnSpy with a newer extension build that includes dnspy.open_all_modules."
                )

            module_wait_deadline = time.time() + 12.0
            while time.time() < module_wait_deadline:
                if module_loaded("McpDebugTarget.exe"):
                    break
                time.sleep(0.35)

            bp_res = None
            for attempt in range(1, max(1, args.set_breakpoint_retries) + 1):
                bp_res = call_tool(
                    tname("set_breakpoint_text"),
                    {
                        "full_type_name": "McpDebugTarget.Worker",
                        "method_name": "Compute",
                        "decompiled_line_contains": "int sum = Add",
                        "occurrence": 1,
                        "assembly_name": "McpDebugTarget",
                        "module_path": debug_target_exe,
                    },
                    timeout_s=20,
                )
                if not (isinstance(bp_res, dict) and bp_res.get("error") == "Method not found."):
                    break
                if attempt < max(1, args.set_breakpoint_retries):
                    time.sleep(0.5)
            print("set_breakpoint_text:", bp_res)
            if isinstance(bp_res, dict) and bp_res.get("error") == "Method not found.":
                get_loaded = tname("get_loaded_assemblies")
                if get_loaded in tool_names:
                    try:
                        la_json = call_tool(get_loaded, {}, timeout_s=10)
                        print("get_loaded_assemblies:", la_json)
                    except Exception as exc:
                        print("get_loaded_assemblies: error:", str(exc))
                return 4

            debug_map_json = call_tool(
                tname("get_method_debug_map"),
                {
                    "full_type_name": "McpDebugTarget.Worker",
                    "method_name": "Compute",
                    "assembly_name": "McpDebugTarget",
                    "module_path": debug_target_exe,
                },
                timeout_s=20,
            )
            print("get_method_debug_map:", debug_map_json)
            if not isinstance(debug_map_json, dict) or debug_map_json.get("ok") is False:
                print("error: get_method_debug_map failed")
                return 12
            if not isinstance(debug_map_json.get("sequence_points"), list) or len(debug_map_json["sequence_points"]) == 0:
                print("error: get_method_debug_map has no sequence_points")
                return 13

            deadline = time.time() + args.timeout_seconds
            while time.time() < deadline:
                st_json = call_tool(tname("get_status"), {}, timeout_s=10)
                if st_json.get("state") == "break":
                    print("get_status (break):", st_json)
                    st_compact_json = call_tool(tname("get_status"), {"mode": "compact"}, timeout_s=10)
                    print("get_status (compact):", st_compact_json)
                    stack_json = call_tool(tname("get_stack_trace"), {"max_frames": 10}, timeout_s=10)
                    print("get_stack_trace:", stack_json)
                    stack_compact_json = call_tool(tname("get_stack_trace"), {"max_frames": 10, "mode": "compact"}, timeout_s=10)
                    print("get_stack_trace (compact):", stack_compact_json)

                    vars_json = call_tool(tname("get_variables"), {"frame_index": 0, "max_items": 100}, timeout_s=12)
                    print("get_variables:", vars_json)
                    vars_compact_json = call_tool(
                        tname("get_variables"),
                        {"frame_index": 0, "max_items": 100, "mode": "compact"},
                        timeout_s=12,
                    )
                    print("get_variables (compact):", vars_compact_json)

                    eval_json = call_tool(
                        tname("evaluate_expression"),
                        {"expression": "baseValue + value", "frame_index": 0},
                        timeout_s=12,
                    )
                    print("evaluate_expression:", eval_json)
                    eval_error_json = call_tool(
                        tname("evaluate_expression"),
                        {"expression": "baseValue +", "frame_index": 0},
                        timeout_s=12,
                    )
                    print("evaluate_expression (error):", eval_error_json)

                    if not isinstance(stack_json, dict) or not isinstance(stack_json.get("frames"), list) or len(stack_json["frames"]) == 0:
                        print("error: get_stack_trace returned no frames")
                        return 5
                    if not isinstance(stack_compact_json, dict) or stack_compact_json.get("mode") != "compact":
                        print("error: get_stack_trace compact mode missing")
                        return 8
                    if not isinstance(vars_json, dict) or not isinstance(vars_json.get("variables"), list) or len(vars_json["variables"]) == 0:
                        print("error: get_variables returned no variables")
                        return 6
                    if not isinstance(vars_compact_json, dict) or vars_compact_json.get("mode") != "compact":
                        print("error: get_variables compact mode missing")
                        return 9
                    if not isinstance(eval_json, dict) or eval_json.get("ok") is not True:
                        print("error: evaluate_expression failed")
                        return 7
                    if not isinstance(eval_error_json, dict) or eval_error_json.get("ok") is not False or "error_code" not in eval_error_json:
                        print("error: evaluate_expression error_code missing")
                        return 10
                    if not isinstance(st_compact_json, dict) or "last_process_id" not in st_compact_json or "last_thread_id" not in st_compact_json:
                        print("error: get_status compact missing stable ids")
                        return 11
                    return 0
                time.sleep(0.5)
            print("get_status (timeout):", call_tool(tname("get_status"), {}, timeout_s=10))
            return 3

        # If not spawning a target, just print tool list for basic sanity.
        print("tools:", sorted(tool_names))
        return 0
    finally:
        c.close()
        if proc is not None:
            try:
                proc.terminate()
            except Exception:
                pass


if __name__ == "__main__":
    sys.exit(main())
