import argparse
import json
import queue
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request


class McpSseClient:
    def __init__(self, host, port):
        self.host = host
        self.port = port
        self.session_endpoint = None
        self._resp = None
        self._queue = queue.Queue()
        self._reader_thread = None
        self._running = False

    def connect(self):
        url = f"http://{self.host}:{self.port}/sse/"
        self._resp = urllib.request.urlopen(url, timeout=5)
        # Parse the SSE handshake (event: endpoint + data: /message?... then blank line)
        event = None
        data = None
        deadline = time.time() + 5.0
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
            raise RuntimeError("Timed out waiting for SSE endpoint.")

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

    def post(self, payload):
        if not self.session_endpoint:
            raise RuntimeError("No session endpoint established.")
        url = f"http://{self.host}:{self.port}{self.session_endpoint}"
        body = json.dumps(payload).encode("utf-8")
        req = urllib.request.Request(url, data=body, method="POST")
        req.add_header("Content-Type", "application/json")
        with urllib.request.urlopen(req) as resp:
            resp.read()

    def recv(self, timeout=5.0):
        try:
            msg = self._queue.get(timeout=timeout)
        except queue.Empty:
            return None
        if msg.get("event") == "endpoint":
            return None
        data = msg.get("data")
        if not data:
            return None
        try:
            return json.loads(data)
        except json.JSONDecodeError:
            return {"raw": data}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", default="3003")
    parser.add_argument("--run", help="Run commands in batch mode (newline separated).")
    parser.add_argument("--script", help="Run commands from a file in batch mode.")
    args = parser.parse_args()

    client = McpSseClient(args.host, args.port)
    client.connect()

    next_id = 1

    def send(method, params=None):
        nonlocal next_id
        payload = {"jsonrpc": "2.0", "id": next_id, "method": method}
        if params is not None:
            payload["params"] = params
        client.post(payload)
        next_id += 1

    def iter_commands():
        if args.run:
            for line in args.run.splitlines():
                yield line
            return
        if args.script:
            with open(args.script, "r", encoding="utf-8") as f:
                for line in f:
                    yield line.rstrip("\n")
            return
        while True:
            try:
                yield input("> ")
            except EOFError:
                return

    if not (args.run or args.script):
        print("Connected. Commands: init, tools, call <name> <json-args>, raw <json>, recv, exit")

    for raw_line in iter_commands():
        line = (raw_line or "").strip()
        line = line.lstrip("\ufeff")
        if not line:
            continue
        if line == "exit":
            break
        if line == "init":
            send("initialize", {"capabilities": {}})
            continue
        if line == "tools":
            send("tools/list")
            continue
        if line.startswith("call "):
            parts = line.split(" ", 2)
            if len(parts) < 2:
                print("Usage: call <name> <json-args>")
                continue
            name = parts[1]
            args_json = {}
            if len(parts) == 3 and parts[2].strip():
                args_json = json.loads(parts[2])
            send("tools/call", {"name": name, "arguments": args_json})
            continue
        if line.startswith("raw "):
            raw = line[4:].strip()
            payload = json.loads(raw)
            client.post(payload)
            continue
        if line == "recv":
            msg = client.recv(timeout=5.0)
            print(json.dumps(msg, indent=2))
            continue
        if line == "help":
            print("Commands: init, tools, call <name> <json-args>, raw <json>, recv, exit")
            continue
        print("Unknown command. Type 'help'.")

    client.close()


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print("Error:", exc)
        sys.exit(1)
