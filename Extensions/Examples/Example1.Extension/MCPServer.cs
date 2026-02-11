using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Metadata;

namespace Example1.Extension {
	static class Global {
		public static SimpleMcpServer? MySimpleMCPServer;
		public static IDocumentTreeView? MyTreeView;
		public static IAppWindow? MyAppWindow;
		public static IDocumentTabService? MyDocumentTabService;
		public static DbgManager? DbgManager;
		public static AttachableProcessesService? AttachableProcessesService;
		public static DbgCodeBreakpointsService? DbgCodeBreakpointsService;
		public static DbgDotNetCodeLocationFactory? DbgDotNetCodeLocationFactory;
		public static IModuleIdProvider? ModuleIdProvider;
		public static McpDebugState DebugState { get; } = new McpDebugState();
	}

	sealed class McpDebugState {
		readonly object gate = new object();
		bool attached;
		LastBreakInfo? lastBreak;

		public void Attach(DbgManager manager) {
			if (attached)
				return;
			attached = true;
			manager.MessageBoundBreakpoint += (s, e) => UpdateLastBreak(new LastBreakInfo {
				Kind = "breakpoint",
				BreakpointId = e.BoundBreakpoint?.Breakpoint?.Id,
				ThreadId = e.Thread?.Id,
				Message = "Breakpoint hit"
			});
			manager.MessageExceptionThrown += (s, e) => UpdateLastBreak(new LastBreakInfo {
				Kind = "exception",
				ThreadId = e.Exception.Thread?.Id,
				Exception = new {
					id = e.Exception.Id.ToString(),
					category = e.Exception.Id.Category,
					name = e.Exception.Id.HasName ? e.Exception.Id.Name : null,
					code = e.Exception.Id.HasCode ? e.Exception.Id.Code : (int?)null,
					message = e.Exception.Message,
					hresult = e.Exception.HResult,
					first_chance = e.Exception.IsFirstChance,
					second_chance = e.Exception.IsSecondChance,
					unhandled = e.Exception.IsUnhandled
				},
				Message = e.Exception.Message
			});
			manager.MessageStepComplete += (s, e) => UpdateLastBreak(new LastBreakInfo {
				Kind = "step_complete",
				ThreadId = e.Thread.Id,
				Message = e.Error
			});
			manager.MessageProgramBreak += (s, e) => UpdateLastBreak(new LastBreakInfo {
				Kind = "program_break",
				ThreadId = e.Thread?.Id,
				Message = "Program break"
			});
			manager.MessageEntryPointBreak += (s, e) => UpdateLastBreak(new LastBreakInfo {
				Kind = "entry_point_break",
				ThreadId = e.Thread?.Id,
				Message = "Entry point break"
			});
			manager.MessageBreak += (s, e) => UpdateLastBreak(new LastBreakInfo {
				Kind = "break",
				ThreadId = e.Thread?.Id,
				Message = "Break"
			});
		}

		void UpdateLastBreak(LastBreakInfo info) {
			info.Utc = DateTime.UtcNow;
			lock (gate) {
				lastBreak = info;
			}
		}

		public LastBreakInfo? GetLastBreak() {
			lock (gate) {
				return lastBreak;
			}
		}
	}

	sealed class LastBreakInfo {
		public string? Kind { get; set; }
		public int? BreakpointId { get; set; }
		public ulong? ThreadId { get; set; }
		public object? Exception { get; set; }
		public string? Message { get; set; }
		public DateTime Utc { get; set; }
	}

	class SimpleMcpServer {
		readonly HttpListener listener = new HttpListener();
		readonly Dictionary<string, MethodInfo> commands = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
		readonly Type commandSourceType;
		readonly JavaScriptSerializer jsonSerializer = new JavaScriptSerializer();
		readonly Dictionary<string, StreamWriter> sseSessions = new Dictionary<string, StreamWriter>();
		bool isRunning;

		public SimpleMcpServer(Type commandSourceType) {
			this.commandSourceType = commandSourceType;
			foreach (var method in commandSourceType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) {
				var attr = method.GetCustomAttribute<CommandAttribute>();
				if (attr != null)
					commands[attr.Name] = method;
			}

			var ipAddress = Environment.GetEnvironmentVariable("DNSPY_MCP_HOST");
			if (string.IsNullOrWhiteSpace(ipAddress))
				ipAddress = "+";
			var port = Environment.GetEnvironmentVariable("DNSPY_MCP_PORT");
			if (string.IsNullOrWhiteSpace(port))
				port = "3003";
			listener.Prefixes.Add("http://" + ipAddress + ":" + port + "/");
		}

