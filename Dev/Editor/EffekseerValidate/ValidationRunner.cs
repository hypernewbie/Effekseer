using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EffekseerValidate
{
	// [UAA] - START - efkc validate command: corpus expansion and prepared-schema session
	// The validate command. Expands PATH... into a canonical, deduplicated,
	// sorted list of .efkefc files (explicit .efkproj files are kept), then
	// runs every file through Validator.Run with one prepared schema. Core.Root
	// is global mutable state, so files are processed sequentially and the
	// loop continues after a failed file.
	public static class ValidationRunner
	{
		public sealed class FileResult
		{
			public string Path; // canonical absolute path
			public List<Issue> Issues = new List<Issue>();
			public string Status; // ok | warning | error (strict applied)
		}

		public static int Run(Args args)
		{
			// Schema setup happens once per invocation, not once per file.
			if (!Validator.PrepareSchema(
					new Validator.Options { SchemaIn = args.SchemaIn, SchemaOut = args.SchemaOut },
					out var schema,
					out var schemaIssues))
			{
					// Under --json the machine contract still holds: emit one
					// error result so consumers always get a document.
					if (args.Json)
					{
						var failureResults = new List<FileResult>
						{
							ErrorResult(args.SchemaIn ?? "",
								schemaIssues.Count > 0 ? schemaIssues[0].Message : "schema preparation failed"),
						};
						CliOutput.EmitValidateJson(failureResults, args.Strict);
					}
					else
					{
						foreach (var issue in schemaIssues)
							Console.Error.WriteLine($"efkc: {issue.Message}");
					}
					return 1;
				}

			var options = new Validator.Options
			{
				Check = args.Check,
				CheckInput = args.CheckInput,
				Strict = args.Strict,
				CheckResources = args.CheckResources,
				SchemaIn = args.SchemaIn,
				SchemaOut = args.SchemaOut,
			};

			var results = new List<FileResult>();
			foreach (var input in ExpandInputs(args.Paths, results))
			{
				var issues = Validator.Run(input, options, schema);
				results.Add(new FileResult
				{
					Path = input,
					Issues = issues,
					Status = CliOutput.StatusFor(issues, args.Strict),
				});
			}

			// Fully deterministic order: synthetic input errors and file
			// results are all sorted by canonical path, deduplicated first so
			// a repeated missing-path argument cannot duplicate the record.
			var seen = new HashSet<string>(PathComparer);
			results = results.Where(r => seen.Add(r.Path)).ToList();
			results.Sort((a, b) => PathComparer.Compare(a.Path, b.Path));

			if (args.Json)
				CliOutput.EmitValidateJson(results, args.Strict);
			else
				CliOutput.EmitValidateHuman(results);

			return CliOutput.ValidateExitCode(results);
		}

		// Expands files and directories into the canonical list. Missing files
		// and empty directories become synthetic error results so they show up
		// in the output (and fail the run) rather than being silently skipped.
		static List<string> ExpandInputs(List<string> inputs, List<FileResult> results)
		{
			var files = new List<string>();
			foreach (var raw in inputs)
			{
				var full = Path.GetFullPath(raw);
				if (Directory.Exists(full))
				{
					var found = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
						.Where(f => f.EndsWith(".efkefc", StringComparison.OrdinalIgnoreCase))
						.Select(Path.GetFullPath)
						.OrderBy(f => f, StringComparer.Ordinal)
						.ToList();
					if (found.Count == 0)
						results.Add(ErrorResult(full, $"no .efkefc files found in directory: {raw}"));
					files.AddRange(found);
				}
				else if (File.Exists(full))
				{
					// Explicit files keep any extension: .efkproj is still
					// supported when passed directly.
					files.Add(full);
				}
				else
				{
					results.Add(ErrorResult(full, $"file not found: {raw}"));
				}
			}
			return files
				.Distinct(PathComparer)
				.OrderBy(f => f, PathComparer)
				.ToList();
		}

		// Case-insensitive on case-insensitive filesystems (Windows), exact
		// elsewhere, so canonical-path dedup matches what the filesystem
		// actually treats as the same file.
		static readonly StringComparer PathComparer =
			System.Environment.OSVersion.Platform == PlatformID.Win32NT
				? StringComparer.OrdinalIgnoreCase
				: StringComparer.Ordinal;

		static FileResult ErrorResult(string path, string message)
		{
			return new FileResult
			{
				Path = path,
				Status = "error",
				Issues = new List<Issue> { Issue.Error(path, 0, 0, message) },
			};
		}
	}
}
// [UAA] - END
