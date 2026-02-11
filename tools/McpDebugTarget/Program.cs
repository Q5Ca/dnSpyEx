using System;
using System.Threading;

namespace McpDebugTarget {
	class Program {
		static void Main(string[] args) {
			Console.WriteLine("McpDebugTarget starting...");
			Console.WriteLine("PID: " + System.Diagnostics.Process.GetCurrentProcess().Id);

			bool wait = false;
			int delaySeconds = 10;
			int iterations = 10;
			foreach (var a in args ?? Array.Empty<string>()) {
				if (string.Equals(a, "--wait", StringComparison.OrdinalIgnoreCase))
					wait = true;
				if (a != null && a.StartsWith("--delay-seconds=", StringComparison.OrdinalIgnoreCase)) {
					if (int.TryParse(a.Substring("--delay-seconds=".Length), out var parsed) && parsed >= 0)
						delaySeconds = parsed;
				}
				if (a != null && a.StartsWith("--iterations=", StringComparison.OrdinalIgnoreCase)) {
					if (int.TryParse(a.Substring("--iterations=".Length), out var parsed))
						iterations = parsed;
				}
			}
			if (wait) {
				Console.WriteLine("Press ENTER to begin loop.");
				Console.ReadLine();
			}
			else {
				Console.WriteLine("Auto-starting loop in " + delaySeconds + " seconds. (Pass --wait to pause)");
				Thread.Sleep(delaySeconds * 1000);
			}

			var worker = new Worker();
			if (iterations <= 0) {
				int i = 0;
				while (true) {
					var result = worker.Compute(i);
					Console.WriteLine("Compute(" + i + ") = " + result);
					Thread.Sleep(500);
					i++;
				}
			}
			for (int i = 0; i < iterations; i++) {
				var result = worker.Compute(i);
				Console.WriteLine("Compute(" + i + ") = " + result);
				Thread.Sleep(500);
			}

			Console.WriteLine("Done. Press ENTER to exit.");
			Console.ReadLine();
		}
	}

	class Worker {
		public int Compute(int value) {
			int baseValue = 10;
			int sum = Add(baseValue, value);
			return sum * 2;
		}

		int Add(int a, int b) {
			return a + b;
		}
	}
}
