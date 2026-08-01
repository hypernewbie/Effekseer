// [UAA] - START - driver for headless effect editing and export
using System;
using System.Collections.Generic;
using System.Linq;
using Effekseer.Data;

namespace EffekseerValidate
{
	// Drives the editing and export options. Kept apart from Validator so the
	// validation path stays read-only and unchanged: these options mutate or
	// produce files, which is a different contract entirely.
	public static class EditCommand
	{
		public const int ExitOk = 0;
		public const int ExitFailure = 1;
		public const int ExitLoadFailure = 4;
		public const int ExitUsage = 64;

		public static int Run(string fullPath, Args args)
		{
			if (!EffekseerLoad.TryLoad(fullPath, out _, out var root, out var issues))
			{
				foreach (var issue in issues.Where(i => i.Severity == Severity.Error))
					Console.Error.WriteLine($"effekseer-validate: {issue.Message}");
				return ExitLoadFailure;
			}

			if (root == null)
			{
				Console.Error.WriteLine("effekseer-validate: the effect loaded but produced no tree");
				return ExitLoadFailure;
			}

			if (args.ListResources)
				ListResources(root);

			var mutated = false;

			if (args.Retarget != null)
			{
				var separator = args.Retarget.IndexOf('=');
				var from = args.Retarget.Substring(0, separator);
				var to = args.Retarget.Substring(separator + 1);

				if (from.Length == 0)
				{
					Console.Error.WriteLine("effekseer-validate: --retarget needs a non-empty FROM prefix");
					return ExitUsage;
				}

				var rewritten = EffectEdit.Retarget(root, from, to, args.DryRun, Console.Out);
				if (rewritten == 0)
				{
					Console.Error.WriteLine($"effekseer-validate: no resource path started with {from}");
					return ExitFailure;
				}

				Console.WriteLine(
					args.DryRun
						? $"would retarget {rewritten} resource reference(s)"
						: $"retargeted {rewritten} resource reference(s)");
				mutated = !args.DryRun;
			}

			// Refuse to silently discard edits. Retargeting without a destination
			// would look like it worked and change nothing on disk.
			if (mutated && args.Save == null && args.ExportEfk == null)
			{
				Console.Error.WriteLine(
					"effekseer-validate: edits were made but not written; pass --save <out.efkefc> " +
					"or --export-efk <out.efk> (or --dry-run to only preview)");
				return ExitUsage;
			}

			if (args.Save != null)
			{
				if (!EffectEdit.SaveEfkEfc(root, args.Save, out var error))
				{
					Console.Error.WriteLine($"effekseer-validate: {error}");
					return ExitFailure;
				}
				Console.WriteLine($"saved {args.Save}");
			}

			if (args.ExportEfk != null)
			{
				if (!EffectEdit.ExportEfk(root, args.ExportEfk, args.Magnification, out var error))
				{
					Console.Error.WriteLine($"effekseer-validate: {error}");
					return ExitFailure;
				}
				Console.WriteLine($"exported {args.ExportEfk} (magnification {args.Magnification})");
			}

			return ExitOk;
		}

		static void ListResources(NodeRoot root)
		{
			var resources = EffectEdit.CollectResources(root);
			var used = resources.Where(r => !string.IsNullOrEmpty(r.Relative)).ToList();

			foreach (var resource in used)
				Console.WriteLine($"{resource.Kind}\t{resource.Owner}\t{resource.Relative}");

			Console.WriteLine(
				$"{used.Count} resource reference(s), {resources.Count - used.Count} empty slot(s)");
		}
	}
}
// [UAA] - END
