// [UAA] - START - efkc subcommand drivers: resources, retarget, export
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Effekseer.Data;

namespace EffekseerValidate
{
	// resources INPUT [--json]: inventory every image/model/sound/material
	// reference in one effect file. Read-only; exit 0 even when referenced
	// files are missing (the gate for that is validate --check-resources).
	public static class ResourcesCommand
	{
		public static int Run(Args args)
		{
			var input = args.Paths[0];
			if (!EffekseerLoad.TryLoad(input, out _, out var root, out var issues))
			{
				foreach (var issue in issues.Where(i => i.Severity == Severity.Error))
					Console.Error.WriteLine($"efkc: {issue.Message}");
				return 1;
			}
			if (root == null)
			{
				Console.Error.WriteLine("efkc: the effect loaded but produced no tree");
				return 1;
			}

			var walk = EffectEdit.WalkResources(root);
			var used = walk.Resources.Where(r => !string.IsNullOrEmpty(r.Relative)).ToList();
			var missing = used.Count(r => !r.Exists);

			if (args.Json)
			{
				CliOutput.EmitResourcesJson(Path.GetFullPath(input), walk);
			}
			else
			{
				foreach (var r in used)
					Console.WriteLine($"{r.Kind}\t{r.Location}\t{r.Relative}");

				Console.WriteLine(
					$"{used.Count} resource reference(s), {walk.Resources.Count - used.Count} empty slot(s), {missing} missing");

				foreach (var w in walk.Warnings)
					Console.Error.WriteLine($"efkc: warning: {w}");
			}

			// Walker blind spots mean the inventory cannot promise completeness;
			// a distinct exit code lets scripts notice.
			return walk.Warnings.Count > 0 ? 2 : 0;
		}
	}

	// retarget INPUT --from PREFIX --to PREFIX (--output OUT | --dry-run)
	// [--force]: rewrite resource path prefixes (segment-aware) and save the
	// result. After a real retarget the output is reloaded and re-validated,
	// and the resource paths are checked to have actually landed.
	public static class RetargetCommand
	{
		public static int Run(Args args)
		{
			// The named-command parser already enforces these, but the legacy
			// flag translation can reach here with an incomplete contract.
			if (!args.DryRun && string.IsNullOrEmpty(args.Output))
			{
				Console.Error.WriteLine("efkc: retarget requires --output OUT.efkefc or --dry-run");
				return 64;
			}
			if (args.DryRun && !string.IsNullOrEmpty(args.Output))
			{
				Console.Error.WriteLine("efkc: --dry-run and --output are mutually exclusive");
				return 64;
			}

			var input = args.Paths[0];
			if (!EffekseerLoad.TryLoad(input, out _, out var root, out var issues))
			{
				foreach (var issue in issues.Where(i => i.Severity == Severity.Error))
					Console.Error.WriteLine($"efkc: {issue.Message}");
				return 1;
			}
			if (root == null)
			{
				Console.Error.WriteLine("efkc: the effect loaded but produced no tree");
				return 1;
			}

			// Pre-retarget inventory. Snapshot the non-empty absolute paths
			// BEFORE Retarget mutates the tree: they are the invariant against
			// which the save+reload is verified later. Walker blind spots mean
			// the inventory cannot promise completeness, which downgrades the
			// result to warning level.
			var preWalk = EffectEdit.WalkResources(root);
			var inventoryComplete = preWalk.Warnings.Count == 0;
			foreach (var w in preWalk.Warnings)
				Console.Error.WriteLine($"efkc: warning: {w}");
			var preAbsolutes = preWalk.Resources
				.Where(r => !string.IsNullOrEmpty(r.Relative))
				.Select(r => r.Absolute)
				.ToList();

			var changes = EffectEdit.Retarget(root, args.From!, args.To!, args.DryRun, Console.Out);
			if (changes.Count == 0)
			{
				Console.Error.WriteLine($"efkc: no resource path matched --from '{args.From}'");
				return 1;
			}

			Console.WriteLine(
				args.DryRun
					? $"would retarget {changes.Count} resource reference(s)"
					: $"retargeted {changes.Count} resource reference(s)");

			if (args.DryRun)
				return inventoryComplete ? 0 : 2;

			var output = args.Output!;
			if (File.Exists(output) && !args.Force)
			{
				Console.Error.WriteLine($"efkc: output exists: {output} (pass --force to overwrite)");
				return 1;
			}

			if (!EffectEdit.SaveEfkEfc(root, output, out var error))
			{
				Console.Error.WriteLine($"efkc: {error}");
				return 1;
			}
			Console.WriteLine($"saved {output}");

			return VerifyOutput(output, preAbsolutes, changes, inventoryComplete);
		}

