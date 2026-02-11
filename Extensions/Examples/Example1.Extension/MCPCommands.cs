using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Metadata;
using dnSpy.Contracts.Text;
using static Example1.Extension.SimpleMcpServer;

namespace Example1.Extension {
	static class MCPCommands {
		static bool AssemblyMatches(ModuleDef mod, string assemblyName) {
			var asm = mod.Assembly;
			if (asm == null)
				return false;
			if (string.Equals(asm.Name.String, assemblyName, StringComparison.OrdinalIgnoreCase))
				return true;
			if (string.Equals(asm.FullName, assemblyName, StringComparison.OrdinalIgnoreCase))
				return true;
			return false;
		}

		static string? GetAssemblySimpleName(ModuleDef mod) => mod.Assembly?.Name.String;

		static T RunOnUI<T>(Func<T> action) {
			var app = Global.MyAppWindow;
			if (app == null)
				return action();
			var disp = app.MainWindow.Dispatcher;
			if (disp.CheckAccess())
				return action();
			var evt = new ManualResetEventSlim(false);
			T result = default!;
			Exception? error = null;
			disp.BeginInvoke(new Action(() => {
				try {
					result = action();
				}
				catch (Exception ex) {
					error = ex;
				}
				finally {
					evt.Set();
				}
			}));
			evt.Wait(TimeSpan.FromSeconds(10));
			if (error != null)
				throw error;
			return result;
		}

		[Command("attach", MCPCmdDescription = "Attach to a process by ID.")]
		public static object Attach(int process_id) {
			if (Global.AttachableProcessesService == null || Global.DbgManager == null)
				return new { error = "Debugger services not available." };

			var processes = Global.AttachableProcessesService
				.GetAttachableProcessesAsync(null, new[] { process_id }, null)
				.GetAwaiter().GetResult();

			if (processes.Length == 0)
				return new { error = "No attachable process found with that ID." };
			if (processes.Length > 1)
				return new { error = "Multiple attachable processes found. Be more specific." };

			var proc = processes[0];
			RunOnDbgDispatcher(() => {
				Global.DebugState.ClearLastBreak();
				proc.Attach();
				return 0;
			});
			return new {
				process_id = proc.ProcessId,
				name = proc.Name,
				runtime = proc.RuntimeName,
				architecture = proc.Architecture.ToString(),
				os = proc.OperatingSystem.ToString()
			};
		}

		[Command("list_breakpoints", MCPCmdDescription = "List all breakpoints with IDs and locations.")]
		public static object ListBreakpoints() {
			if (Global.DbgCodeBreakpointsService == null || Global.DbgManager == null)
				return new { error = "Breakpoint service not available." };

			var snapshots = RunOnDbgDispatcher(() => {
				var list = new List<BreakpointSnapshot>();
				foreach (var bp in Global.DbgCodeBreakpointsService.Breakpoints) {
					var loc = bp.Location as IDbgDotNetCodeLocation;
					var msg = bp.BoundBreakpointsMessage;
					var bound = bp.BoundBreakpoints.Select(bb => new {
						process_id = bb.Process.Id,
						runtime_id = bb.Runtime.Id,
						module_name = bb.Module?.Name,
						module_filename = bb.Module?.Filename,
						address = bb.HasAddress ? "0x" + bb.Address.ToString("X") : null,
						message = new { severity = bb.Message.Severity.ToString(), message = bb.Message.Message }
					}).ToArray();
					list.Add(new BreakpointSnapshot {
						Id = bp.Id,
						Enabled = bp.IsEnabled,
						BoundBreakpoints = bound,
						BoundBreakpointsMessageSeverity = msg.Severity.ToString(),
						BoundBreakpointsMessageText = msg.Message,
						Token = loc?.Token,
						ILOffset = loc?.Offset,
						ModuleName = loc?.Module.ModuleName,
						AssemblyFullName = loc?.Module.AssemblyFullName,
					});
				}
				return list;
			});

			var cache = new Dictionary<string, DecompiledLocationInfo>(StringComparer.OrdinalIgnoreCase);
			var result = new List<object>(snapshots.Count);
			foreach (var bp in snapshots) {
				DecompiledLocationInfo? dec = null;
				if (bp.Token != null && bp.ILOffset != null && !string.IsNullOrWhiteSpace(bp.ModuleName) && !string.IsNullOrWhiteSpace(bp.AssemblyFullName)) {
					var key = bp.AssemblyFullName + "|" + bp.ModuleName + "|" + bp.Token.Value.ToString("X8") + "|" + bp.ILOffset.Value;
					if (!cache.TryGetValue(key, out dec)) {
						dec = ResolveDecompiledLocation(bp.AssemblyFullName!, bp.ModuleName!, bp.Token.Value, bp.ILOffset.Value, includeContext: false);
						cache[key] = dec;
					}
				}

				result.Add(new {
					id = bp.Id,
					enabled = bp.Enabled,
					bound_breakpoints_count = bp.BoundBreakpoints.Length,
					bound_breakpoints_message = new { severity = bp.BoundBreakpointsMessageSeverity, message = bp.BoundBreakpointsMessageText },
					bound_breakpoints = bp.BoundBreakpoints,
					location = bp.Token == null || bp.ILOffset == null ? null : new {
						token_hex = "0x" + bp.Token.Value.ToString("X8"),
						il_offset = bp.ILOffset.Value,
						module_name = bp.ModuleName,
						assembly_full_name = bp.AssemblyFullName
					},
					decompiled_line_number = dec?.LineNumber,
					decompiled_line_text = dec?.LineText,
					decompiled_full_type_name = dec?.FullTypeName,
					decompiled_method_name = dec?.MethodName,
					decompiled_resolve_error = dec?.Error
				});
			}
			return result;
		}

		[Command("set_breakpoint_text", MCPCmdDescription = "Set a breakpoint by decompiled text anchor. Optional selectors: module_id, module_path, process_id + assembly_name, or assembly_name.")]
		public static object SetBreakpointText(
			string full_type_name,
			string method_name,
			string decompiled_line_contains,
			int? occurrence = null,
			string? assembly_name = null,
			string? module_name = null,
			string? module_path = null,
			int? process_id = null,
			string? module_id = null) {

			if (Global.MyTreeView == null || Global.MyAppWindow == null || Global.MyDocumentTabService == null)
				return ErrorResult("ui_services_not_available", "dnSpy UI services not available.");
			if (Global.DbgCodeBreakpointsService == null || Global.DbgDotNetCodeLocationFactory == null || Global.ModuleIdProvider == null)
				return ErrorResult("debugger_services_not_available", "Debugger services not available.");

			var methodResult = FindMethod(full_type_name, method_name, assembly_name, module_name, module_path, process_id, module_id);
			if (methodResult.Error != null) {
				if (string.Equals(methodResult.ErrorCode, "ambiguous_target", StringComparison.OrdinalIgnoreCase)) {
					return new {
						ok = false,
						error_code = "ambiguous_target",
						error = methodResult.Error,
						candidates = methodResult.Candidates ?? Array.Empty<object>()
					};
				}
				return ErrorResult(methodResult.ErrorCode ?? "method_lookup_failed", methodResult.Error);
			}

			var selected = methodResult.Candidate!;
			var method = selected.Method;
			var module = method.Module;
			var selectedOutputPath = !string.IsNullOrWhiteSpace(module_path)
				? NormalizePath(module_path)
				: (!string.IsNullOrWhiteSpace(selected.RuntimeModulePath) ? selected.RuntimeModulePath : selected.ModulePath);

			var snapshot = GetMethodDecompiledSnapshot(method, out var contentError);
			if (snapshot == null)
				return ErrorResult("decompiled_content_unavailable", contentError ?? "Failed to get decompiled content.");

			var debugInfo = FindMethodDebugInfo(snapshot.MethodDebugInfos, method);
			if (debugInfo == null)
				return ErrorResult("method_debug_info_unavailable", "Method debug info not available for the target method.");

			var matches = FindMatchingLines(snapshot.Text, debugInfo, decompiled_line_contains);
			if (matches.Count == 0)
				return ErrorResult("decompiled_line_not_found", "No matching decompiled line found.", new {
					match_text = decompiled_line_contains,
					module_id = selected.ModuleId,
					process_id = selected.ProcessId,
					module_path = selectedOutputPath
				});

			if (occurrence == null && matches.Count > 1) {
				return ErrorResult("ambiguous_line_match", "Multiple matching lines found. Provide more specific text or filters.", new {
					match_count = matches.Count
				});
			}

			int chosenIndex = 0;
			if (occurrence != null) {
				if (occurrence <= 0 || occurrence > matches.Count)
					return ErrorResult("invalid_occurrence", "Occurrence is out of range.", new {
						match_count = matches.Count
					});
				chosenIndex = occurrence.Value - 1;
			}

			var match = matches[chosenIndex];
			var pos = match.LineStart + match.LineText.IndexOf(decompiled_line_contains, StringComparison.Ordinal);
			var statement = debugInfo.GetSourceStatementByTextOffset(match.LineStart, match.LineEnd, pos);
			if (statement == null)
				return ErrorResult("il_mapping_failed", "Failed to map the line to IL.");

			var ilOffset = statement.Value.ILSpan.Start;
			var token = method.MDToken.Raw;
			var moduleIdValue = selected.ModuleIdValue;
			var modulePathHint = selectedOutputPath;
			var runtimeModuleId = TryResolveRuntimeModuleId(modulePathHint, process_id ?? selected.ProcessId);
			if (runtimeModuleId != null)
				moduleIdValue = runtimeModuleId.Value;
			// Use Approximate mapping: dnSpy's own breakpoint placement often uses approximate mapping
			// and it improves reliability across small decompiler/IL mapping differences.
			var location = Global.DbgDotNetCodeLocationFactory.Create(moduleIdValue, token, ilOffset, DbgILOffsetMapping.Approximate);
			var existing = RunOnDbgDispatcher(() => FindExistingCodeBreakpoint(moduleIdValue, token, ilOffset));
			if (existing != null) {
				return new {
					id = existing.Id,
					module_id = selected.ModuleId,
					process_id = selected.ProcessId,
					module_path = modulePathHint,
					il_offset = ilOffset,
					token_hex = "0x" + token.ToString("X8"),
					module_name = module.Name.String,
					assembly_full_name = module.Assembly.FullName,
					line_number = match.LineNumber,
					line_text = match.LineText,
					already_exists = true
				};
			}
			var settings = new DbgCodeBreakpointSettings { IsEnabled = true };
			var added = RunOnDbgDispatcher(() =>
				Global.DbgCodeBreakpointsService.Add(new DbgCodeBreakpointInfo(location, settings))
			);
			if (added == null) {
				var existingAfterAdd = RunOnDbgDispatcher(() => FindExistingCodeBreakpoint(moduleIdValue, token, ilOffset));
				if (existingAfterAdd != null) {
					return new {
						id = existingAfterAdd.Id,
						module_id = selected.ModuleId,
						process_id = selected.ProcessId,
						module_path = modulePathHint,
						il_offset = ilOffset,
						token_hex = "0x" + token.ToString("X8"),
						module_name = module.Name.String,
						assembly_full_name = module.Assembly.FullName,
						line_number = match.LineNumber,
						line_text = match.LineText,
						already_exists = true
					};
				}
				return ErrorResult("breakpoint_add_failed", "Failed to add breakpoint.", new {
					module_id = selected.ModuleId,
					process_id = selected.ProcessId,
					module_path = modulePathHint,
					token_hex = "0x" + token.ToString("X8"),
					il_offset = ilOffset
				});
			}

			return new {
				id = added.Id,
				module_id = selected.ModuleId,
				process_id = selected.ProcessId,
				module_path = modulePathHint,
				il_offset = ilOffset,
				token_hex = "0x" + token.ToString("X8"),
				module_name = module.Name.String,
				assembly_full_name = module.Assembly.FullName,
				line_number = match.LineNumber,
				line_text = match.LineText,
				already_exists = false
			};
		}

