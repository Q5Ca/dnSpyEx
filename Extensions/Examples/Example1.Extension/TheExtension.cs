using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Metadata;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.ToolWindows.App;
using dnlib.DotNet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Example1.Extension {
	[ExportExtension]
	sealed class TheExtension : IExtension {
		[Import] public IAppWindow dnWindow = null!;
		[Import(AllowDefault = true)] public IDocumentTreeView MyTreeView = null!;
		[Import(AllowDefault = true)] public IDocumentTabService MyTabService = null!;
		[Import(AllowDefault = true)] public IDsToolWindowService? ToolWindowService { get; set; }
		[Import] public IDecompilerService decompilerService = null!;
		[Import] public Lazy<DbgManager> DbgManager = null!;
		[Import] public Lazy<AttachableProcessesService> AttachableProcessesService = null!;
		[Import] public Lazy<DbgCodeBreakpointsService> DbgCodeBreakpointsService = null!;
		[Import] public Lazy<DbgDotNetCodeLocationFactory> DbgDotNetCodeLocationFactory = null!;
		[Import] public Lazy<DbgLanguageService> DbgLanguageService = null!;
		[Import] public IModuleIdProvider ModuleIdProvider = null!;

		public IEnumerable<string> MergedResourceDictionaries {
			get { yield break; }
		}

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "dnSpy MCP Server Extension",
		};

		public void OnEvent(ExtensionEvent @event, object? obj) {
			if (@event == ExtensionEvent.AppLoaded) {
				new Thread(() => {
					dnWindow.MainWindow.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() => {
						Global.MyTreeView = MyTreeView;
						Global.MyAppWindow = dnWindow;
						Global.MyDocumentTabService = MyTabService;
						Global.DbgManager = DbgManager.Value;
						Global.AttachableProcessesService = AttachableProcessesService.Value;
						Global.DbgCodeBreakpointsService = DbgCodeBreakpointsService.Value;
						Global.DbgDotNetCodeLocationFactory = DbgDotNetCodeLocationFactory.Value;
						Global.DbgLanguageService = DbgLanguageService.Value;
						Global.ModuleIdProvider = ModuleIdProvider;

						Global.DebugState.Attach(Global.DbgManager);

						Global.MySimpleMCPServer = new SimpleMcpServer(typeof(MCPCommands));
						Global.MySimpleMCPServer.Start();
					}));
				}).Start();
			}
		}

		public static string DumpSource(ModuleDocumentNode mod, MethodDef methodDef) {
			var decCtx = new DecompilationContext();
			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb)) {
				var indenter = new Indenter(4, 4, true);
				var textOutput = new TextWriterDecompilerOutput(sw, indenter);
				mod.Context.Decompiler.Decompile(methodDef, textOutput, decCtx);
			}
			try {
				return sb.ToString();
			}
			catch (ExternalException) {
			}
			return string.Empty;
		}

		public static string DumpSource(ModuleDocumentNode mod, TypeDef typeDef) {
			var decCtx = new DecompilationContext();
			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb)) {
				var indenter = new Indenter(4, 4, true);
				var textOutput = new TextWriterDecompilerOutput(sw, indenter);
				mod.Context.Decompiler.Decompile(typeDef, textOutput, decCtx);
			}
			try {
				return sb.ToString();
			}
			catch (ExternalException) {
			}
			return string.Empty;
		}

		public static string UpdateSource(ModuleDocumentNode modNode, MethodDef methodDef, string newCSharpBody) {
			string source = string.Empty;
			try {
				var retType = methodDef.ReturnType.FullName;
				var parameters = string.Join(", ", methodDef.Parameters.Where(p => !p.IsHiddenThisParameter).Select(p => p.Type.FullName + " " + p.Name));
				source = "using System;" + Environment.NewLine +
					"public static class __Patch {" + Environment.NewLine +
					$"  public static {retType} {methodDef.Name}({parameters}) {{" + Environment.NewLine +
					$"    {newCSharpBody}" + Environment.NewLine +
					"  }" + Environment.NewLine +
					"}" + Environment.NewLine;

				var tree = CSharpSyntaxTree.ParseText(source);
				var refs = new[] {
					MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
					MetadataReference.CreateFromFile(modNode.GetModule().Location)
				};
				var comp = CSharpCompilation.Create(
					"__PatchAsm",
					new[] { tree },
					refs,
					new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
				);
				using var ms = new MemoryStream();
				var result = comp.Emit(ms);
				if (!result.Success) {
					return "Compilation errors: " + string.Join(Environment.NewLine,
						result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
				}

				var patchMod = ModuleDefMD.Load(ms.ToArray());
				var patchType = patchMod.Types.First(t => t.Name == "__Patch");
				var patchMethod = patchType.Methods.First(m => m.Name == methodDef.Name);

				var body = methodDef.Body;
				body.Instructions.Clear();
				foreach (var instr in patchMethod.Body.Instructions)
					body.Instructions.Add(instr);

				Global.MyTreeView.TreeView.RefreshAllNodes();
				return "Updated method body of " + methodDef.Name;
			}
			catch (Exception) {
				return "Exception: Failed to update function";
			}
		}
	}
}
