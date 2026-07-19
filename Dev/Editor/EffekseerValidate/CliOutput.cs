using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EffekseerValidate
{
	// Format Issues for human (clang-style path:line:col: sev: msg) or JSON
	// (for AI consumption). Emits to stdout; exit code is computed separately.
	public static class CliOutput
	{
		public static void Emit(string path, List<Issue> issues, bool jsonMode)
		{
			if (jsonMode)
			{
				EmitJson(path, issues);
				return;
			}

			EmitHuman(issues);
		}

		// Exit codes:
		//   0 = OK (no errors, no warnings)
		//   1 = errors (or warnings with --strict)
		//   2 = warnings only, not --strict
		//   3 = internal exception (handled at the Main boundary, not here)
		public static int ExitCode(List<Issue> issues, bool strict)
		{
			var hasErrors = issues.Any(i => i.Severity == Severity.Error);
			var hasWarnings = issues.Any(i => i.Severity == Severity.Warning);
			if (hasErrors) return 1;
			if (hasWarnings && strict) return 1;
			if (hasWarnings) return 2;
			return 0;
		}

		static void EmitHuman(List<Issue> issues)
		{
			if (issues.Count == 0)
				return;

			foreach (var i in issues.OrderBy(i => i.Line).ThenBy(i => i.Column))
			{
				var sev = i.Severity == Severity.Error ? "error" : "warning";
				Console.WriteLine($"{i.Path}:{i.Line}:{i.Column}: {sev}: {i.Message}");
			}

			var errCount = issues.Count(i => i.Severity == Severity.Error);
			var warnCount = issues.Count(i => i.Severity == Severity.Warning);
			var summary = errCount > 0
				? $"{issues.Count} issue(s): {errCount} error(s), {warnCount} warning(s)"
				: $"{warnCount} warning(s)";
			Console.WriteLine(summary);
		}

		static void EmitJson(string path, List<Issue> issues)
		{
			var payload = new
			{
				path,
				ok = !issues.Any(i => i.Severity == Severity.Error),
				issues = issues.Select(i => new
				{
					line = i.Line,
					column = i.Column,
					severity = i.Severity == Severity.Error ? "error" : "warning",
					message = i.Message,
				}).ToArray(),
			};
			var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
			Console.WriteLine(JsonSerializer.Serialize(payload, options));
		}
	}
}