		[Command("get_method_debug_map", MCPCmdDescription = "Get method debug map for a decompiled method: sequence points (IL->line/col), locals (slot/scope), and args (index/name mapping).")]
		public static object GetMethodDebugMap(
			string full_type_name,
			string method_name,
			string? assembly_name = null,
			string? module_name = null,
			string? module_path = null) {

			if (Global.MyTreeView == null || Global.MyAppWindow == null || Global.MyDocumentTabService == null)
				return ErrorResult("ui_services_not_available", "dnSpy UI services not available.");

			var methodResult = FindMethod(full_type_name, method_name, assembly_name, module_name, module_path);
			if (methodResult.Error != null) {
				if (string.Equals(methodResult.ErrorCode, "ambiguous_target", StringComparison.OrdinalIgnoreCase)) {
					return new {
						ok = false,
						error_code = "ambiguous_target",
						error = methodResult.Error,
						candidates = methodResult.Candidates ?? Array.Empty<object>()
					};
				}
				return ErrorResult("method_lookup_failed", methodResult.Error);
			}

			var method = methodResult.Candidate!.Method;
			var snapshot = GetMethodDecompiledSnapshot(method, out var contentError);
			if (snapshot == null)
				return ErrorResult("decompiled_content_unavailable", contentError ?? "Failed to get decompiled content.");

			var debugInfo = FindMethodDebugInfo(snapshot.MethodDebugInfos, method);
			if (debugInfo == null)
				return ErrorResult("method_debug_info_unavailable", "Method debug info not available for the target method.");

			var lineStarts = BuildLineStarts(snapshot.Text);
			var sequencePoints = debugInfo.Statements.Select(st => {
				var start = GetLineColumnByOffset(lineStarts, snapshot.Text.Length, st.TextSpan.Start);
				var end = GetLineColumnByOffset(lineStarts, snapshot.Text.Length, st.TextSpan.End);
				return (object)new {
					il_offset = st.ILSpan.Start,
					il_end = st.ILSpan.End,
					line = start.line,
					col = start.col,
					end_line = end.line,
					end_col = end.col
				};
			}).ToArray();

			var locals = new List<object>();
			CollectScopeLocals(debugInfo.Scope, locals);

			var args = debugInfo.Parameters
				.OrderBy(p => p.Parameter.MethodSigIndex)
				.Select(p => {
					var runtimeName = string.IsNullOrWhiteSpace(p.Parameter.Name) ? null : p.Parameter.Name;
					return (object)new {
						index = p.Parameter.MethodSigIndex,
						runtime_name = runtimeName,
						decompiled_name = p.Name,
						pdb_name = runtimeName,
						is_hidden_this = p.Parameter.IsHiddenThisParameter,
						type = p.Type.FullName,
						hoisted_field = p.HoistedField?.FullName,
					};
				})
				.ToArray();

			return new {
				full_type_name = method.DeclaringType?.FullName,
				method_name = method.Name.String,
				token_hex = "0x" + method.MDToken.Raw.ToString("X8"),
				module_name = method.Module.Name.String,
				module_path = method.Module.Location,
				assembly_full_name = method.Module.Assembly?.FullName,
				text_span_end_is_exclusive = true,
				sequence_points = sequencePoints,
				locals,
				args,
			};
		}

		[Command("remove_breakpoint", MCPCmdDescription = "Remove a breakpoint by ID.")]
		public static object RemoveBreakpoint(int id) {
			if (Global.DbgCodeBreakpointsService == null || Global.DbgManager == null)
				return new { error = "Breakpoint service not available." };

			return RunOnDbgDispatcher(() => {
				var bp = Global.DbgCodeBreakpointsService.Breakpoints.FirstOrDefault(b => b.Id == id);
				if (bp == null)
					return (object)new { error = "Breakpoint not found." };
				Global.DbgCodeBreakpointsService.Remove(bp);
				return (object)new { ok = true };
			});
		}

		[Command("continue", MCPCmdDescription = "Continue execution.")]
		public static object Continue() => RunManagerAction(mgr => mgr.RunAll());

		[Command("break_all", MCPCmdDescription = "Break all debugged processes.")]
		public static object BreakAll() => RunManagerAction(mgr => mgr.BreakAll());

		[Command("step_over", MCPCmdDescription = "Step over in decompiled view.")]
		public static object StepOver() => Step(DbgStepKind.StepOver);

		[Command("step_into", MCPCmdDescription = "Step into in decompiled view.")]
		public static object StepInto() => Step(DbgStepKind.StepInto);

		[Command("step_out", MCPCmdDescription = "Step out in decompiled view.")]
		public static object StepOut() => Step(DbgStepKind.StepOut);

		[Command("get_status", MCPCmdDescription = "Get debugger status. Optional args: mode ('verbose' or 'compact'), include_context (default true in verbose, false in compact). Returns current_frame_kind and stable paired IDs for current_* and last_* identities.")]
		public static object GetStatus(string? mode = null, bool? include_context = null) {
			if (Global.DbgManager == null)
				return ErrorResult("debugger_not_available", "Debugger not available.");

			return RunOnDbgDispatcher(() => {
				var mgr = Global.DbgManager!;
				bool compact = string.Equals(mode, "compact", StringComparison.OrdinalIgnoreCase);
				bool includeContext = include_context ?? !compact;
				var state = mgr.IsDebugging
					? (mgr.IsRunning == true ? "running" : mgr.IsRunning == false ? "break" : "mixed")
					: "detached";
				var last = Global.DebugState.GetLastBreak();
				object? lastObj = null;
				if (last != null) {
					lastObj = new {
						kind = last.Kind,
						breakpoint_id = last.BreakpointId,
						process_id = last.ProcessId,
						thread_id = last.ThreadId,
						exception = last.Exception,
						step = last.Step,
						message = last.Message,
						utc = last.Utc.ToString("o")
					};
				}

				var statusThread = ResolveStatusThread(mgr, last);
				int? currentProcessId = statusThread?.Process.Id;
				ulong? currentThreadId = statusThread?.Id;
				var lastIdentity = ResolveStableLastIdentity(mgr, last, statusThread);
				int? lastProcessId = lastIdentity.processId;
				ulong? lastThreadId = lastIdentity.threadId;
				string? currentFrameKind = GetCurrentFrameKind(statusThread);

				CurrentStopInfo? currentStop = null;
				object? currentStopObj = null;
				if (mgr.IsDebugging && mgr.IsRunning == false) {
					currentStop = ResolveCurrentStop(statusThread, includeContext);
					if (currentStop != null)
						currentStopObj = ToCurrentStopPayload(currentStop, includeContext);
				}

				if (compact) {
					return (object)new {
						state,
						is_debugging = mgr.IsDebugging,
						is_running = mgr.IsRunning,
						reason = last?.Kind,
						current_process_id = currentProcessId,
						current_thread_id = currentThreadId,
						current_frame_kind = currentFrameKind,
						last_process_id = lastProcessId,
						last_thread_id = lastThreadId,
						last_break = lastObj == null ? null : new {
							kind = last!.Kind,
							breakpoint_id = last.BreakpointId,
							process_id = last.ProcessId,
							thread_id = last.ThreadId,
							message = last.Message,
							utc = last.Utc.ToString("o")
						},
						current_stop = currentStopObj
					};
				}

				return (object)new {
					state,
					is_debugging = mgr.IsDebugging,
					is_running = mgr.IsRunning,
					current_process_id = currentProcessId,
					current_thread_id = currentThreadId,
					current_frame_kind = currentFrameKind,
					last_process_id = lastProcessId,
					last_thread_id = lastThreadId,
					last_break = lastObj,
					current_stop = currentStopObj
				};
			});
		}

		[Command("get_stack_trace", MCPCmdDescription = "Get formatted call stack frames. Optional args: thread_id, max_frames, mode ('verbose' or 'compact').")]
		public static object GetStackTrace(ulong? thread_id = null, int? max_frames = null, string? mode = null) {
			if (Global.DbgManager == null)
				return ErrorResult("debugger_not_available", "Debugger not available.");
			if (Global.DbgLanguageService == null)
				return ErrorResult("language_service_unavailable", "Language service not available.");

			return RunOnDbgDispatcher(() => {
				var mgr = Global.DbgManager!;
				if (!mgr.IsDebugging)
					return (object)ErrorResult("not_debugging", "Not debugging.");
				bool compact = string.Equals(mode, "compact", StringComparison.OrdinalIgnoreCase);

				var threadResult = GetTargetThread(mgr, thread_id);
				if (threadResult.error != null)
					return (object)ErrorResult("thread_not_found", threadResult.error!);
				var thread = threadResult.thread!;

				int maxFrames = max_frames ?? 20;
				if (maxFrames <= 0)
					maxFrames = 1;
				if (maxFrames > 256)
					maxFrames = 256;

				var frames = thread.GetFrames(maxFrames);
				try {
					var language = Global.DbgLanguageService!.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
					var valueOptions = DbgValueFormatterOptions.Decimal | DbgValueFormatterOptions.IntrinsicTypeKeywords | DbgValueFormatterOptions.Namespaces;
					var frameOptions =
						DbgStackFrameFormatterOptions.ParameterTypes |
						DbgStackFrameFormatterOptions.ParameterNames |
						DbgStackFrameFormatterOptions.DeclaringTypes |
						DbgStackFrameFormatterOptions.Namespaces |
						DbgStackFrameFormatterOptions.IntrinsicTypeKeywords |
						DbgStackFrameFormatterOptions.Decimal;
					var rows = new List<object>(frames.Length);
					for (int i = 0; i < frames.Length; i++) {
						var frame = frames[i];
						var dotnetLoc = frame.Location as IDbgDotNetCodeLocation;
						string? tokenHex = null;
						uint? ilOffset = null;
						string? moduleName = frame.Module?.Name;
						string? assemblyFullName = null;
						if (dotnetLoc != null) {
							tokenHex = "0x" + dotnetLoc.Token.ToString("X8");
							ilOffset = dotnetLoc.Offset;
							moduleName = dotnetLoc.Module.ModuleName;
							assemblyFullName = dotnetLoc.Module.AssemblyFullName;
						}
						else if (frame.HasFunctionToken) {
							tokenHex = "0x" + frame.FunctionToken.ToString("X8");
							ilOffset = frame.FunctionOffset;
						}

						if (compact) {
							rows.Add(new {
								frame_index = i,
								is_active = i == 0,
								display = FormatFrame(language, frame, frameOptions, valueOptions),
								module_name = moduleName,
								token_hex = tokenHex,
								il_offset = ilOffset
							});
							continue;
						}

						rows.Add(new {
							frame_index = i,
							is_active = i == 0,
							display = FormatFrame(language, frame, frameOptions, valueOptions),
							thread_id = frame.Thread.Id,
							module_name = moduleName,
							module_path = frame.Module?.Filename,
							assembly_full_name = assemblyFullName,
							token_hex = tokenHex,
							il_offset = ilOffset,
							flags = frame.Flags.ToString()
						});
					}

					return (object)new {
						thread_id = thread.Id,
						managed_thread_id = thread.ManagedId,
						mode = compact ? "compact" : "verbose",
						frame_count = rows.Count,
						frames = rows
					};
				}
				finally {
					CloseFrames(frames);
				}
			});
		}

