using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace EffekseerValidate
{
	// [UAA] - START - efkc JSON contract and policy-aware exit codes
	// Output formatting for efkc results. Machine output goes to stdout and
	// diagnostics to stderr, so under --json the two streams stay separable.
	// Exit codes are computed from the same policy the JSON records use, so a
	// JSON record can never claim a file passed while the process exits
	// nonzero (including warning-only files under --strict, which fold into
	// status "error").
	public static class CliOutput
	{
		static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
		};

		// Policy-aware status: warnings fold into "error" under --strict.
		public static string StatusFor(List<Issue> issues, bool strict)
		{
			if (issues.Any(i => i.Severity == Severity.Error)) return "error";
			if (issues.Any(i => i.Severity == Severity.Warning)) return strict ? "error" : "warning";
			return "ok";
		}

		// Exit codes: 0 = ok, 1 = errors (or warnings with --strict),
		// 2 = warnings only. 3 (internal exception) and 64 (usage error) are
		// handled at the Main boundary, not here.
		public static int ExitCode(List<Issue> issues, bool strict)
		{
			var status = StatusFor(issues, strict);
			return status == "error" ? 1 : status == "warning" ? 2 : 0;
		}

		public static int ValidateExitCode(IReadOnlyList<ValidationRunner.FileResult> results)
		{
			if (results.Any(r => r.Status == "error")) return 1;
			if (results.Any(r => r.Status == "warning")) return 2;
			return 0;
		}

		public static object IssueRecord(Issue i)
		{
			return new { code = i.Code, line = i.Line, column = i.Column, message = i.Message };
		}

		// Machine-readable resource audit. notRun states carry only state +
		// reason: counts were never produced, so no zeros are fabricated.
		static object AuditRecord(ResourceCheck.ResourceAudit a)
		{
			var record = new Dictionary<string, object> { ["state"] = a.State };
			if (a.State == "notRun")
			{
				record["reason"] = a.Reason;
			}
			else
			{
				record["referenced"] = a.Referenced;
				record["empty"] = a.Empty;
				record["missing"] = a.Missing;
				record["walkerBlindSpots"] = a.WalkerBlindSpots;
				record["blindSpotLocations"] = a.BlindSpotLocations;
			}
			return record;
		}

		static object ResultRecord(ValidationRunner.FileResult r)
		{
			var record = new Dictionary<string, object>
			{
				["path"] = r.Path,
				["status"] = r.Status,
				["errors"] = r.Issues.Where(i => i.Severity == Severity.Error).Select(IssueRecord).ToArray(),
				["warnings"] = r.Issues.Where(i => i.Severity == Severity.Warning).Select(IssueRecord).ToArray(),
			};
			// resourceAudit is absent entirely unless --check-resources ran, so
			// the pre-audit JSON shape remains compatible; issue codes are additive.
			if (r.Audit != null)
				record["resourceAudit"] = AuditRecord(r.Audit);
			return record;
		}

		public static void EmitValidateJson(IReadOnlyList<ValidationRunner.FileResult> results, bool strict)
		{
			var payload = new
			{
				results = results.Select(ResultRecord).ToArray(),
				summary = new
				{
					files = results.Count,
					ok = results.Count(r => r.Status == "ok"),
					warning = results.Count(r => r.Status == "warning"),
					error = results.Count(r => r.Status == "error"),
					strict,
					exitCode = ValidateExitCode(results),
				},
			};
			Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
		}

		public static void EmitValidateHuman(IReadOnlyList<ValidationRunner.FileResult> results)
		{
			foreach (var r in results)
				EmitHumanFile(r);

			if (results.Count > 1)
			{
				var ok = results.Count(r => r.Status == "ok");
				var warn = results.Count(r => r.Status == "warning");
				var err = results.Count(r => r.Status == "error");
				Console.WriteLine(
					$"summary: {results.Count} file(s): {ok} ok, {warn} warning, {err} error (exit {ValidateExitCode(results)})");
			}
		}

		public static void EmitHumanFile(ValidationRunner.FileResult r)
		{
			var issues = r.Issues.OrderBy(i => i.Line).ThenBy(i => i.Column).ToList();
			if (issues.Count == 0)
				return;

			foreach (var i in issues)
			{
				var sev = i.Severity == Severity.Error ? "error" : "warning";
				Console.WriteLine($"{i.Path}:{i.Line}:{i.Column}: {sev}: {i.Message}");
			}

			var errCount = issues.Count(i => i.Severity == Severity.Error);
			var warnCount = issues.Count(i => i.Severity == Severity.Warning);
			Console.WriteLine(errCount > 0
				? $"{issues.Count} issue(s): {errCount} error(s), {warnCount} warning(s)"
				: $"{warnCount} warning(s)");
		}

		public static void EmitResourcesJson(string path, EffectEdit.WalkResult walk)
		{
			var used = walk.Resources.Where(r => !string.IsNullOrEmpty(r.Relative)).ToList();
			var payload = new
			{
				path,
				resources = used.Select(r => new
				{
					kind = r.Kind,
					location = r.Location,
					relative = r.Relative,
					absolute = r.Absolute,
					exists = r.Exists,
				}).ToArray(),
				warnings = walk.Warnings.ToArray(),
				counts = new
				{
					total = used.Count,
					empty = walk.Resources.Count - used.Count,
					missing = used.Count(r => !r.Exists),
				},
			};
			Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
		}
	}
}
// [UAA] - END
