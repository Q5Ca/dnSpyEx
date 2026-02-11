# Progress

## 2026-02-11

- Added Streamable HTTP support for MCP (`POST /` and `POST /mcp`) while keeping legacy SSE (`/sse`, `/message`).
- Fixed MCP `tools/call` response shape to be MCP-compatible:
  - `content: [{ "type": "text", "text": ... }]`
  - `structuredContent`
  - `isError`
- Renamed exposed tool names to short MCP names (no `dnspy.` prefix), eg:
  - `attach`, `get_status`, `set_breakpoint_text`, `open_all_modules`, `open_file`, etc.
- Kept backward compatibility for existing clients by accepting legacy `dnspy.*` names in `tools/call`.
- Added and hardened `open_all_modules` flow and smoke-test sequencing to reduce race conditions.
- Extended `list_breakpoints` to include decompiled source mapping fields per breakpoint:
  - `decompiled_line_number`
  - `decompiled_line_text`
  - `decompiled_full_type_name`
  - `decompiled_method_name`
  - `decompiled_resolve_error` (best-effort diagnostics when mapping is unavailable)
- Updated `tools/mcp_smoke_test.py` to:
  - Resolve short names and legacy names
  - Parse both legacy (`content[0].json`) and current (`structuredContent`) tool responses
  - Optionally simulate agent delay between calls
  - Clear breakpoints and wait for module visibility before setting breakpoints
- Created Codex skill:
  - `C:/Users/Administrator/.codex/skills/dnspy-mcp-debugging`
  - Validated with skill validator.

## Verification

- Build:
  - `dotnet msbuild Extensions\\Examples\\Example1.Extension\\Example1.Extension.csproj /t:Build /p:Configuration=Debug /m`
  - If locked, stopped running `dnSpy.exe` and rebuilt successfully.
- Runtime checks:
  - `tools/list` now returns short tool names.
  - Legacy `dnspy.get_status` still works via alias normalization.
  - `list_breakpoints` now resolves and returns decompiled line info when module metadata is available in the Assembly Explorer tree.
  - Smoke test passes:
    - `python tools\\mcp_smoke_test.py --host 127.0.0.1 --port 3003 --spawn-debug-target --timeout-seconds 40 --simulate-agent-delay --agent-delay-seconds 1.0`