		[Command("get_variables", MCPCmdDescription = "Get locals/arguments with formatted values at a frame. Optional args: thread_id, frame_index, include_compiler_generated, include_decompiler_generated, show_raw_locals, no_func_eval, max_items, mode ('verbose' or 'compact').")]
		public static object GetVariables(
			ulong? thread_id = null,
			int? frame_index = null,
			bool? include_compiler_generated = null,
			bool? include_decompiler_generated = null,
			bool? show_raw_locals = null,
			bool? no_func_eval = null,
			int? max_items = null,
			string? mode = null) {
			if (Global.DbgManager == null)
				return ErrorResult("debugger_not_available", "Debugger not available.");
			if (Global.DbgLanguageService == null)
				return ErrorResult("language_service_unavailable", "Language service not available.");

			return RunOnDbgDispatcher(() => {
				var mgr = Global.DbgManager!;
				if (!mgr.IsDebugging)
					return (object)ErrorResult("not_debugging", "Not debugging.");
				bool compact = string.Equals(mode, "compact", StringComparison.OrdinalIgnoreCase);

				var threadResult = GetTargetThread(mgr, thread_id);
				if (threadResult.error != null)
					return (object)ErrorResult("thread_not_found", threadResult.error!);
				var thread = threadResult.thread!;

				int frameIndex = frame_index ?? 0;
				if (frameIndex < 0)
					return (object)ErrorResult("invalid_frame_index", "frame_index must be >= 0.");

				var frames = thread.GetFrames(frameIndex + 1);
				if (frames.Length <= frameIndex) {
					CloseFrames(frames);
					return (object)ErrorResult("frame_out_of_range", "frame_index is out of range.", new { frame_count = frames.Length });
				}

				var frame = frames[frameIndex];
				DbgEvaluationContext? context = null;
				DbgLocalsValueNodeInfo[] locals = Array.Empty<DbgLocalsValueNodeInfo>();
				try {
					var language = Global.DbgLanguageService!.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
					context = language.CreateContext(frame, cancellationToken: CancellationToken.None);
					var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);

					var nodeOptions = (no_func_eval ?? true) ? DbgValueNodeEvaluationOptions.NoFuncEval : DbgValueNodeEvaluationOptions.None;
					var localsOptions = DbgLocalsValueNodeEvaluationOptions.None;
					if (include_compiler_generated ?? false)
						localsOptions |= DbgLocalsValueNodeEvaluationOptions.ShowCompilerGeneratedVariables;
					if (include_decompiler_generated ?? false)
						localsOptions |= DbgLocalsValueNodeEvaluationOptions.ShowDecompilerGeneratedVariables;
					if (show_raw_locals ?? false)
						localsOptions |= DbgLocalsValueNodeEvaluationOptions.ShowRawLocals;

					locals = language.LocalsProvider.GetNodes(evalInfo, nodeOptions, localsOptions);
					int maxItems = max_items ?? 200;
					if (maxItems <= 0)
						maxItems = 1;
					if (maxItems > 1000)
						maxItems = 1000;

					var valueOptions = DbgValueFormatterOptions.Decimal | DbgValueFormatterOptions.IntrinsicTypeKeywords | DbgValueFormatterOptions.Namespaces;
					if ((nodeOptions & DbgValueNodeEvaluationOptions.NoFuncEval) == 0)
						valueOptions |= DbgValueFormatterOptions.FuncEval | DbgValueFormatterOptions.ToString;
					var nameOptions = DbgValueFormatterOptions.IntrinsicTypeKeywords | DbgValueFormatterOptions.Namespaces;
					var typeOptions = DbgValueFormatterTypeOptions.Decimal | DbgValueFormatterTypeOptions.IntrinsicTypeKeywords | DbgValueFormatterTypeOptions.Namespaces | DbgValueFormatterTypeOptions.Tokens;

					var rows = new List<object>(Math.Min(locals.Length, maxItems));
					for (int i = 0; i < locals.Length && i < maxItems; i++) {
						var local = locals[i];
						rows.Add(FormatLocal(evalInfo, local, nameOptions, valueOptions, typeOptions, compact));
					}

					return (object)new {
						thread_id = thread.Id,
						frame_index = frameIndex,
						mode = compact ? "compact" : "verbose",
						total_count = locals.Length,
						returned_count = rows.Count,
						truncated = locals.Length > rows.Count,
						variables = rows
					};
				}
				finally {
					if (locals.Length != 0)
						CloseValueNodes(locals.Select(a => a.ValueNode));
					context?.Close();
					CloseFrames(frames);
				}
			});
		}