		// Reload the saved output and run the same validation the validate
		// command runs (structural + schema), plus a resource check that the
		// retarget actually landed: the multiset of non-empty absolute paths
		// after reload must equal the pre-retarget set with every matched path
		// replaced by its rewritten form. Absolute paths are invariant across
		// save+reload - the file stores relative paths and Core re-resolves
		// them against the output directory, which the editor keeps pointing
		// at the same absolute asset. Missing resource FILES are reported but
		// do not fail the command; the gate for missing assets is
		// 'efkc validate --check-resources'.
		static int VerifyOutput(
			string output,
			List<string> preAbsolutes,
			List<EffectEdit.RetargetChange> changes,
			bool inventoryComplete)
		{
			var schema = Schema.Generate();
			var issues = Validator.Run(output, new Validator.Options { }, schema);
			CliOutput.EmitHumanFile(new ValidationRunner.FileResult { Path = output, Issues = issues, Status = CliOutput.StatusFor(issues, false) });
			var status = CliOutput.StatusFor(issues, strict: false);
			if (status == "error")
			{
				Console.Error.WriteLine($"efkc: retargeted output failed validation: {output}");
				return 1;
			}

			// Expected post-retarget absolutes. A reference rewritten to empty
			// (--to "" removing the whole path) is not present in either set,
			// so both sides skip empty paths and the comparison stays fair.
			var expected = new Dictionary<string, int>(StringComparer.Ordinal);
			var byOldAbsolute = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var ch in changes)
				byOldAbsolute[ch.OldAbsolute] = ch.NewAbsolute;
			foreach (var oldAbsolute in preAbsolutes)
			{
				var abs = byOldAbsolute.TryGetValue(oldAbsolute, out var newAbsolute) ? newAbsolute : oldAbsolute;
				if (string.IsNullOrEmpty(abs))
					continue;
				expected[abs] = expected.TryGetValue(abs, out var count) ? count + 1 : 1;
			}

			if (!EffekseerLoad.TryLoad(output, out _, out var reloadedRoot, out var reloadIssues))
			{
				foreach (var issue in reloadIssues.Where(i => i.Severity == Severity.Error))
					Console.Error.WriteLine($"efkc: reload of retargeted output failed: {issue.Message}");
				return 1;
			}

			var reloadWalk = EffectEdit.WalkResources(reloadedRoot);
			var reloadComplete = reloadWalk.Warnings.Count == 0;
			foreach (var w in reloadWalk.Warnings)
				Console.Error.WriteLine($"efkc: warning: {w}");
			var actual = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var r in reloadWalk.Resources)
			{
				if (string.IsNullOrEmpty(r.Relative))
					continue;
				actual[r.Absolute] = actual.TryGetValue(r.Absolute, out var count) ? count + 1 : 1;
			}

			if (!MultisetEqual(expected, actual, out var diff))
			{
				Console.Error.WriteLine("efkc: retarget did not land: reloaded output's resource paths differ from the expected set");
				foreach (var line in diff.Take(10))
					Console.Error.WriteLine($"efkc:   {line}");
				return 1;
			}

			var missing = actual.Count(kv => !File.Exists(kv.Key));
			if (missing > 0)
				Console.WriteLine(
					$"note: {missing} resource path(s) in {output} do not exist on disk (gate on them with 'efkc validate --check-resources')");

			// Blind spots in either inventory mean the retarget cannot be fully
			// verified; that is a warning-level result, not success.
			if (status == "error")
				return 1;
			if (status == "warning" || !inventoryComplete || !reloadComplete)
				return 2;
			return 0;
		}

		static bool MultisetEqual(Dictionary<string, int> expected, Dictionary<string, int> actual, out List<string> diff)
		{
			diff = new List<string>();
			foreach (var kv in expected)
			{
				if (!actual.TryGetValue(kv.Key, out var actualCount) || actualCount != kv.Value)
					diff.Add($"expected {kv.Value} instance(s) of {kv.Key}, found {(actual.TryGetValue(kv.Key, out var c) ? c : 0)}");
			}
			foreach (var kv in actual)
			{
				if (!expected.TryGetValue(kv.Key, out var expectedCount) || expectedCount != kv.Value)
					diff.Add($"unexpected {kv.Value} instance(s) of {kv.Key}");
			}
			return diff.Count == 0;
		}
	}

	// export INPUT --output OUT.efk [--magnification N] [--force]: cook the
	// runtime .ef an application loads.
	public static class ExportCommand
	{
		public static int Run(Args args)
		{
			if (string.IsNullOrEmpty(args.Output))
			{
				Console.Error.WriteLine("efkc: export requires --output OUT.efk");
				return 64;
			}

			var input = args.Paths[0];
			if (!EffekseerLoad.TryLoad(input, out _, out var root, out var issues))
			{
				foreach (var issue in issues.Where(i => i.Severity == Severity.Error))
					Console.Error.WriteLine($"efkc: {issue.Message}");
				return 1;
			}
			if (root == null)
			{
				Console.Error.WriteLine("efkc: the effect loaded but produced no tree");
				return 1;
			}

			var output = args.Output!;
			if (File.Exists(output) && !args.Force)
			{
				Console.Error.WriteLine($"efkc: output exists: {output} (pass --force to overwrite)");
				return 1;
			}

			if (!EffectEdit.ExportEfk(root, output, args.Magnification, out var error))
			{
				Console.Error.WriteLine($"efkc: {error}");
				return 1;
			}

			Console.WriteLine(
				$"exported {output} (magnification {args.Magnification.ToString(CultureInfo.InvariantCulture)})");
			return 0;
		}
	}
}
// [UAA] - END
