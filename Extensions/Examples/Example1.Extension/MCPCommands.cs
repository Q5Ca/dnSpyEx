using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Steppers;
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
						dec = ResolveDecompiledLocation(bp.AssemblyFullName!, bp.ModuleName!, bp.Token.Value, bp.ILOffset.Value);
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

		[Command("set_breakpoint_text", MCPCmdDescription = "Set a breakpoint by decompiled text anchor.")]
		public static object SetBreakpointText(
			string full_type_name,
			string method_name,
			string decompiled_line_contains,
			int? occurrence = null,
			string? assembly_name = null,
			string? module_name = null,
			string? module_path = null) {

			if (Global.MyTreeView == null || Global.MyAppWindow == null || Global.MyDocumentTabService == null)
				return new { error = "dnSpy UI services not available." };
			if (Global.DbgCodeBreakpointsService == null || Global.DbgDotNetCodeLocationFactory == null || Global.ModuleIdProvider == null)
				return new { error = "Debugger services not available." };

			var methodResult = FindMethod(full_type_name, method_name, assembly_name, module_name, module_path);
			if (methodResult.error != null)
				return new { error = methodResult.error };

			var method = methodResult.method!;
			var module = method.Module;

			var content = GetDocumentViewerContent(method, out var contentError);
			if (content == null)
				return new { error = contentError ?? "Failed to get decompiled content." };

			var debugInfo = FindMethodDebugInfo(content.MethodDebugInfos, method);
			if (debugInfo == null)
				return new { error = "Method debug info not available for the target method." };

			var matches = FindMatchingLines(content.Text, debugInfo, decompiled_line_contains);
			if (matches.Count == 0)
				return new { error = "No matching decompiled line found." };

			if (occurrence == null && matches.Count > 1) {
				return new { error = "Multiple matching lines found. Provide more specific text or filters.", match_count = matches.Count };
			}

			int chosenIndex = 0;
			if (occurrence != null) {
				if (occurrence <= 0 || occurrence > matches.Count)
					return new { error = "Occurrence is out of range.", match_count = matches.Count };
				chosenIndex = occurrence.Value - 1;
			}

			var match = matches[chosenIndex];
			var pos = match.LineStart + match.LineText.IndexOf(decompiled_line_contains, StringComparison.Ordinal);
			var statement = debugInfo.GetSourceStatementByTextOffset(match.LineStart, match.LineEnd, pos);
			if (statement == null)
				return new { error = "Failed to map the line to IL." };

			var ilOffset = statement.Value.ILSpan.Start;
			var token = method.MDToken.Raw;
			// Prefer the runtime ModuleId if we're currently debugging. It can differ from a file-based ModuleId
			// (eg. dynamic module IDs, module ID updates) and unbound breakpoints won't hit.
			var moduleId = Global.ModuleIdProvider.Create(module);
			var runtimeModuleId = RunOnDbgDispatcher(() => {
				if (Global.DbgManager == null)
					return (ModuleId?)null;
				if (!Global.DbgManager.IsDebugging)
					return (ModuleId?)null;

				string targetPath;
				try {
					targetPath = Path.GetFullPath(module.Location).Replace('/', '\\');
				}
				catch {
					targetPath = (module.Location ?? string.Empty).Replace('/', '\\');
				}
				var targetFile = Path.GetFileName(targetPath);

				foreach (var proc in Global.DbgManager.Processes) {
					foreach (var rt in proc.Runtimes) {
						var dotnetRt = rt.InternalRuntime as IDbgDotNetRuntime;
						if (dotnetRt == null)
							continue;
						foreach (var m in rt.Modules) {
							var fn = (m.Filename ?? string.Empty).Replace('/', '\\');
							bool match = false;
							if (!string.IsNullOrEmpty(fn) && !string.IsNullOrEmpty(targetPath) &&
								string.Equals(fn, targetPath, StringComparison.OrdinalIgnoreCase)) {
								match = true;
							}
							else if (!string.IsNullOrEmpty(targetFile) &&
								string.Equals(Path.GetFileName(fn), targetFile, StringComparison.OrdinalIgnoreCase)) {
								match = true;
							}
							else if (!string.IsNullOrEmpty(targetFile) &&
								string.Equals(m.Name, targetFile, StringComparison.OrdinalIgnoreCase)) {
								match = true;
							}

							if (!match)
								continue;

							try {
								return (ModuleId?)dotnetRt.GetModuleId(m);
							}
							catch {
								// Ignore engines that don't support it or if the module is in an unexpected state
							}
						}
					}
				}

				return (ModuleId?)null;
			});
			if (runtimeModuleId != null)
				moduleId = runtimeModuleId.Value;
			// Use Approximate mapping: dnSpy's own breakpoint placement often uses approximate mapping
			// and it improves reliability across small decompiler/IL mapping differences.
			var location = Global.DbgDotNetCodeLocationFactory.Create(moduleId, token, ilOffset, DbgILOffsetMapping.Approximate);
			var settings = new DbgCodeBreakpointSettings { IsEnabled = true };
			var added = RunOnDbgDispatcher(() =>
				Global.DbgCodeBreakpointsService.Add(new DbgCodeBreakpointInfo(location, settings))
			);
			if (added == null)
				return new { error = "Failed to add breakpoint." };

			return new {
				id = added.Id,
				il_offset = ilOffset,
				token_hex = "0x" + token.ToString("X8"),
				module_name = module.Name.String,
				assembly_full_name = module.Assembly.FullName,
				line_number = match.LineNumber,
				line_text = match.LineText
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

		[Command("get_status", MCPCmdDescription = "Get current debugger state and last break.")]
		public static object GetStatus() {
			if (Global.DbgManager == null)
				return new { error = "Debugger not available." };

			return RunOnDbgDispatcher(() => {
				var mgr = Global.DbgManager!;
				var state = mgr.IsDebugging
					? (mgr.IsRunning == true ? "running" : mgr.IsRunning == false ? "break" : "mixed")
					: "detached";
				var last = Global.DebugState.GetLastBreak();
				object? lastObj = null;
				if (last != null) {
					lastObj = new {
						kind = last.Kind,
						breakpoint_id = last.BreakpointId,
						thread_id = last.ThreadId,
						exception = last.Exception,
						message = last.Message,
						utc = last.Utc.ToString("o")
					};
				}
				return (object)new {
					state,
					is_debugging = mgr.IsDebugging,
					is_running = mgr.IsRunning,
					current_process_id = mgr.CurrentProcess.Current?.Id,
					current_thread_id = mgr.CurrentThread.Current?.Id,
					last_break = lastObj
				};
			});
		}

		[Command("detach", MCPCmdDescription = "Detach from all debugged processes.")]
		public static object Detach() => RunManagerAction(mgr => mgr.DetachAll());

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

		[Command("classes_from_namespace", MCPCmdDescription = "List all classes under a namespace.")]
		public static string Classes_From_Namespace(string assemblyName, string namespaceName) {
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
					var types = mod.GetTypes().OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase);
					foreach (var t in types) {
						if (string.Equals(t.Namespace, namespaceName, StringComparison.Ordinal))
							sb.AppendLine(t.FullName);
					}
				}
				return sb.ToString();
			});
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
				return new { error = "Debugger not available." };
			return RunOnDbgDispatcher(() => {
				if (!Global.DbgManager.IsDebugging)
					return (object)new { error = "Not debugging." };
				var thread = Global.DbgManager.CurrentThread.Current;
				if (thread == null)
					return (object)new { error = "No current thread." };
				var stepper = thread.CreateStepper();
				stepper.Step(kind, autoClose: true);
				return (object)new { ok = true };
			});
		}

		static (MethodDef? method, string? error) FindMethod(string fullTypeName, string methodName, string? assemblyName, string? moduleName, string? modulePath) {
			if (Global.MyTreeView == null)
				return (null, "TreeView not available.");

			return RunOnUI(() => {
				var candidates = new List<MethodDef>();
				var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var modNode in Global.MyTreeView.GetAllModuleNodes().ToList()) {
					var mod = modNode.GetModule();
					if (mod == null)
						continue;
					if (!string.IsNullOrWhiteSpace(assemblyName)) {
						var asm = mod.Assembly;
						if (asm == null || (!string.Equals(asm.Name.String, assemblyName, StringComparison.OrdinalIgnoreCase) &&
							!string.Equals(asm.FullName, assemblyName, StringComparison.OrdinalIgnoreCase)))
							continue;
					}
					if (!string.IsNullOrWhiteSpace(moduleName) &&
						!string.Equals(mod.Name, moduleName, StringComparison.OrdinalIgnoreCase))
						continue;
					if (!string.IsNullOrWhiteSpace(modulePath) &&
						!string.Equals(mod.Location, modulePath, StringComparison.OrdinalIgnoreCase))
						continue;

					foreach (var type in mod.GetTypes()) {
						if (!string.Equals(type.FullName, fullTypeName, StringComparison.Ordinal))
							continue;
						foreach (var method in type.Methods) {
							if (!string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
								continue;
							var key = (mod.Location ?? string.Empty) + "|" + method.MDToken.Raw.ToString("X8");
							if (seen.Add(key))
								candidates.Add(method);
						}
					}
				}

				if (candidates.Count == 0)
					return ((MethodDef?)null, "Method not found.");
				if (candidates.Count > 1)
					return ((MethodDef?)null, "Multiple methods found. Be more specific.");
				return (candidates[0], (string?)null);
			});
		}

		static DocumentViewerContent? GetDocumentViewerContent(MethodDef method, out string? error) {
			error = null;
			if (Global.MyAppWindow == null || Global.MyDocumentTabService == null) {
				error = "Document services not available.";
				return null;
			}

			var evt = new ManualResetEventSlim(false);
			DocumentViewerContent? content = null;
			string? localError = null;

			Global.MyAppWindow.MainWindow.Dispatcher.BeginInvoke(new Action(() => {
				Global.MyDocumentTabService.FollowReference(method, newTab: true, setFocus: false, onShown: args => {
					if (!args.Success) {
						localError = "Failed to show document tab.";
						evt.Set();
						return;
					}
					var viewer = args.Tab.TryGetDocumentViewer();
					if (viewer == null) {
						localError = "Document viewer not available.";
						evt.Set();
						return;
					}
					content = viewer.Content;
					evt.Set();
				});
			}));

			if (!evt.Wait(TimeSpan.FromSeconds(10))) {
				error = "Timed out waiting for decompiled content.";
				return null;
			}

			error = localError;
			return content;
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
			public string? FullTypeName { get; set; }
			public string? MethodName { get; set; }
			public string? Error { get; set; }
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

		static DecompiledLocationInfo ResolveDecompiledLocation(string assemblyFullName, string moduleName, uint token, uint ilOffset) {
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

					bool moduleMatches =
						string.Equals(modSimple, inputModule, StringComparison.OrdinalIgnoreCase) ||
						string.Equals(modPath, inputModule, StringComparison.OrdinalIgnoreCase) ||
						(!string.IsNullOrEmpty(inputFile) && string.Equals(modSimple, inputFile, StringComparison.OrdinalIgnoreCase)) ||
						(!string.IsNullOrEmpty(inputFile) && string.Equals(modFile, inputFile, StringComparison.OrdinalIgnoreCase));
					if (!moduleMatches)
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

			var content = GetDocumentViewerContent(method, out var contentError);
			if (content == null) {
				return new DecompiledLocationInfo {
					FullTypeName = method.DeclaringType?.FullName,
					MethodName = method.Name.String,
					Error = contentError ?? "Failed to get decompiled content."
				};
			}

			var debugInfo = FindMethodDebugInfo(content.MethodDebugInfos, method);
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

			var line = GetLineByTextOffset(content.Text, stmt.Value.TextSpan.Start);
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
				FullTypeName = method.DeclaringType?.FullName,
				MethodName = method.Name.String,
				Error = null
			};
		}

		static (int lineNumber, string lineText)? GetLineByTextOffset(string text, int offset) {
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
			return (lineNumber, lineText);
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