		[Command("evaluate_expression", MCPCmdDescription = "Evaluate expression/code in current debug frame. Optional args: thread_id, frame_index, is_expression, no_side_effects, no_func_eval. Errors include machine-readable error_code.")]
		public static object EvaluateExpression(
			string expression,
			ulong? thread_id = null,
			int? frame_index = null,
			bool? is_expression = null,
			bool? no_side_effects = null,
			bool? no_func_eval = null) {
			if (Global.DbgManager == null)
				return ErrorResult("debugger_not_available", "Debugger not available.");
			if (Global.DbgLanguageService == null)
				return ErrorResult("language_service_unavailable", "Language service not available.");
			if (string.IsNullOrWhiteSpace(expression))
				return ErrorResult("missing_expression", "expression is required.");

			return RunOnDbgDispatcher(() => {
				var mgr = Global.DbgManager!;
				if (!mgr.IsDebugging)
					return (object)ErrorResult("not_debugging", "Not debugging.");

				var threadResult = GetTargetThread(mgr, thread_id);
				if (threadResult.error != null)
					return (object)ErrorResult("thread_not_found", threadResult.error!);
				var thread = threadResult.thread!;

				int frameIndex = frame_index ?? 0;
				if (frameIndex < 0)
					return (object)ErrorResult("invalid_frame_index", "frame_index must be >= 0.");

				var frames = thread.GetFrames(frameIndex + 1);
				if (frames.Length <= frameIndex) {
					CloseFrames(frames);
					return (object)ErrorResult("frame_out_of_range", "frame_index is out of range.", new { frame_count = frames.Length });
				}

				var frame = frames[frameIndex];
				DbgEvaluationContext? context = null;
				DbgValue? value = null;
				try {
					var language = Global.DbgLanguageService!.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
					context = language.CreateContext(frame, cancellationToken: CancellationToken.None);
					var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);

					var options = (is_expression ?? true) ? DbgEvaluationOptions.Expression : DbgEvaluationOptions.None;
					if (no_side_effects ?? false)
						options |= DbgEvaluationOptions.NoSideEffects;
					if (no_func_eval ?? false)
						options |= DbgEvaluationOptions.NoFuncEval;

					var evalRes = language.ExpressionEvaluator.Evaluate(evalInfo, expression, options, state: null);
					if (evalRes.Error != null) {
						var code = InferEvaluateErrorCode(evalRes.Error, options);
						return (object)new {
							ok = false,
							expression,
							frame_index = frameIndex,
							thread_id = thread.Id,
							error = evalRes.Error,
							error_code = code,
							flags = evalRes.Flags.ToString()
						};
					}

					value = evalRes.Value!;
					var valueOptions = DbgValueFormatterOptions.Decimal | DbgValueFormatterOptions.IntrinsicTypeKeywords | DbgValueFormatterOptions.Namespaces;
					if ((options & DbgEvaluationOptions.NoFuncEval) == 0)
						valueOptions |= DbgValueFormatterOptions.FuncEval | DbgValueFormatterOptions.ToString;
					var typeOptions = DbgValueFormatterTypeOptions.Decimal | DbgValueFormatterTypeOptions.IntrinsicTypeKeywords | DbgValueFormatterTypeOptions.Namespaces | DbgValueFormatterTypeOptions.Tokens;

					var valueText = FormatValue(language, evalInfo, value, valueOptions);
					var typeText = FormatType(language, evalInfo, value, typeOptions);

					object? rawValue = null;
					if (value.HasRawValue)
						rawValue = NormalizeRawValue(value.RawValue);

					return (object)new {
						ok = true,
						expression,
						frame_index = frameIndex,
						thread_id = thread.Id,
						value = valueText,
						type = typeText,
						raw_value = rawValue,
						simple_value_type = value.ValueType.ToString(),
						is_thrown_exception = evalRes.IsThrownException,
						flags = evalRes.Flags.ToString(),
						format_specifiers = evalRes.FormatSpecifiers.ToArray()
					};
				}
				finally {
					value?.Close();
					context?.Close();
					CloseFrames(frames);
				}
			});
		}

		[Command("detach", MCPCmdDescription = "Detach from all debugged processes.")]
		public static object Detach() {
			if (Global.DbgManager == null)
				return new { error = "Debugger not available." };
			return RunOnDbgDispatcher(() => {
				if (!Global.DbgManager.IsDebugging)
					return (object)new { error = "Not debugging." };
				Global.DbgManager.DetachAll();
				Global.DebugState.ClearLastBreak();
				return (object)new { ok = true };
			});
		}

		[Command("get_loaded_assemblies", MCPCmdDescription = "Get all loaded assemblies.")]
		public static string Get_Loaded_Assemblies() {
			if (Global.MyTreeView == null)
				return "TreeView not available.";
			return RunOnUI(() => {
				var sb = new StringBuilder();
				foreach (var modNode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null)
						continue;
					var asmName = GetAssemblySimpleName(mod);
					if (!string.IsNullOrEmpty(asmName))
						sb.AppendLine(asmName);
				}
				return sb.ToString();
			});
		}

		[Command("list_modules", MCPCmdDescription = "List loaded module nodes (module name/path + assembly info).")]
		public static object List_Modules() {
			if (Global.MyTreeView == null)
				return new { error = "TreeView not available." };
			return RunOnUI(() => {
				var list = new List<object>();
				foreach (var modNode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null)
						continue;
					var asm = mod.Assembly;
					list.Add(new {
						module_name = mod.Name.String,
						module_path = mod.Location,
						assembly_name = asm?.Name.String,
						assembly_full_name = asm?.FullName
					});
				}
				return (object)list;
			});
		}

		[Command("open_all_modules", MCPCmdDescription = "Load all modules from the attached debuggee process(es) into Assembly Explorer.")]
		public static object Open_All_Modules(int? process_id = null, bool? include_dynamic = null, bool? include_in_memory = null, int? wait_ms = null) {
			if (Global.MyTreeView == null)
				return new { error = "TreeView not available." };
			if (Global.DbgManager == null)
				return new { error = "Debugger not available." };

			bool allowDynamic = include_dynamic ?? false;
			bool allowInMemory = include_in_memory ?? false;
			int waitMilliseconds = wait_ms ?? 6000;
			if (waitMilliseconds < 0)
				waitMilliseconds = 0;

			var sw = Stopwatch.StartNew();
			List<DebugModuleCandidate> dbgModules = new List<DebugModuleCandidate>();
			do {
				dbgModules = RunOnDbgDispatcher(() => CollectDebugModules(process_id));
				if (dbgModules.Count != 0)
					break;
				if (waitMilliseconds == 0)
					break;
				Thread.Sleep(150);
			} while (sw.ElapsedMilliseconds < waitMilliseconds);

			if (dbgModules.Count == 0) {
				if (process_id != null)
					return new { error = "No modules found for target process. Is it attached and running managed code?", process_id };
				return new { error = "No debuggee modules found. Attach first." };
			}

			var loaded = new List<object>();
			var skipped = new List<object>();
			var failed = new List<object>();
			var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			RunOnUI(() => {
				foreach (var m in dbgModules) {
					var modulePath = m.ModulePath;
					var moduleName = m.ModuleName ?? string.Empty;
					var processIdValue = m.ProcessId;
					var isDynamic = m.IsDynamic;
					var isInMemory = m.IsInMemory;

					if (isDynamic && !allowDynamic) {
						skipped.Add(new { module_name = moduleName, module_path = modulePath, process_id = processIdValue, reason = "dynamic_module" });
						continue;
					}
					if (isInMemory && !allowInMemory) {
						skipped.Add(new { module_name = moduleName, module_path = modulePath, process_id = processIdValue, reason = "in_memory_module" });
						continue;
					}
					if (string.IsNullOrWhiteSpace(modulePath)) {
						skipped.Add(new { module_name = moduleName, module_path = modulePath, process_id = processIdValue, reason = "no_module_path" });
						continue;
					}

					string normalizedPath;
					try {
						normalizedPath = Path.GetFullPath(modulePath);
					}
					catch {
						normalizedPath = modulePath;
					}

					if (!File.Exists(normalizedPath)) {
						skipped.Add(new { module_name = moduleName, module_path = normalizedPath, process_id = processIdValue, reason = "file_not_found" });
						continue;
					}
					if (!expectedPaths.Add(normalizedPath))
						continue;

					try {
						var doc = Global.MyTreeView.DocumentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(normalizedPath));
						if (doc == null) {
							failed.Add(new { module_name = moduleName, module_path = normalizedPath, process_id = processIdValue, reason = "document_load_failed" });
							continue;
						}
						loaded.Add(new {
							module_name = moduleName,
							module_path = normalizedPath,
							process_id = processIdValue,
							process_name = m.ProcessName,
							filename = doc.Filename
						});
					}
					catch (Exception ex) {
						failed.Add(new { module_name = moduleName, module_path = normalizedPath, process_id = processIdValue, reason = ex.Message });
					}
				}
				return 0;
			});

			var pending = new List<string>();
			if (waitMilliseconds > 0 && expectedPaths.Count > 0) {
				var deadline = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
				while (DateTime.UtcNow < deadline) {
					var present = RunOnUI(() => {
						var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
						foreach (var modNode in Global.MyTreeView!.GetAllModuleNodes().ToList()) {
							var mod = modNode.GetModule();
							if (mod == null)
								continue;
							if (string.IsNullOrWhiteSpace(mod.Location))
								continue;
							try {
								hs.Add(Path.GetFullPath(mod.Location));
							}
							catch {
								hs.Add(mod.Location);
							}
						}
						return hs;
					});

					pending = expectedPaths.Where(p => !present.Contains(p)).ToList();
					if (pending.Count == 0)
						break;
					Thread.Sleep(150);
				}
			}

			return new {
				ok = true,
				process_id,
				debug_modules_count = dbgModules.Count,
				requested_load_count = expectedPaths.Count,
				loaded_count = loaded.Count,
				skipped_count = skipped.Count,
				failed_count = failed.Count,
				pending_count = pending.Count,
				loaded,
				skipped,
				failed,
				pending
			};
		}

		[Command("classes_from_namespace", MCPCmdDescription = "List classes under a namespace. Returns structured rows deduped by (module_id, full_type_name). Optional process_id can scope results to a debuggee process. module_path prefers runtime module path when available.")]
		public static object Classes_From_Namespace(string assemblyName, string namespaceName, int? process_id = null) {
			if (Global.MyTreeView == null)
				return ErrorResult("treeview_not_available", "TreeView not available.");
			if (Global.ModuleIdProvider == null)
				return ErrorResult("module_id_provider_not_available", "Module ID provider not available.");

			var runtimeModules = CollectRuntimeModuleInfos();
			var rows = RunOnUI(() => {
				var list = new List<object>();
				var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

				foreach (var modNode in Global.MyTreeView!.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null)
						continue;
					if (!AssemblyMatches(mod, assemblyName))
						continue;

					var normalizedPath = NormalizePath(mod.Location);
					var runtimeMatches = runtimeModules
						.Where(r => RuntimeModuleMatchesModule(r, mod, normalizedPath))
						.ToArray();
					if (process_id != null)
						runtimeMatches = runtimeMatches.Where(r => r.ProcessId == process_id.Value).ToArray();

					string fallbackModuleId = ModuleIdToStableString(Global.ModuleIdProvider!.Create(mod));
					foreach (var t in mod.GetTypes().OrderBy(a => a.FullName, StringComparer.OrdinalIgnoreCase)) {
						if (!string.Equals(t.Namespace, namespaceName, StringComparison.Ordinal))
							continue;

						if (runtimeMatches.Length == 0) {
							if (process_id != null)
								continue;
							var k = fallbackModuleId + "|" + t.FullName;
							if (!dedupe.Add(k))
								continue;
							list.Add(new {
								full_type_name = t.FullName,
								module_id = fallbackModuleId,
								module_path = normalizedPath,
								assembly_name = mod.Assembly?.Name.String,
								process_id = (int?)null
							});
							continue;
						}

						foreach (var match in runtimeMatches) {
							var k = match.ModuleId + "|" + t.FullName;
							if (!dedupe.Add(k))
								continue;
							list.Add(new {
								full_type_name = t.FullName,
								module_id = match.ModuleId,
								module_path = match.ModulePath,
								assembly_name = mod.Assembly?.Name.String,
								process_id = match.ProcessId
							});
						}
					}
				}

				return list;
			});

			return new {
				ok = true,
				classes = rows
			};
		}

		[Command("get_class_sourcecode", MCPCmdDescription = "Get a class decompiled source.")]
		public static string Get_Class_Sourcecode(string assemblyName, string namespaceName, string className) {
			if (Global.MyTreeView == null)
				return "TreeView not available.";
			return RunOnUI(() => {
				foreach (var modNode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null)
						continue;
					if (!AssemblyMatches(mod, assemblyName))
						continue;
					var types = mod.GetTypes().ToList();
					foreach (var t in types) {
						if (t.Namespace == namespaceName && t.Name == className)
							return TheExtension.DumpSource(modNode, t);
					}
				}
				return "Class not found.";
			});
		}

		[Command("get_method_prototypes", MCPCmdDescription = "List all method prototypes from a class.")]
		public static string Get_Method_Prototypes(string assemblyName, string namespaceName, string className) {
			if (Global.MyTreeView == null)
				return "TreeView not available.";
			return RunOnUI(() => {
				var sb = new StringBuilder();
				foreach (var modNode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null)
						continue;
					if (!AssemblyMatches(mod, assemblyName))
						continue;
					foreach (var type in mod.GetTypes()) {
						if (type.Namespace == namespaceName && type.Name == className) {
							foreach (var method in type.Methods.OrderBy(m => m.Name.String, StringComparer.OrdinalIgnoreCase))
								sb.AppendLine(method.FullName);
							return sb.ToString();
						}
					}
				}
				return "Class not found.";
			});
		}

		[Command("get_method_sourcecode", MCPCmdDescription = "Get a method decompiled source.")]
		public static string Get_Method_SourceCode(string assemblyName, string namespaceName, string className, string methodName) {
			if (Global.MyTreeView == null)
				return "TreeView not available.";
			return RunOnUI(() => {
				foreach (var modNode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null)
						continue;
					if (!AssemblyMatches(mod, assemblyName))
						continue;
					foreach (var type in mod.GetTypes()) {
						if (type.Namespace == namespaceName && type.Name == className) {
							var method = type.Methods.FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
							if (method != null)
								return TheExtension.DumpSource(modNode, method);
						}
					}
				}
				return "Method not found.";
			});
		}

		[Command("get_function_opcodes", MCPCmdDescription = "Get IL opcodes for a method.")]
		public static string Get_Function_Opcodes(string assemblyName, string namespaceName, string className, string methodName) {
			if (Global.MyTreeView == null)
				return "TreeView not available.";

			return RunOnUI(() => {
				foreach (var modNode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null)
						continue;
					if (!AssemblyMatches(mod, assemblyName))
						continue;
					foreach (var type in mod.GetTypes()) {
						if (type.Namespace == namespaceName && type.Name == className) {
							var method = type.Methods.FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
							if (method == null)
								return "Method not found.";

							if (!method.HasBody)
								return "Method has no body.";

							var sb = new StringBuilder();
							sb.AppendLine("// IL for " + method.FullName);
							sb.AppendLine("// #    Offset   OpCode     Operand");
							sb.AppendLine("// --------------------------------------------");
							int i = 1;
							foreach (var instr in method.Body.Instructions) {
								var offset = instr.Offset.ToString("X4");
								var op = instr.OpCode.Name;
								var operand = instr.Operand == null ? "" : instr.Operand.ToString();
								sb.AppendLine(string.Format("{0,-4} {1,-7} {2,-9} {3}", i, offset, op, operand));
								i++;
							}
							return sb.ToString();
						}
					}
				}
				return "Method not found.";
			});
		}

		[Command("open_file", MCPCmdDescription = "Open a file in dnSpy's Assembly Explorer (loads into the tree view).")]
		public static object Open_File(string path, bool? select = null) {
			if (Global.MyAppWindow == null || Global.MyTreeView == null)
				return new { error = "dnSpy UI services not available." };
			if (string.IsNullOrWhiteSpace(path))
				return new { error = "Path is required." };
			if (!File.Exists(path))
				return new { error = "File not found.", path };

			bool doSelect = select ?? true;
			object result = new { ok = true, path };

			var evt = new ManualResetEventSlim(false);
			Exception? error = null;
			Global.MyAppWindow.MainWindow.Dispatcher.BeginInvoke(new Action(() => {
				try {
					var doc = Global.MyTreeView.DocumentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(path));
					if (doc == null) {
						result = new { error = "Failed to load document.", path };
						return;
					}
					if (doSelect) {
						var node = Global.MyTreeView.FindNode(doc);
						if (node != null)
							Global.MyTreeView.TreeView.SelectItems(new[] { node });
					}
					result = new { ok = true, filename = doc.Filename };
				}
				catch (Exception ex) {
					error = ex;
				}
				finally {
					evt.Set();
				}
			}));
			evt.Wait(TimeSpan.FromSeconds(10));
			if (error != null)
				return new { error = error.Message };
			return result;
		}

		static object RunManagerAction(Action<DbgManager> action) {
			if (Global.DbgManager == null)
				return new { error = "Debugger not available." };
			return RunOnDbgDispatcher(() => {
				if (!Global.DbgManager.IsDebugging)
					return (object)new { error = "Not debugging." };
				action(Global.DbgManager);
				return (object)new { ok = true };
			});
		}

		static object Step(DbgStepKind kind) {
			if (Global.DbgManager == null)
				return ErrorResult("debugger_not_available", "Debugger not available.");
			return RunOnDbgDispatcher(() => {
				if (!Global.DbgManager.IsDebugging)
					return (object)ErrorResult("not_debugging", "Not debugging.");
				var thread = Global.DbgManager.CurrentThread.Current;
				if (thread == null)
					return (object)ErrorResult("thread_not_available", "No current thread.");

				var stepCheck = GetStepReadiness(thread);
				if (!stepCheck.can_step) {
					return (object)ErrorResult(
						"step_not_possible_here",
						stepCheck.reason ?? "Step is not possible at the current frame.",
						new {
							step_kind = kind.ToString(),
							current_frame_kind = stepCheck.frame_kind,
							location_type = stepCheck.location_type,
							il_offset_mapping = stepCheck.il_offset_mapping
						}
					);
				}

				Global.DebugState.RecordStepRequest(kind.ToString(), thread);
				var stepper = thread.CreateStepper();
				stepper.Step(kind, autoClose: true);
				return (object)new { ok = true };
			});
		}

		sealed class MethodLookupResult {
			public MethodCandidate? Candidate { get; set; }
			public string? Error { get; set; }
			public string? ErrorCode { get; set; }
			public object[]? Candidates { get; set; }
		}

		sealed class MethodBaseCandidate {
			public MethodDef Method { get; set; } = null!;
			public string ModulePath { get; set; } = "";
			public string ModuleName { get; set; } = "";
			public string AssemblySimpleName { get; set; } = "";
			public string AssemblyFullName { get; set; } = "";
		}

		sealed class MethodCandidate {
			public MethodDef Method { get; set; } = null!;
			public string ModulePath { get; set; } = "";
			public string RuntimeModulePath { get; set; } = "";
			public string ModuleName { get; set; } = "";
			public string AssemblySimpleName { get; set; } = "";
			public string AssemblyFullName { get; set; } = "";
			public int? ProcessId { get; set; }
			public ModuleId ModuleIdValue { get; set; }
			public string ModuleId { get; set; } = "";
		}

		sealed class RuntimeModuleInfo {
			public int ProcessId { get; set; }
			public string ModulePath { get; set; } = "";
			public string ModuleName { get; set; } = "";
			public string AssemblyFullName { get; set; } = "";
			public ModuleId ModuleIdValue { get; set; }
			public string ModuleId { get; set; } = "";
		}

		static MethodLookupResult FindMethod(
			string fullTypeName,
			string methodName,
			string? assemblyName,
			string? moduleName,
			string? modulePath,
			int? processId = null,
			string? moduleId = null) {
			if (Global.MyTreeView == null)
				return new MethodLookupResult { Error = "TreeView not available." };
			if (Global.ModuleIdProvider == null)
				return new MethodLookupResult { Error = "Module ID provider not available." };

			var baseCandidates = RunOnUI(() => {
				var list = new List<MethodBaseCandidate>();
				foreach (var modNode in Global.MyTreeView!.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null)
						continue;

					if (!string.IsNullOrWhiteSpace(moduleName) &&
						!string.Equals(mod.Name, moduleName, StringComparison.OrdinalIgnoreCase))
						continue;

					var normalizedPath = NormalizePath(mod.Location);

					var asmSimple = mod.Assembly?.Name.String ?? string.Empty;
					var asmFull = mod.Assembly?.FullName ?? string.Empty;
					foreach (var type in mod.GetTypes()) {
						if (!string.Equals(type.FullName, fullTypeName, StringComparison.Ordinal))
							continue;
						foreach (var method in type.Methods) {
							if (!string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
								continue;
							list.Add(new MethodBaseCandidate {
								Method = method,
								ModulePath = normalizedPath,
								ModuleName = mod.Name.String ?? string.Empty,
								AssemblySimpleName = asmSimple,
								AssemblyFullName = asmFull
							});
						}
					}
				}
				return list;
			});

			if (baseCandidates.Count == 0)
				return new MethodLookupResult { Error = "Method not found.", ErrorCode = "method_not_found" };

			var runtimeModules = CollectRuntimeModuleInfos();
			var expanded = ExpandMethodCandidates(baseCandidates, runtimeModules);
			var filtered = ApplyMethodCandidateFilters(expanded, assemblyName, moduleName, modulePath, processId, moduleId);

			if (filtered.Count == 0)
				return new MethodLookupResult { Error = "Method not found.", ErrorCode = "method_not_found" };

			if (filtered.Count > 1) {
				return new MethodLookupResult {
					Error = "Multiple matching methods found.",
					ErrorCode = "ambiguous_target",
					Candidates = filtered
						.Select(c => (object)new {
							module_id = c.ModuleId,
							process_id = c.ProcessId,
							module_path = c.ModulePath,
							runtime_module_path = !string.IsNullOrWhiteSpace(c.RuntimeModulePath) ? c.RuntimeModulePath : null,
							assembly_full_name = c.AssemblyFullName,
							full_type_name = c.Method.DeclaringType?.FullName,
							method_name = c.Method.Name.String,
							token_hex = "0x" + c.Method.MDToken.Raw.ToString("X8")
						})
						.ToArray()
				};
			}

			return new MethodLookupResult { Candidate = filtered[0] };
		}

		static List<MethodCandidate> ExpandMethodCandidates(List<MethodBaseCandidate> baseCandidates, List<RuntimeModuleInfo> runtimeModules) {
			var list = new List<MethodCandidate>(baseCandidates.Count);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var b in baseCandidates) {
				var matches = runtimeModules
					.Where(r => RuntimeModuleMatchesBaseCandidate(r, b))
					.ToArray();
				if (matches.Length == 0) {
					var fallbackModuleId = Global.ModuleIdProvider == null
						? ModuleId.Create(b.ModulePath)
						: Global.ModuleIdProvider.Create(b.Method.Module);
					var moduleIdText = ModuleIdToStableString(fallbackModuleId);
					var key = moduleIdText + "|" + b.Method.MDToken.Raw.ToString("X8") + "|" + b.ModulePath;
					if (!seen.Add(key))
						continue;
					list.Add(new MethodCandidate {
						Method = b.Method,
						ModulePath = b.ModulePath,
						RuntimeModulePath = b.ModulePath,
						ModuleName = b.ModuleName,
						AssemblySimpleName = b.AssemblySimpleName,
						AssemblyFullName = b.AssemblyFullName,
						ProcessId = null,
						ModuleIdValue = fallbackModuleId,
						ModuleId = moduleIdText
					});
					continue;
				}

				foreach (var m in matches) {
					// Include source module path in key so duplicate tree modules don't collapse
					// into a single runtime candidate. This preserves ambiguity diagnostics.
					var key = m.ModuleId + "|" + b.Method.MDToken.Raw.ToString("X8") + "|" + m.ProcessId.ToString(CultureInfo.InvariantCulture) + "|" + b.ModulePath;
					if (!seen.Add(key))
						continue;
					list.Add(new MethodCandidate {
						Method = b.Method,
						ModulePath = b.ModulePath,
						RuntimeModulePath = m.ModulePath,
						ModuleName = b.ModuleName,
						AssemblySimpleName = b.AssemblySimpleName,
						AssemblyFullName = b.AssemblyFullName,
						ProcessId = m.ProcessId,
						ModuleIdValue = m.ModuleIdValue,
						ModuleId = m.ModuleId
					});
				}
			}
			return list;
		}

		static List<MethodCandidate> ApplyMethodCandidateFilters(
			List<MethodCandidate> candidates,
			string? assemblyName,
			string? moduleName,
			string? modulePath,
			int? processId,
			string? moduleId) {
			IEnumerable<MethodCandidate> filtered = candidates;
			bool usedProcessScopedAssembly = false;

			if (!string.IsNullOrWhiteSpace(moduleId)) {
				filtered = filtered.Where(c => string.Equals(c.ModuleId, moduleId.Trim(), StringComparison.OrdinalIgnoreCase));
			}
			else if (!string.IsNullOrWhiteSpace(modulePath)) {
				var normalizedPath = NormalizePath(modulePath);
				// Prefer exact tree-module path match when present. Fallback to runtime path
				// matching so callers can still target modules only visible from debug runtime.
				var exactSource = filtered.Where(c => string.Equals(c.ModulePath, normalizedPath, StringComparison.OrdinalIgnoreCase)).ToList();
				if (exactSource.Count != 0) {
					filtered = exactSource;
				}
				else {
					filtered = filtered.Where(c =>
						string.Equals(c.ModulePath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
						string.Equals(c.RuntimeModulePath, normalizedPath, StringComparison.OrdinalIgnoreCase));
				}
			}
			else if (processId != null && !string.IsNullOrWhiteSpace(assemblyName)) {
				usedProcessScopedAssembly = true;
				filtered = filtered.Where(c =>
					c.ProcessId == processId.Value &&
					AssemblyNameMatches(c, assemblyName));
			}
			else if (!string.IsNullOrWhiteSpace(assemblyName)) {
				filtered = filtered.Where(c => AssemblyNameMatches(c, assemblyName));
			}

			if (!string.IsNullOrWhiteSpace(moduleName)) {
				filtered = filtered.Where(c =>
					string.Equals(c.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(Path.GetFileName(c.ModulePath), moduleName, StringComparison.OrdinalIgnoreCase));
			}

			if (processId != null && !usedProcessScopedAssembly && string.IsNullOrWhiteSpace(moduleId) && string.IsNullOrWhiteSpace(modulePath)) {
				filtered = filtered.Where(c => c.ProcessId == processId.Value);
			}

			return filtered
				.OrderBy(c => c.ModulePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(c => c.RuntimeModulePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(c => c.ProcessId ?? int.MinValue)
				.ThenBy(c => c.Method.MDToken.Raw)
				.ToList();
		}

		static bool AssemblyNameMatches(MethodCandidate candidate, string assemblyName) {
			return string.Equals(candidate.AssemblySimpleName, assemblyName, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(candidate.AssemblyFullName, assemblyName, StringComparison.OrdinalIgnoreCase);
		}

		static bool RuntimeModuleMatchesBaseCandidate(RuntimeModuleInfo runtime, MethodBaseCandidate candidate) {
			if (!string.IsNullOrEmpty(runtime.ModulePath) &&
				!string.IsNullOrEmpty(candidate.ModulePath) &&
				string.Equals(runtime.ModulePath, candidate.ModulePath, StringComparison.OrdinalIgnoreCase)) {
				return true;
			}

			var runtimeLeaf = GetLeafName(runtime.ModulePath, runtime.ModuleName);
			var candidateLeaf = GetLeafName(candidate.ModulePath, candidate.ModuleName);
			if (string.IsNullOrEmpty(runtimeLeaf) || string.IsNullOrEmpty(candidateLeaf))
				return false;
			if (!string.Equals(runtimeLeaf, candidateLeaf, StringComparison.OrdinalIgnoreCase))
				return false;

			if (string.IsNullOrEmpty(runtime.AssemblyFullName) || string.IsNullOrEmpty(candidate.AssemblyFullName))
				return true;
			return string.Equals(runtime.AssemblyFullName, candidate.AssemblyFullName, StringComparison.OrdinalIgnoreCase);
		}

		static bool RuntimeModuleMatchesModule(RuntimeModuleInfo runtime, ModuleDef mod, string normalizedPath) {
			if (!string.IsNullOrEmpty(runtime.ModulePath) &&
				!string.IsNullOrEmpty(normalizedPath) &&
				string.Equals(runtime.ModulePath, normalizedPath, StringComparison.OrdinalIgnoreCase)) {
				return true;
			}

			var runtimeLeaf = GetLeafName(runtime.ModulePath, runtime.ModuleName);
			var moduleLeaf = GetLeafName(normalizedPath, mod.Name.String ?? string.Empty);
			if (string.IsNullOrEmpty(runtimeLeaf) || string.IsNullOrEmpty(moduleLeaf))
				return false;
			if (!string.Equals(runtimeLeaf, moduleLeaf, StringComparison.OrdinalIgnoreCase))
				return false;

			var asmFull = mod.Assembly?.FullName ?? string.Empty;
			if (string.IsNullOrEmpty(runtime.AssemblyFullName) || string.IsNullOrEmpty(asmFull))
				return true;
			return string.Equals(runtime.AssemblyFullName, asmFull, StringComparison.OrdinalIgnoreCase);
		}

		static string GetLeafName(string pathOrName, string fallback) {
			string candidate = pathOrName;
			if (string.IsNullOrWhiteSpace(candidate))
				candidate = fallback;
			if (string.IsNullOrWhiteSpace(candidate))
				return string.Empty;
			try {
				return Path.GetFileName(candidate);
			}
			catch {
				return candidate;
			}
		}

		static List<RuntimeModuleInfo> CollectRuntimeModuleInfos() {
			return RunOnDbgDispatcher(() => {
				var list = new List<RuntimeModuleInfo>();
				if (Global.DbgManager == null || !Global.DbgManager.IsDebugging)
					return list;
				foreach (var proc in Global.DbgManager.Processes) {
					foreach (var rt in proc.Runtimes) {
						var dotnetRt = rt.InternalRuntime as IDbgDotNetRuntime;
						if (dotnetRt == null)
							continue;
						foreach (var mod in rt.Modules) {
							ModuleId moduleIdValue;
							try {
								moduleIdValue = dotnetRt.GetModuleId(mod);
							}
							catch {
								try {
									moduleIdValue = ModuleId.Create(mod.Filename ?? mod.Name ?? string.Empty);
								}
								catch {
									continue;
								}
							}
							var modulePath = NormalizePath(!string.IsNullOrWhiteSpace(mod.Filename) ? mod.Filename : mod.Name);
							var moduleName = GetLeafName(modulePath, mod.Name ?? string.Empty);
							list.Add(new RuntimeModuleInfo {
								ProcessId = proc.Id,
								ModulePath = modulePath,
								ModuleName = moduleName,
								AssemblyFullName = moduleIdValue.AssemblyFullName ?? string.Empty,
								ModuleIdValue = moduleIdValue,
								ModuleId = ModuleIdToStableString(moduleIdValue)
							});
						}
					}
				}
				list = list
					.GroupBy(a => a.ProcessId.ToString(CultureInfo.InvariantCulture) + "|" + a.ModuleId, StringComparer.OrdinalIgnoreCase)
					.Select(a => a.First())
					.ToList();
				return list;
			});
		}

		static ModuleId? TryResolveRuntimeModuleId(string modulePath, int? processId) {
			return RunOnDbgDispatcher(() => {
				if (Global.DbgManager == null || !Global.DbgManager.IsDebugging)
					return (ModuleId?)null;

				var targetPath = NormalizePath(modulePath);
				var targetLeaf = GetLeafName(targetPath, string.Empty);
				foreach (var proc in Global.DbgManager.Processes) {
					if (processId != null && proc.Id != processId.Value)
						continue;
					foreach (var rt in proc.Runtimes) {
						var dotnetRt = rt.InternalRuntime as IDbgDotNetRuntime;
						if (dotnetRt == null)
							continue;
						foreach (var mod in rt.Modules) {
							var modPath = NormalizePath(!string.IsNullOrWhiteSpace(mod.Filename) ? mod.Filename : mod.Name);
							var modLeaf = GetLeafName(modPath, mod.Name ?? string.Empty);
							bool match =
								(!string.IsNullOrEmpty(targetPath) && string.Equals(modPath, targetPath, StringComparison.OrdinalIgnoreCase)) ||
								(!string.IsNullOrEmpty(targetLeaf) && string.Equals(modLeaf, targetLeaf, StringComparison.OrdinalIgnoreCase));
							if (!match)
								continue;
							try {
								return (ModuleId?)dotnetRt.GetModuleId(mod);
							}
							catch {
							}
						}
					}
				}

				return (ModuleId?)null;
			});
		}

		static DbgCodeBreakpoint? FindExistingCodeBreakpoint(ModuleId moduleId, uint token, uint ilOffset) {
			if (Global.DbgCodeBreakpointsService == null)
				return null;

			foreach (var bp in Global.DbgCodeBreakpointsService.Breakpoints) {
				if (bp.Location is not IDbgDotNetCodeLocation loc)
					continue;
				if (loc.Token != token || loc.Offset != ilOffset)
					continue;
				if (loc.Module != moduleId)
					continue;
				return bp;
			}
			return null;
		}

		static string NormalizePath(string? path) {
			if (string.IsNullOrWhiteSpace(path))
				return string.Empty;
			try {
				return Path.GetFullPath(path).Replace('/', '\\');
			}
			catch {
				return path.Replace('/', '\\');
			}
		}

		static string ModuleIdToStableString(ModuleId moduleId) => moduleId.ToString();

		static readonly object decompiledCacheGate = new object();
		static readonly Dictionary<string, DecompiledMethodSnapshot> decompiledCache = new Dictionary<string, DecompiledMethodSnapshot>(StringComparer.OrdinalIgnoreCase);
		static readonly TimeSpan decompiledCacheTtl = TimeSpan.FromMinutes(5);
		const int maxDecompiledCacheEntries = 128;

		sealed class DecompiledMethodSnapshot {
			public string Text { get; set; } = "";
			public IReadOnlyList<MethodDebugInfo> MethodDebugInfos { get; set; } = Array.Empty<MethodDebugInfo>();
			public DateTime CachedUtc { get; set; }
		}

		static DecompiledMethodSnapshot? GetMethodDecompiledSnapshot(MethodDef method, out string? error) {
			error = null;
			if (Global.MyAppWindow == null || Global.MyDocumentTabService == null) {
				error = "Document services not available.";
				return null;
			}

			if (TryGetCachedMethodDecompiledSnapshot(method, out var cached))
				return cached;

			var evt = new ManualResetEventSlim(false);
			DecompiledMethodSnapshot? snapshot = null;
			string? localError = null;

			Global.MyAppWindow.MainWindow.Dispatcher.BeginInvoke(new Action(() => {
				Global.MyDocumentTabService.FollowReference(method, newTab: true, setFocus: false, onShown: args => {
					try {
						if (!args.Success) {
							localError = "Failed to show document tab.";
							return;
						}
						var viewer = args.Tab.TryGetDocumentViewer();
						if (viewer == null) {
							localError = "Document viewer not available.";
							return;
						}

						var content = viewer.Content;
						snapshot = new DecompiledMethodSnapshot {
							Text = content.Text,
							MethodDebugInfos = content.MethodDebugInfos.ToArray(),
							CachedUtc = DateTime.UtcNow,
						};
					}
					finally {
						evt.Set();
						try {
							var tab = args.Tab;
							var dispatcher = Global.MyAppWindow?.MainWindow?.Dispatcher;
							if (dispatcher != null) {
								dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => {
									try {
										tab.Close();
									}
									catch {
									}
								}));
							}
							else {
								tab.Close();
							}
						}
						catch {
						}
					}
				});
			}));

			if (!evt.Wait(TimeSpan.FromSeconds(10))) {
				error = "Timed out waiting for decompiled content.";
				return null;
			}

			error = localError;
			if (snapshot != null)
				AddCachedMethodDecompiledSnapshot(method, snapshot);
			return snapshot;
		}

		static MethodDebugInfo? FindMethodDebugInfo(IReadOnlyList<MethodDebugInfo> infos, MethodDef method) {
			foreach (var info in infos) {
				if (info.Method.MDToken == method.MDToken &&
					string.Equals(info.Method.Module.Location, method.Module.Location, StringComparison.OrdinalIgnoreCase))
					return info;
			}
			return null;
		}

		sealed class LineMatch {
			public int LineNumber { get; set; }
			public int LineStart { get; set; }
			public int LineEnd { get; set; }
			public string LineText { get; set; } = "";
		}

		sealed class BreakpointSnapshot {
			public int Id { get; set; }
			public bool Enabled { get; set; }
			public object[] BoundBreakpoints { get; set; } = Array.Empty<object>();
			public string BoundBreakpointsMessageSeverity { get; set; } = "";
			public string BoundBreakpointsMessageText { get; set; } = "";
			public uint? Token { get; set; }
			public uint? ILOffset { get; set; }
			public string? ModuleName { get; set; }
			public string? AssemblyFullName { get; set; }
		}

		sealed class DecompiledLocationInfo {
			public int? LineNumber { get; set; }
			public string? LineText { get; set; }
			public string? PreviousLineText { get; set; }
			public string? NextLineText { get; set; }
			public string? CodeContext { get; set; }
			public string? FullTypeName { get; set; }
			public string? MethodName { get; set; }
			public string? Error { get; set; }
		}

		sealed class CurrentStopInfo {
			public ulong ThreadId { get; set; }
			public string CurrentFrameKind { get; set; } = "managed";
			public string TokenHex { get; set; } = "";
			public uint ILOffset { get; set; }
			public string ModuleName { get; set; } = "";
			public string AssemblyFullName { get; set; } = "";
			public string? DecompiledFullTypeName { get; set; }
			public string? DecompiledMethodName { get; set; }
			public int? DecompiledLineNumber { get; set; }
			public string? DecompiledLineText { get; set; }
			public string? DecompiledCodeContext { get; set; }
			public string? DecompiledPrevLineText { get; set; }
			public string? DecompiledNextLineText { get; set; }
			public string? DecompiledResolveError { get; set; }
		}

		sealed class StepReadinessInfo {
			public bool can_step { get; set; }
			public string frame_kind { get; set; } = "unknown";
			public string? location_type { get; set; }
			public string? il_offset_mapping { get; set; }
			public string? reason { get; set; }
		}

		sealed class DebugModuleCandidate {
			public int ProcessId { get; set; }
			public string ProcessName { get; set; } = "";
			public string ModuleName { get; set; } = "";
			public string ModulePath { get; set; } = "";
			public bool IsDynamic { get; set; }
			public bool IsInMemory { get; set; }
		}

		static List<DebugModuleCandidate> CollectDebugModules(int? processId) {
			if (Global.DbgManager == null || !Global.DbgManager.IsDebugging)
				return new List<DebugModuleCandidate>();
			var list = new List<DebugModuleCandidate>();
			foreach (var proc in Global.DbgManager.Processes) {
				if (processId != null && proc.Id != processId.Value)
					continue;
				foreach (var rt in proc.Runtimes) {
					foreach (var mod in rt.Modules) {
						list.Add(new DebugModuleCandidate {
							ProcessId = proc.Id,
							ProcessName = proc.Filename,
							ModuleName = mod.Name,
							ModulePath = mod.Filename,
							IsDynamic = mod.IsDynamic,
							IsInMemory = mod.IsInMemory,
						});
					}
				}
			}
			return list;
		}

		static DecompiledLocationInfo ResolveDecompiledLocation(string assemblyFullName, string moduleName, uint token, uint ilOffset, bool includeContext) {
			if (Global.MyTreeView == null || Global.MyDocumentTabService == null || Global.MyAppWindow == null) {
				return new DecompiledLocationInfo { Error = "dnSpy UI/document services not available." };
			}

			MethodDef? method = null;
			string inputModule = moduleName.Replace('/', '\\');
			string inputFile = string.Empty;
			try {
				inputFile = Path.GetFileName(inputModule);
			}
			catch {
				inputFile = inputModule;
			}
			var findErr = RunOnUI(() => {
				foreach (var modNode in Global.MyTreeView!.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null || mod.Assembly == null)
						continue;
					if (!string.Equals(mod.Assembly.FullName, assemblyFullName, StringComparison.OrdinalIgnoreCase))
						continue;
					var modSimple = mod.Name.String ?? string.Empty;
					var modPath = (mod.Location ?? string.Empty).Replace('/', '\\');
					var modFile = string.Empty;
					try {
						modFile = Path.GetFileName(modPath);
					}
					catch {
						modFile = modPath;
					}

					bool exactMatch =
						string.Equals(modSimple, inputModule, StringComparison.OrdinalIgnoreCase) ||
						string.Equals(modPath, inputModule, StringComparison.OrdinalIgnoreCase);
					bool fallbackFileMatch =
						!string.IsNullOrEmpty(inputFile) &&
						(string.Equals(modSimple, inputFile, StringComparison.OrdinalIgnoreCase) ||
						 string.Equals(modFile, inputFile, StringComparison.OrdinalIgnoreCase));
					bool inputLooksLikePath =
						inputModule.IndexOf('\\') >= 0 ||
						inputModule.IndexOf('/') >= 0 ||
						inputModule.IndexOf(':') >= 0;

					if (!exactMatch) {
						// If caller passed a path-like module identifier, do not fall back to leaf-name matching,
						// otherwise duplicate modules with same file name can resolve to stale tree nodes.
						if (inputLooksLikePath || !fallbackFileMatch)
							continue;
					}

					if (!(exactMatch || fallbackFileMatch))
						continue;

					IMDTokenProvider? md = null;
					try {
						md = mod.ResolveToken(token);
					}
					catch {
					}
					if (md == null)
						continue;

					if (md is MethodDef mdef)
						method = mdef;
					else if (md is MemberRef mref)
						method = mref.ResolveMethodDef();

					if (method != null)
						return (string?)null;
				}
				return "Method metadata not found in loaded tree modules.";
			});

			if (method == null) {
				return new DecompiledLocationInfo {
					Error = findErr ?? "Method metadata not found."
				};
			}

			var snapshot = GetMethodDecompiledSnapshot(method, out var contentError);
			if (snapshot == null) {
				return new DecompiledLocationInfo {
					FullTypeName = method.DeclaringType?.FullName,
					MethodName = method.Name.String,
					Error = contentError ?? "Failed to get decompiled content."
				};
			}

			var debugInfo = FindMethodDebugInfo(snapshot.MethodDebugInfos, method);
			if (debugInfo == null) {
				return new DecompiledLocationInfo {
					FullTypeName = method.DeclaringType?.FullName,
					MethodName = method.Name.String,
					Error = "Method debug info not available for resolved method."
				};
			}

			var stmt = debugInfo.GetSourceStatementByCodeOffset(ilOffset);
			if (stmt == null) {
				return new DecompiledLocationInfo {
					FullTypeName = method.DeclaringType?.FullName,
					MethodName = method.Name.String,
					Error = "No matching source statement for IL offset."
				};
			}

			var line = GetLineByTextOffset(snapshot.Text, stmt.Value.TextSpan.Start, includeContext);
			if (line == null) {
				return new DecompiledLocationInfo {
					FullTypeName = method.DeclaringType?.FullName,
					MethodName = method.Name.String,
					Error = "Failed to map source statement text span to line."
				};
			}

			return new DecompiledLocationInfo {
				LineNumber = line.Value.lineNumber,
				LineText = line.Value.lineText,
				PreviousLineText = line.Value.previousLineText,
				NextLineText = line.Value.nextLineText,
				CodeContext = line.Value.codeContext,
				FullTypeName = method.DeclaringType?.FullName,
				MethodName = method.Name.String,
				Error = null
			};
		}

		static string GetMethodCacheKey(MethodDef method) {
			var modulePath = method.Module?.Location ?? string.Empty;
			try {
				modulePath = Path.GetFullPath(modulePath);
			}
			catch {
			}
			var token = method.MDToken.Raw.ToString("X8");
			return modulePath + "|" + token;
		}

		static bool TryGetCachedMethodDecompiledSnapshot(MethodDef method, out DecompiledMethodSnapshot snapshot) {
			var key = GetMethodCacheKey(method);
			lock (decompiledCacheGate) {
				if (decompiledCache.TryGetValue(key, out var value)) {
					if (DateTime.UtcNow - value.CachedUtc <= decompiledCacheTtl) {
						snapshot = value;
						return true;
					}
					decompiledCache.Remove(key);
				}
			}
			snapshot = null!;
			return false;
		}

		static void AddCachedMethodDecompiledSnapshot(MethodDef method, DecompiledMethodSnapshot snapshot) {
			var key = GetMethodCacheKey(method);
			lock (decompiledCacheGate) {
				decompiledCache[key] = snapshot;
				if (decompiledCache.Count <= maxDecompiledCacheEntries)
					return;

				var toRemove = decompiledCache
					.OrderBy(kv => kv.Value.CachedUtc)
					.Take(Math.Max(1, decompiledCache.Count - maxDecompiledCacheEntries))
					.Select(kv => kv.Key)
					.ToArray();
				foreach (var k in toRemove)
					decompiledCache.Remove(k);
			}
		}

		static (int lineNumber, string lineText, string? previousLineText, string? nextLineText, string? codeContext)? GetLineByTextOffset(string text, int offset, bool includeContext) {
			if (string.IsNullOrEmpty(text))
				return null;
			if (offset < 0)
				offset = 0;
			if (offset >= text.Length)
				offset = text.Length - 1;

			int lineStart = offset;
			while (lineStart > 0 && text[lineStart - 1] != '\n')
				lineStart--;

			int lineEnd = offset;
			while (lineEnd < text.Length && text[lineEnd] != '\n')
				lineEnd++;
			if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
				lineEnd--;

			int lineNumber = 1;
			for (int i = 0; i < lineStart; i++) {
				if (text[i] == '\n')
					lineNumber++;
			}

			var lineText = text.Substring(lineStart, Math.Max(0, lineEnd - lineStart));
			if (!includeContext)
				return (lineNumber, lineText, null, null, null);

			string? prev = GetLineByNumber(text, lineNumber - 1);
			string? next = GetLineByNumber(text, lineNumber + 1);
			var sb = new StringBuilder();
			if (!string.IsNullOrEmpty(prev))
				sb.AppendLine("  " + prev);
			sb.AppendLine("> " + lineText);
			if (!string.IsNullOrEmpty(next))
				sb.AppendLine("  " + next);
			return (lineNumber, lineText, prev, next, sb.ToString().TrimEnd());
		}

		static string? GetLineByNumber(string text, int lineNumber) {
			if (lineNumber <= 0 || string.IsNullOrEmpty(text))
				return null;
			int current = 1;
			int start = 0;
			for (int i = 0; i <= text.Length; i++) {
				bool isEnd = i == text.Length || text[i] == '\n';
				if (!isEnd)
					continue;
				int end = i;
				if (end > start && text[end - 1] == '\r')
					end--;
				if (current == lineNumber)
					return text.Substring(start, Math.Max(0, end - start));
				current++;
				start = i + 1;
			}
			return null;
		}

		static List<int> BuildLineStarts(string text) {
			var starts = new List<int> { 0 };
			for (int i = 0; i < text.Length; i++) {
				if (text[i] == '\n')
					starts.Add(i + 1);
			}
			return starts;
		}

		static (int line, int col) GetLineColumnByOffset(IReadOnlyList<int> lineStarts, int textLength, int offset) {
			int clamped = offset;
			if (clamped < 0)
				clamped = 0;
			if (clamped > textLength)
				clamped = textLength;
			if (lineStarts.Count == 0)
				return (1, clamped + 1);

			int lo = 0;
			int hi = lineStarts.Count - 1;
			while (lo <= hi) {
				int mid = lo + ((hi - lo) / 2);
				if (lineStarts[mid] <= clamped)
					lo = mid + 1;
				else
					hi = mid - 1;
			}
			int lineIndex = Math.Max(0, hi);
			int lineStart = lineStarts[lineIndex];
			return (lineIndex + 1, (clamped - lineStart) + 1);
		}

		static void CollectScopeLocals(MethodDebugScope scope, List<object> locals) {
			foreach (var local in scope.Locals) {
				int? slot = local.Local?.Index;
				var metadataName = local.Local?.Name;
				string? runtimeName = string.IsNullOrWhiteSpace(metadataName) ? null : metadataName;
				if (runtimeName == null && slot != null && slot.Value >= 0)
					runtimeName = "V_" + slot.Value.ToString(CultureInfo.InvariantCulture);
				locals.Add(new {
					slot,
					runtime_name = runtimeName,
					decompiled_name = local.Name,
					pdb_name = string.IsNullOrWhiteSpace(metadataName) ? null : metadataName,
					il_start = scope.Span.Start,
					il_end = scope.Span.End,
					is_decompiler_generated = local.IsDecompilerGenerated,
					hoisted_field = local.HoistedField?.FullName,
					type = local.Type.FullName,
				});
			}
			foreach (var child in scope.Scopes)
				CollectScopeLocals(child, locals);
		}

		static (int? processId, ulong? threadId) ResolveStableLastIdentity(DbgManager mgr, LastBreakInfo? lastBreak, DbgThread? statusThread) {
			if (lastBreak?.ProcessId != null && lastBreak.ThreadId != null)
				return (lastBreak.ProcessId, lastBreak.ThreadId);

			if (lastBreak?.ThreadId != null) {
				var resolved = FindThreadById(mgr, lastBreak.ThreadId.Value);
				if (resolved != null)
					return (resolved.Process.Id, resolved.Id);
			}

			if (statusThread != null)
				return (statusThread.Process.Id, statusThread.Id);

			return (null, null);
		}

		static string? GetCurrentFrameKind(DbgThread? thread) {
			if (thread == null)
				return null;
			var frame = thread.GetTopStackFrame();
			try {
				var info = GetFrameKind(frame);
				return info.frameKind;
			}
			finally {
				frame?.Close();
			}
		}

		static StepReadinessInfo GetStepReadiness(DbgThread thread) {
			var frame = thread.GetTopStackFrame();
			try {
				var info = GetFrameKind(frame);
				if (frame == null) {
					return new StepReadinessInfo {
						can_step = false,
						frame_kind = info.frameKind,
						location_type = info.locationType,
						il_offset_mapping = info.ilOffsetMapping,
						reason = "No current stack frame is available."
					};
				}

				if (info.frameKind != "managed") {
					return new StepReadinessInfo {
						can_step = false,
						frame_kind = info.frameKind,
						location_type = info.locationType,
						il_offset_mapping = info.ilOffsetMapping,
						reason = "Current frame is not at a managed decompiled statement."
					};
				}

				if (!info.locationIsNextStatement) {
					return new StepReadinessInfo {
						can_step = false,
						frame_kind = info.frameKind,
						location_type = info.locationType,
						il_offset_mapping = info.ilOffsetMapping,
						reason = "Current frame location is not a valid next statement."
					};
				}

				return new StepReadinessInfo {
					can_step = true,
					frame_kind = info.frameKind,
					location_type = info.locationType,
					il_offset_mapping = info.ilOffsetMapping
				};
			}
			finally {
				frame?.Close();
			}
		}

		static (string frameKind, string? locationType, string? ilOffsetMapping, bool locationIsNextStatement) GetFrameKind(DbgStackFrame? frame) {
			if (frame == null)
				return ("native_transition", null, null, false);
			var isNextStatement = (frame.Flags & DbgStackFrameFlags.LocationIsNextStatement) != 0;
			var location = frame.Location;
			if (location == null)
				return ("native_transition", null, null, isNextStatement);

			var locationType = location.Type;
			if (location is IDbgDotNetCodeLocation dotNetLoc) {
				var mapping = dotNetLoc.ILOffsetMapping;
				var mappingText = mapping.ToString();
				bool hasStableManagedStatement = mapping == DbgILOffsetMapping.Exact || mapping == DbgILOffsetMapping.Approximate;
				var frameKind = hasStableManagedStatement && isNextStatement ? "managed" : "native_transition";
				return (frameKind, locationType, mappingText, isNextStatement);
			}

			return ("native", locationType, null, isNextStatement);
		}

		static CurrentStopInfo? ResolveCurrentStop(DbgThread? thread, bool includeContext) {
			if (thread == null)
				return null;

			var frame = thread.GetTopStackFrame();
			try {
				var frameInfo = GetFrameKind(frame);
				if (frame?.Location is not IDbgDotNetCodeLocation loc) {
					return new CurrentStopInfo {
						ThreadId = thread.Id,
						CurrentFrameKind = frameInfo.frameKind,
						DecompiledResolveError = "No .NET code location available for current frame."
					};
				}

				var moduleName = loc.Module.ModuleName ?? string.Empty;
				var assemblyFullName = loc.Module.AssemblyFullName ?? string.Empty;
				var token = loc.Token;
				var ilOffset = loc.Offset;

				var dec = ResolveDecompiledLocation(assemblyFullName, moduleName, token, ilOffset, includeContext);
				return new CurrentStopInfo {
					ThreadId = thread.Id,
					CurrentFrameKind = frameInfo.frameKind,
					TokenHex = "0x" + token.ToString("X8"),
					ILOffset = ilOffset,
					ModuleName = moduleName,
					AssemblyFullName = assemblyFullName,
					DecompiledFullTypeName = dec.FullTypeName,
					DecompiledMethodName = dec.MethodName,
					DecompiledLineNumber = dec.LineNumber,
					DecompiledLineText = dec.LineText,
					DecompiledCodeContext = dec.CodeContext,
					DecompiledPrevLineText = dec.PreviousLineText,
					DecompiledNextLineText = dec.NextLineText,
					DecompiledResolveError = dec.Error
				};
			}
			finally {
				frame?.Close();
			}
		}

		static object ToCurrentStopPayload(CurrentStopInfo stop, bool includeContext) {
			if (!includeContext) {
				return new {
					thread_id = stop.ThreadId,
					current_frame_kind = stop.CurrentFrameKind,
					token_hex = stop.TokenHex,
					il_offset = stop.ILOffset,
					module_name = stop.ModuleName,
					assembly_full_name = stop.AssemblyFullName,
					decompiled_full_type_name = stop.DecompiledFullTypeName,
					decompiled_method_name = stop.DecompiledMethodName,
					decompiled_line_number = stop.DecompiledLineNumber,
					decompiled_line_text = stop.DecompiledLineText,
					decompiled_resolve_error = stop.DecompiledResolveError
				};
			}

			return new {
				thread_id = stop.ThreadId,
				current_frame_kind = stop.CurrentFrameKind,
				token_hex = stop.TokenHex,
				il_offset = stop.ILOffset,
				module_name = stop.ModuleName,
				assembly_full_name = stop.AssemblyFullName,
				decompiled_full_type_name = stop.DecompiledFullTypeName,
				decompiled_method_name = stop.DecompiledMethodName,
				decompiled_line_number = stop.DecompiledLineNumber,
				decompiled_line_text = stop.DecompiledLineText,
				decompiled_code_context = stop.DecompiledCodeContext,
				decompiled_prev_line_text = stop.DecompiledPrevLineText,
				decompiled_next_line_text = stop.DecompiledNextLineText,
				decompiled_resolve_error = stop.DecompiledResolveError
			};
		}

		static DbgThread? ResolveStatusThread(DbgManager mgr, LastBreakInfo? lastBreak) {
			var current = mgr.CurrentThread.Current;
			if (current != null)
				return current;
			if (lastBreak?.ThreadId != null)
				return FindThreadById(mgr, lastBreak.ThreadId.Value);
			return null;
		}

		static DbgThread? FindThreadById(DbgManager mgr, ulong threadId) {
			foreach (var proc in mgr.Processes) {
				foreach (var rt in proc.Runtimes) {
					foreach (var thread in rt.Threads) {
						if (thread.Id == threadId)
							return thread;
					}
				}
			}
			return null;
		}

		static (DbgThread? thread, string? error) GetTargetThread(DbgManager mgr, ulong? threadId) {
			if (threadId == null) {
				var current = mgr.CurrentThread.Current;
				if (current != null)
					return (current, null);
				foreach (var proc in mgr.Processes) {
					foreach (var rt in proc.Runtimes) {
						if (rt.Threads.Length != 0)
							return (rt.Threads[0], null);
					}
				}
				return (null, "No thread is available.");
			}

			var found = FindThreadById(mgr, threadId.Value);
			return found == null ? (null, "Thread not found.") : (found, null);
		}

		static void CloseFrames(DbgStackFrame[] frames) {
			for (int i = 0; i < frames.Length; i++) {
				try {
					frames[i].Close();
				}
				catch {
				}
			}
		}

		static void CloseValueNodes(IEnumerable<DbgValueNode> nodes) {
			var mgr = Global.DbgManager;
			foreach (var node in nodes) {
				try {
					if (mgr != null)
						mgr.Close(node);
				}
				catch {
				}
			}
		}

		static string FormatFrame(DbgLanguage language, DbgStackFrame frame, DbgStackFrameFormatterOptions frameOptions, DbgValueFormatterOptions valueOptions) {
			DbgEvaluationContext? context = null;
			try {
				context = language.CreateContext(frame, options: DbgEvaluationContextOptions.NoMethodBody, cancellationToken: CancellationToken.None);
				var evalInfo = new DbgEvaluationInfo(context, frame, CancellationToken.None);
				var output = new DbgStringBuilderTextWriter();
				language.Formatter.FormatFrame(evalInfo, output, frameOptions, valueOptions, cultureInfo: null);
				return output.ToString();
			}
			catch (Exception ex) {
				return "<frame format failed: " + ex.Message + ">";
			}
			finally {
				context?.Close();
			}
		}

		static object FormatLocal(DbgEvaluationInfo evalInfo, DbgLocalsValueNodeInfo local, DbgValueFormatterOptions nameOptions, DbgValueFormatterOptions valueOptions, DbgValueFormatterTypeOptions typeOptions, bool compact) {
			var node = local.ValueNode;
			if (compact) {
				return new {
					kind = local.Kind.ToString().ToLowerInvariant(),
					name = TryFormatNodeName(evalInfo, node, nameOptions),
					value = TryFormatNodeValue(evalInfo, node, valueOptions),
					error = node.ErrorMessage
				};
			}
			return new {
				kind = local.Kind.ToString().ToLowerInvariant(),
				name = TryFormatNodeName(evalInfo, node, nameOptions),
				value = TryFormatNodeValue(evalInfo, node, valueOptions),
				type = TryFormatNodeType(evalInfo, node, typeOptions, valueOptions),
				expression = node.Expression,
				error = node.ErrorMessage,
				is_read_only = node.IsReadOnly,
				causes_side_effects = node.CausesSideEffects,
				has_children = node.HasChildren
			};
		}

		static string TryFormatNodeName(DbgEvaluationInfo evalInfo, DbgValueNode node, DbgValueFormatterOptions nameOptions) {
			try {
				var output = new DbgStringBuilderTextWriter();
				node.FormatName(evalInfo, output, nameOptions, cultureInfo: null);
				return output.ToString();
			}
			catch (Exception ex) {
				return "<name format failed: " + ex.Message + ">";
			}
		}

		static string TryFormatNodeValue(DbgEvaluationInfo evalInfo, DbgValueNode node, DbgValueFormatterOptions valueOptions) {
			try {
				var output = new DbgStringBuilderTextWriter();
				node.FormatValue(evalInfo, output, valueOptions, cultureInfo: null);
				return output.ToString();
			}
			catch (Exception ex) {
				return "<value format failed: " + ex.Message + ">";
			}
		}

		static string TryFormatNodeType(DbgEvaluationInfo evalInfo, DbgValueNode node, DbgValueFormatterTypeOptions typeOptions, DbgValueFormatterOptions valueOptions) {
			try {
				var output = new DbgStringBuilderTextWriter();
				node.FormatActualType(evalInfo, output, typeOptions, valueOptions, cultureInfo: null);
				if (!output.IsEmpty)
					return output.ToString();
				output.Reset();
				node.FormatExpectedType(evalInfo, output, typeOptions, valueOptions, cultureInfo: null);
				return output.ToString();
			}
			catch (Exception ex) {
				return "<type format failed: " + ex.Message + ">";
			}
		}

		static string FormatValue(DbgLanguage language, DbgEvaluationInfo evalInfo, DbgValue value, DbgValueFormatterOptions options) {
			try {
				var output = new DbgStringBuilderTextWriter();
				language.Formatter.FormatValue(evalInfo, output, value, options, cultureInfo: null);
				return output.ToString();
			}
			catch (Exception ex) {
				return "<value format failed: " + ex.Message + ">";
			}
		}

		static string FormatType(DbgLanguage language, DbgEvaluationInfo evalInfo, DbgValue value, DbgValueFormatterTypeOptions options) {
			try {
				var output = new DbgStringBuilderTextWriter();
				language.Formatter.FormatType(evalInfo, output, value, options, cultureInfo: null);
				return output.ToString();
			}
			catch (Exception ex) {
				return "<type format failed: " + ex.Message + ">";
			}
		}

		static object? NormalizeRawValue(object? rawValue) {
			if (rawValue == null)
				return null;
			if (rawValue is DateTime dt)
				return dt.ToString("o", CultureInfo.InvariantCulture);
			if (rawValue is Enum)
				return rawValue.ToString();
			return rawValue;
		}

		static object ErrorResult(string code, string message, object? details = null) {
			if (details == null) {
				return new {
					ok = false,
					error = message,
					error_code = code
				};
			}
			return new {
				ok = false,
				error = message,
				error_code = code,
				error_details = details
			};
		}

		static string InferEvaluateErrorCode(string error, DbgEvaluationOptions options) {
			var s = (error ?? string.Empty).ToLowerInvariant();
			if (s.Contains("native stack frame"))
				return "native_frame_not_supported";
			if ((options & DbgEvaluationOptions.NoSideEffects) != 0 && s.Contains("side effect"))
				return "no_side_effects_blocked";
			if ((options & DbgEvaluationOptions.NoFuncEval) != 0 && (s.Contains("func") || s.Contains("function evaluation") || s.Contains("can't evaluate")))
				return "func_eval_disabled";
			if (s.Contains("compiler") || s.Contains("syntax") || s.Contains("expected") || s.Contains("cs"))
				return "compile_error";
			return "runtime_eval_error";
		}

		static List<LineMatch> FindMatchingLines(string text, MethodDebugInfo info, string containsText) {
			var matches = new List<LineMatch>();
			var lineStarts = new List<int> { 0 };
			for (int i = 0; i < text.Length; i++) {
				if (text[i] == '\n')
					lineStarts.Add(i + 1);
			}

			var spanStart = info.Span.Start;
			var spanEnd = info.Span.End;

			for (int i = 0; i < lineStarts.Count; i++) {
				var start = lineStarts[i];
				var end = (i + 1 < lineStarts.Count ? lineStarts[i + 1] : text.Length);
				if (end > 0 && text[end - 1] == '\n')
					end--;
				if (end > 0 && text[end - 1] == '\r')
					end--;

				if (info.HasSpan && (start >= spanEnd || end <= spanStart))
					continue;

				var lineText = text.Substring(start, end - start);
				if (lineText.IndexOf(containsText, StringComparison.Ordinal) >= 0) {
					matches.Add(new LineMatch {
						LineNumber = i + 1,
						LineStart = start,
						LineEnd = end,
						LineText = lineText
					});
				}
			}

			return matches;
		}

		static T RunOnDbgDispatcher<T>(Func<T> action) {
			var mgr = Global.DbgManager;
			if (mgr == null)
				return action();
			if (mgr.Dispatcher.CheckAccess())
				return action();
			var evt = new ManualResetEventSlim(false);
			T result = default!;
			Exception? error = null;
			mgr.Dispatcher.BeginInvoke(() => {
				try {
					result = action();
				}
				catch (Exception ex) {
					error = ex;
				}
				finally {
					evt.Set();
				}
			});
			evt.Wait();
			if (error != null)
				throw error;
			return result;
		}
	}
}