		[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
		public sealed class CommandAttribute : Attribute {
			public string Name { get; }
			public string? MCPCmdDescription { get; set; }
			public CommandAttribute(string name) => Name = name;
		}

		public void Start() {
			if (isRunning)
				return;
			listener.Start();
			listener.BeginGetContext(OnRequest, null);
			isRunning = true;
		}

		public void Stop() {
			if (!isRunning)
				return;
			listener.Stop();
			isRunning = false;
		}

		void OnRequest(IAsyncResult ar) {
			HttpListenerContext? ctx = null;
			try {
				ctx = listener.EndGetContext(ar);
			}
			catch (ObjectDisposedException) {
				return;
			}
			catch (Exception ex) {
				Console.WriteLine("Listener error: " + ex.Message);
				return;
			}

			try {
				if (listener.IsListening)
					listener.BeginGetContext(OnRequest, null);
			}
			catch {
			}

			if (ctx.Request.HttpMethod == "GET") {
				HandleGet(ctx);
				return;
			}

			if (ctx.Request.HttpMethod == "POST") {
				HandlePost(ctx);
				return;
			}

			ctx.Response.StatusCode = 405;
			ctx.Response.OutputStream.Close();
		}

		void HandleGet(HttpListenerContext ctx) {
			var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
			if (path.EndsWith("/sse/") || path.EndsWith("/sse")) {
				ctx.Response.ContentType = "text/event-stream; charset=utf-8";
				ctx.Response.StatusCode = 200;
				ctx.Response.SendChunked = true;
				ctx.Response.KeepAlive = true;
				ctx.Response.Headers.Add("Cache-Control", "no-cache");
				ctx.Response.Headers.Add("X-Accel-Buffering", "no");

				var sessionId = GenerateSessionId();
				var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false), 1024, leaveOpen: true) {
					AutoFlush = true
				};

				lock (sseSessions) {
					sseSessions[sessionId] = writer;
				}

				var messagePath = "/message?sessionId=" + sessionId;
				writer.Write("event: endpoint\n");
				writer.Write("data: " + messagePath + "\n\n");
				return;
			}

			if (IsStreamablePath(path)) {
				WritePlain(ctx, "dnSpy MCP streamable HTTP endpoint. Use POST / or POST /mcp with JSON-RPC.");
				return;
			}

			ctx.Response.StatusCode = 404;
			ctx.Response.OutputStream.Close();
		}

		void HandlePost(HttpListenerContext ctx) {
			var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
			if (path.StartsWith("/message")) {
				HandlePostSse(ctx);
				return;
			}
			if (IsStreamablePath(path)) {
				HandlePostStreamableHttp(ctx);
				return;
			}

			ctx.Response.StatusCode = 404;
			ctx.Response.OutputStream.Close();
		}

		void HandlePostSse(HttpListenerContext ctx) {
			var sessionId = ctx.Request.QueryString["sessionId"];
			if (string.IsNullOrWhiteSpace(sessionId) || !HasSession(sessionId)) {
				ctx.Response.StatusCode = 400;
				WritePlain(ctx, "Invalid or missing sessionId.");
				return;
			}

			string jsonBody;
			using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) {
				jsonBody = reader.ReadToEnd();
			}

			ctx.Response.StatusCode = 202;
			WritePlain(ctx, "Accepted");

			if (string.IsNullOrWhiteSpace(jsonBody)) {
				SendSseError(sessionId, null, -32700, "Empty JSON body.");
				return;
			}

			Dictionary<string, object>? json;
			try {
				json = jsonSerializer.Deserialize<Dictionary<string, object>>(jsonBody);
			}
			catch (Exception ex) {
				SendSseError(sessionId, null, -32700, "Invalid JSON: " + ex.Message);
				return;
			}

			if (json == null) {
				SendSseError(sessionId, null, -32700, "Invalid JSON.");
				return;
			}

