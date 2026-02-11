I want to create a mcp server for this dnspy. update your progress to PROGRESS.md

I want it is an extension of dnSpy. and I think we not making it run headless now because it is harder (dnSpy is GUI)

expected usage is: 
- I open dnSpy in foreground
- the mcp start listening on http port
- I connect AI to it to use

Decisions/clarifications:
- MCP transport: choose best/state-of-the-art for MCP over HTTP (SSE or WebSocket if that is the current standard).
- It is loaded at startup via `--extension-directory` (dnSpy extension).
- If breakpoint-by-text matches multiple lines, return an error that asks for more specific filters.
- `get_status` should include overall debugger state (running/break/attached/etc.) plus `last_break`.
- Use MCP-style tool names.
- Test program: create a small C# console app to debug.

The functions I need to be exposed:
- attach(process_id)
- list_breakpoint() -> list all breakpoint in dnSpy, include ID
-  set_breakpoint_text
    Set a breakpoint by decompiled text anchor.

    Params:
    - `full_type_name` (string, required)
    - `method_name` (string, required)
    - `decompiled_line_contains` (string, required)
    - `occurrence` (int, optional, default 1)
    - Optional module filters to disambiguate:
    - `assembly_name` (string, short or full)
    - `module_name` (string)
    - `module_path` (string)

    Returns:
    - `id` (breakpoint id)
    - `il_offset`, `token_hex`, `module_name`, `assembly_full_name`
    - `line_number`, `line_text` (decompiled line info)

- remove_breakpoint
    Params:
    - `id` (int, required)

- continue | break_all | step_over | step_into | step_out
    No params.

- get_status
    No params. Returns current debugger state and `last_break`.

    `last_break` example:
    - `kind`: `breakpoint`, `exception`, `step_complete`, `program_break`, `entry_point_break`, `break`
    - `breakpoint_id` (int or null)
    - `thread_id` (ulong or null)
    - `exception` (object or null)
    - `message` (string or null)
    - `utc` (timestamp)

-  detach
    No params.



I also need other functions that you can get implementation from folder C:\dev\dnSpyEx_dnSpy\.tmp\DnSpy-MCPserver-Extension:
- Get_Loaded_Assemblies 
- Classes_From_Namespace
- Get_Class_Sourcecode 
- Get_Method_Prototypes
- Get_Method_SourceCode 
- Get_Function_Opcodes 
- 



you can rename the function to make it easiest for GPT to use

And I also want a script (maybe python) that I can use for quick interactive with the mcp (not through AI), I will also use it will AI complain mcp have error for testing.

Code and test for me. Include a small program to test debug it. make sure everything work before stop.

use msbuild to compile. if build fail because file/folder on the default target build is locked. stop the running dnSpy process to unlock.