			ProcessRpcRequest(
				json,
				(id, result) => SendSseResult(sessionId, id, result),
				(id, code, message) => SendSseError(sessionId, id, code, message));
		}

		void HandlePostStreamableHttp(HttpListenerContext ctx) {
			string jsonBody;
			using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) {
				jsonBody = reader.ReadToEnd();
			}

			if (string.IsNullOrWhiteSpace(jsonBody)) {
				WriteJson(ctx, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Empty JSON body." } }, 400);
				return;
			}

			Dictionary<string, object>? json;
			try {
				json = jsonSerializer.Deserialize<Dictionary<string, object>>(jsonBody);
			}
			catch (Exception ex) {
				WriteJson(ctx, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Invalid JSON: " + ex.Message } }, 400);
				return;
			}
			if (json == null) {
				WriteJson(ctx, new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Invalid JSON." } }, 400);
				return;
			}

			bool hasResponse = false;
			object? responsePayload = null;
			ProcessRpcRequest(
				json,
				(id, result) => {
					hasResponse = true;
					responsePayload = new { jsonrpc = "2.0", id, result };
				},
				(id, code, message) => {
					hasResponse = true;
					responsePayload = new { jsonrpc = "2.0", id, error = new { code, message } };
				});

			if (!hasResponse) {
				ctx.Response.StatusCode = 202;
				ctx.Response.OutputStream.Close();
				return;
			}

			WriteJson(ctx, responsePayload!, 200);
		}

		void ProcessRpcRequest(
			Dictionary<string, object> json,
			Action<object?, object> onResult,
			Action<object?, int, string> onError) {
			json.TryGetValue("id", out var rpcId);
			if (!json.TryGetValue("method", out var methodObj) || !(methodObj is string method)) {
				onError(rpcId, -32600, "Missing or invalid method.");
				return;
			}

			try {
				switch (method) {
				case "initialize":
					onResult(rpcId, CreateInitializeResult());
					return;
				case "notifications/initialized":
					return;
				case "ping":
					onResult(rpcId, new { });
					return;
				case "tools/list":
					onResult(rpcId, CreateToolsListResult());
					return;
				case "tools/call":
					onResult(rpcId, CreateToolCallResult(json));
					return;
				default:
					onError(rpcId, -32601, "Method not found: " + method);
					return;
				}
			}
			catch (Exception ex) {
				onError(rpcId, -32603, "Internal error: " + ex.Message);
			}
		}

		object CreateInitializeResult() => new {
			protocolVersion = "2025-03-26",
			serverInfo = new { name = "dnSpy-MCP", version = "1.1.0" },
			capabilities = new { tools = new { }, resources = new { }, prompts = new { } }
		};

		object CreateToolsListResult() {
			var tools = new List<object>();
			foreach (var kvp in commands) {
				var method = kvp.Value;
				var attr = method.GetCustomAttribute<CommandAttribute>();
				var parameters = method.GetParameters();
				var props = new Dictionary<string, object>();
				var required = new List<string>();
				foreach (var p in parameters) {
					props[p.Name ?? "arg"] = new {
						type = GetJsonSchemaType(p.ParameterType)
					};
					if (!p.IsOptional && !IsNullable(p.ParameterType))
						required.Add(p.Name ?? "arg");
				}

				tools.Add(new {
					name = kvp.Key,
					description = attr?.MCPCmdDescription ?? ("Executes " + kvp.Key),
					inputSchema = new {
						type = "object",
						properties = props,
						required = required.Count == 0 ? null : required.ToArray()
					}
				});
			}

			return new { tools };
		}

		object CreateToolCallResult(Dictionary<string, object> json) {
			if (!json.TryGetValue("params", out var paramsObj) || !(paramsObj is Dictionary<string, object> p)) {
				throw new ArgumentException("Missing params.");
			}

			if (!p.TryGetValue("name", out var nameObj) || !(nameObj is string name)) {
				throw new ArgumentException("Missing tool name.");
			}

			var lookupName = NormalizeLegacyToolName(name);
			if (!commands.TryGetValue(lookupName, out var method)) {
				throw new ArgumentException("Unknown tool: " + name);
			}

			Dictionary<string, object>? args = null;
			if (p.TryGetValue("arguments", out var argsObj) && argsObj is Dictionary<string, object> argsDict)
				args = argsDict;

			var parameters = method.GetParameters();
			var invokeArgs = new object?[parameters.Length];
			for (int i = 0; i < parameters.Length; i++) {
				var param = parameters[i];
				object? value = null;
				if (args != null && args.TryGetValue(param.Name ?? string.Empty, out var provided))
					value = ConvertArgumentType(provided, param.ParameterType, param.Name ?? "arg");
				else if (param.IsOptional)
					value = param.DefaultValue;
				else if (IsNullable(param.ParameterType))
					value = null;
				else
					throw new ArgumentException("Missing required argument '" + param.Name + "'");

				invokeArgs[i] = value;
			}

			var result = method.Invoke(null, invokeArgs);
			var text = result == null
				? "null"
				: (result is string s ? s : jsonSerializer.Serialize(result));
			return new {
				content = new[] { new { type = "text", text } },
				structuredContent = result,
				isError = false
			};
		}

		void SendSseResult(string sessionId, object? id, object result) {
			var payload = new { jsonrpc = "2.0", id, result };
			SendData(sessionId, jsonSerializer.Serialize(payload));
		}

		void SendSseError(string sessionId, object? id, int code, string message) {
			var payload = new { jsonrpc = "2.0", id, error = new { code, message } };
			SendData(sessionId, jsonSerializer.Serialize(payload));
		}

		void SendData(string sessionId, string jsonData) {
			StreamWriter? writer;
			lock (sseSessions) {
				sseSessions.TryGetValue(sessionId, out writer);
			}
			if (writer == null)
				return;
			try {
				lock (writer) {
					writer.Write("data: " + jsonData + "\n\n");
					writer.Flush();
				}
			}
			catch (IOException) {
				CleanupSession(sessionId);
			}
			catch (ObjectDisposedException) {
				CleanupSession(sessionId);
			}
		}

		void CleanupSession(string sessionId) {
			lock (sseSessions) {
				if (sseSessions.TryGetValue(sessionId, out var writer)) {
					try { writer.Dispose(); } catch { }
					sseSessions.Remove(sessionId);
				}
			}
		}

		bool HasSession(string sessionId) {
			lock (sseSessions) {
				return sseSessions.ContainsKey(sessionId);
			}
		}

		static string GenerateSessionId() {
			using var rng = RandomNumberGenerator.Create();
			var bytes = new byte[16];
			rng.GetBytes(bytes);
			return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		}

		static void WritePlain(HttpListenerContext ctx, string text) {
			var buffer = Encoding.UTF8.GetBytes(text);
			ctx.Response.ContentType = "text/plain; charset=utf-8";
			ctx.Response.ContentLength64 = buffer.Length;
			ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
			ctx.Response.OutputStream.Close();
		}

		void WriteJson(HttpListenerContext ctx, object payload, int statusCode) {
			var json = jsonSerializer.Serialize(payload);
			var buffer = Encoding.UTF8.GetBytes(json);
			ctx.Response.StatusCode = statusCode;
			ctx.Response.ContentType = "application/json; charset=utf-8";
			ctx.Response.ContentLength64 = buffer.Length;
			ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
			ctx.Response.OutputStream.Close();
		}

		static bool IsStreamablePath(string path) =>
			path == "/" || path == string.Empty || path == "/mcp" || path == "/mcp/";

		static string GetJsonSchemaType(Type type) {
			type = Nullable.GetUnderlyingType(type) ?? type;
			if (type == typeof(string))
				return "string";
			if (type == typeof(bool))
				return "boolean";
			if (type.IsArray)
				return "array";
			if (type == typeof(int) || type == typeof(long) || type == typeof(short) ||
				type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) ||
				type == typeof(byte) || type == typeof(sbyte))
				return "integer";
			if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
				return "number";
			return "object";
		}

		static string NormalizeLegacyToolName(string name) {
			if (name.StartsWith("dnspy.", StringComparison.OrdinalIgnoreCase))
				return name.Substring("dnspy.".Length);
			return name;
		}

		static bool IsNullable(Type type) =>
			!type.IsValueType || Nullable.GetUnderlyingType(type) != null;

		static object? ConvertArgumentType(object argValue, Type requiredType, string paramName) {
			if (argValue == null) {
				if (IsNullable(requiredType))
					return null;
				throw new ArgumentNullException(paramName, "Null provided for non-nullable parameter '" + paramName + "'");
			}

			var underlying = Nullable.GetUnderlyingType(requiredType);
			if (underlying != null)
				return ConvertArgumentType(argValue, underlying, paramName);

			if (requiredType.IsInstanceOfType(argValue))
				return argValue;

			if (requiredType == typeof(int)) return Convert.ToInt32(argValue);
			if (requiredType == typeof(long)) return Convert.ToInt64(argValue);
			if (requiredType == typeof(short)) return Convert.ToInt16(argValue);
			if (requiredType == typeof(byte)) return Convert.ToByte(argValue);
			if (requiredType == typeof(uint)) return Convert.ToUInt32(argValue);
			if (requiredType == typeof(ulong)) return Convert.ToUInt64(argValue);
			if (requiredType == typeof(ushort)) return Convert.ToUInt16(argValue);
			if (requiredType == typeof(sbyte)) return Convert.ToSByte(argValue);
			if (requiredType == typeof(float)) return Convert.ToSingle(argValue);
			if (requiredType == typeof(double)) return Convert.ToDouble(argValue);
			if (requiredType == typeof(decimal)) return Convert.ToDecimal(argValue);
			if (requiredType == typeof(bool)) return Convert.ToBoolean(argValue);

			if (requiredType.IsEnum)
				return Enum.Parse(requiredType, argValue.ToString() ?? string.Empty, true);

			if (requiredType.IsArray && argValue is System.Collections.ArrayList list) {
				var elementType = requiredType.GetElementType() ?? typeof(object);
				var typedArray = Array.CreateInstance(elementType, list.Count);
				for (int i = 0; i < list.Count; i++)
					typedArray.SetValue(Convert.ChangeType(list[i], elementType), i);
				return typedArray;
			}

			if (requiredType.IsArray && argValue is object[] objArray) {
				var elementType = requiredType.GetElementType() ?? typeof(object);
				var typedArray = Array.CreateInstance(elementType, objArray.Length);
				for (int i = 0; i < objArray.Length; i++)
					typedArray.SetValue(Convert.ChangeType(objArray[i], elementType), i);
				return typedArray;
			}

			return Convert.ChangeType(argValue, requiredType);
		}
	}
}
