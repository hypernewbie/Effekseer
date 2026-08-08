using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace EffekseerValidate
{
	// [UAA] - START - efkc: rename effekseer-validate to efkc with named subcommands
	// efkc: command-line interface for editor-side Effekseer effect files
	// (.efkefc and .efkproj). Subcommands keep read-only checks separate
	// from commands that write files:
	//
	//   validate   - structural/schema/round-trip/resource checks over files
	//                or directories (recursively searched for .efkefc)
	//   resources  - inventory of every image/model/sound/material reference
	//   retarget   - rewrite resource path prefixes and save the result
	//   export     - cook the runtime .efk an application loads
	//   from-spec  - author an .efkefc from a strict JSON spec
	//   self-test  - Core/schema-drift check over a directory
	class Program
	{
		const string Usage =
			"usage: efkc <command> [options] <path>...\n" +
			"\n" +
			"  validate [options] PATH...\n" +
			"      validate .efkefc/.efkproj files; directories are searched\n" +
			"      recursively for .efkefc. Canonical paths are deduplicated\n" +
			"      and sorted before processing.\n" +
			"  resources INPUT [--json]\n" +
			"      inventory every image/model/sound/material resource reference\n" +
			"  retarget INPUT --from PREFIX --to PREFIX (--output OUT.efkefc | --dry-run) [--force]\n" +
			"      rewrite resource path prefixes (segment-aware) and save the result\n" +
			"  export INPUT --output OUT.efk [--magnification N] [--force]\n" +
			"      cook the runtime .efk an application loads\n" +
			"  from-spec SPEC.json --output OUT.efkefc [--force]\n" +
			"      author an effect from a JSON spec. Spec shape:\n" +
			"        name (string), settings {start_frame, end_frame, is_loop}\n" +
			"        (top level only), common {max_generation >= 1, spawn_count,\n" +
			"        lifetime, color [r,g,b,a] in [0,1]}, renderer {type:\n" +
			"        sprite|ribbon|ring|model|track|none, color_texture: relative path},\n" +
			"        children [same shape]. spawn_count/lifetime accept an integer\n" +
			"        or {center,min,max} with center == (min+max)/2; color is\n" +
			"        normalized and converted to editor 0..255 channels (round\n" +
			"        half away from zero). Integer fields reject fractional\n" +
			"        values; unknown fields, wrong types, invalid ranges and\n" +
			"        out-of-range color channels are rejected; effective frame\n" +
			"        bounds (omitted keys default to 0/120) must satisfy\n" +
			"        start <= end. The output is verified before it replaces\n" +
			"        the destination (unique sibling temp, atomic force-aware move).\n" +
			"  self-test DIR\n" +
			"      walk DIR for .efkproj/.efkefc, run schema check against Core's\n" +
			"      own dump of each, require zero errors (Core/schema-drift check)\n" +
			"\n" +
			"validate options:\n" +
			"  --json                emit one JSON document with ordered per-file\n" +
			"                        results and a summary (stdout)\n" +
			"  --check               also run fixed-point round-trip check (load->save->load->save)\n" +
			"  --check-input         also compare original file vs editor's re-save\n" +
			"                        (.efkproj only; catches Core's silent elisions)\n" +
			"  --strict              treat warnings as errors (exit 1 instead of 2)\n" +
			"  --check-resources     missing resource references and resource-walker\n" +
			"                        blind spots become validation issues\n" +
			"  --schema-in <path>    load schema from this file instead of generating\n" +
			"  --schema-out <path>   write generated schema to this file (.json or .md)\n" +
			"\n" +
			"resources options:\n" +
			"  --json                emit JSON: ordered resource records (kind, location,\n" +
			"                        relative, absolute, exists), walker warnings, counts\n" +
			"\n" +
			"retarget options:\n" +
			"  --from <prefix>       resource path prefix to rewrite (non-empty)\n" +
			"  --to <prefix>         replacement prefix\n" +
			"  --output <out.efkefc> write the retargeted effect (fails if it exists\n" +
			"                        unless --force)\n" +
			"  --dry-run             report changes without writing (forbids --output)\n" +
			"  --force               overwrite an existing --output\n" +
			"\n" +
			"export options:\n" +
			"  --output <out.efk>    destination (required)\n" +
			"  --magnification <f>   scale applied by export (default 1.0; must be\n" +
			"                        finite and greater than zero)\n" +
			"  --force               overwrite an existing --output\n" +
			"\n" +
			"from-spec options:\n" +
			"  --output <out.efkefc> destination (required; also -o)\n" +
			"  --force               overwrite an existing --output\n" +
			"\n" +
			"  -h, --help            show this help\n" +
			"\n" +
			"exit codes:\n" +
			"  0  ok (no issues)\n" +
			"  1  errors found (or warnings with --strict)\n" +
			"  2  warnings only\n" +
			"  3  internal exception\n" +
			"  64 usage error (bad arguments)\n" +
			"\n" +
			"The legacy single-file form 'efkc [options] FILE' still maps to 'validate',\n" +
			"and the old mutation flags map to the named commands above (or fail with\n" +
			"a migration hint).\n";

		[STAThread]
#pragma warning disable SYSLIB0032 // HandleProcessCorruptedStateExceptions is obsolete in net9.0 but TestCSharp still uses it
		[HandleProcessCorruptedStateExceptions]
#pragma warning restore SYSLIB0032
		static int Main(string[] args)
		{
			if (!ArgsParser.TryParse(args, out var parsed, out var parseError, out var migrationHint))
			{
				Console.Error.WriteLine($"efkc: {parseError}");
				if (migrationHint != null)
					Console.Error.WriteLine($"efkc: {migrationHint}");
				Console.Error.WriteLine(Usage);
				return 64;
			}
			if (parsed.Help)
			{
				Console.WriteLine(Usage);
				return 0;
			}

			try
			{
				switch (parsed.Command)
				{
					case EfkcCommand.Validate:
						return ValidationRunner.Run(parsed);
					case EfkcCommand.Resources:
						return ResourcesCommand.Run(parsed);
					case EfkcCommand.Retarget:
						return RetargetCommand.Run(parsed);
					case EfkcCommand.Export:
						return ExportCommand.Run(parsed);
					case EfkcCommand.FromSpec:
						return FromSpecCommand.Run(parsed);
					case EfkcCommand.SelfTest:
						return SelfTest.Run(parsed.SelfTestDir!);
					default:
						Console.Error.WriteLine("efkc: no command given");
						Console.Error.WriteLine(Usage);
						return 64;
				}
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"efkc: internal exception: {ex.GetType().Name}: {ex.Message}");
				return 3;
			}
		}
	}

	public enum EfkcCommand
	{
		None,
		Validate,
		Resources,
		Retarget,
		Export,
		FromSpec,
		SelfTest,
	}

	public class Args
	{
		public EfkcCommand Command = EfkcCommand.None;
		public List<string> Paths = new List<string>();
		public bool Json;
		public bool Strict;
		public bool Check;
		public bool CheckInput;
		public bool CheckResources;
#pragma warning disable CS8632
		public string? SchemaIn;
		public string? SchemaOut;
		public string? From;
		public string? To;
		public string? Output;
		public string? SelfTestDir;
#pragma warning restore CS8632
		public bool DryRun;
		public bool Force;
		public float Magnification = 1.0f;
		public bool Help;
	}

	public static class ArgsParser
	{
		public static bool TryParse(string[] argv, out Args parsed, out string error, out string? hint)
		{
			parsed = new Args();
			error = "";
			hint = null;

			if (argv.Length == 0)
			{
				error = "missing command";
				return false;
			}

			if (argv[0] == "-h" || argv[0] == "--help")
			{
				parsed.Help = true;
				return true;
			}

			var head = argv[0];
			var rest = argv.Skip(1).ToArray();
			switch (head)
			{
				case "validate":
					parsed.Command = EfkcCommand.Validate;
					return ParseValidate(rest, parsed, out error);
				case "resources":
					parsed.Command = EfkcCommand.Resources;
					return ParseResources(rest, parsed, out error);
				case "retarget":
					parsed.Command = EfkcCommand.Retarget;
					return ParseRetarget(rest, parsed, out error);
				case "export":
					parsed.Command = EfkcCommand.Export;
					return ParseExport(rest, parsed, out error);
				case "from-spec":
					parsed.Command = EfkcCommand.FromSpec;
					return ParseFromSpec(rest, parsed, out error);
				case "self-test":
					parsed.Command = EfkcCommand.SelfTest;
					return ParseSelfTest(rest, parsed, out error);
				default:
					return TryParseLegacy(argv, parsed, out error, out hint);
			}
		}

		static bool TakeValue(string[] argv, ref int i, string opt, out string value, out string error)
		{
			error = "";
			value = "";
			if (i + 1 >= argv.Length)
			{
				error = $"{opt} requires an argument";
				return false;
			}
			value = argv[++i];
			return true;
		}

		static bool ParseValidate(string[] argv, Args parsed, out string error)
		{
			error = "";
			var positionals = new List<string>();
			for (int i = 0; i < argv.Length; i++)
			{
				var a = argv[i];
				switch (a)
				{
					case "-h":
					case "--help": parsed.Help = true; return true;
					case "--json": parsed.Json = true; break;
					case "--check": parsed.Check = true; break;
					case "--check-input": parsed.CheckInput = true; break;
					case "--strict": parsed.Strict = true; break;
					case "--check-resources": parsed.CheckResources = true; break;
					case "--schema-in":
						if (!TakeValue(argv, ref i, a, out var schemaIn, out error)) return false;
						parsed.SchemaIn = schemaIn;
						break;
					case "--schema-out":
						if (!TakeValue(argv, ref i, a, out var schemaOut, out error)) return false;
						parsed.SchemaOut = schemaOut;
						break;
					default:
						if (a.StartsWith("-"))
						{
							error = $"unknown option for validate: {a}";
							return false;
						}
						positionals.Add(a);
						break;
				}
			}
			if (positionals.Count == 0)
			{
				error = "validate requires at least one PATH (.efkefc file or directory)";
				return false;
			}
			parsed.Paths = positionals;
			return true;
		}

		static bool ParseResources(string[] argv, Args parsed, out string error)
		{
			error = "";
			var positionals = new List<string>();
			for (int i = 0; i < argv.Length; i++)
			{
				var a = argv[i];
				if (a == "--json") { parsed.Json = true; continue; }
				if (a == "-h" || a == "--help") { parsed.Help = true; return true; }
				if (a.StartsWith("-"))
				{
					error = $"unknown option for resources: {a}";
					return false;
				}
				positionals.Add(a);
			}
			if (positionals.Count == 0)
			{
				error = "resources requires INPUT (.efkefc or .efkproj file)";
				return false;
			}
			if (positionals.Count > 1)
			{
				error = $"resources accepts exactly one INPUT, got {positionals.Count}";
				return false;
			}
			parsed.Paths = positionals;
			return true;
		}

		static bool ParseRetarget(string[] argv, Args parsed, out string error)
		{
			error = "";
			var positionals = new List<string>();
			for (int i = 0; i < argv.Length; i++)
			{
				var a = argv[i];
				switch (a)
				{
					case "-h":
					case "--help": parsed.Help = true; return true;
					case "--from":
						if (!TakeValue(argv, ref i, a, out var from, out error)) return false;
						parsed.From = from;
						break;
					case "--to":
						if (!TakeValue(argv, ref i, a, out var to, out error)) return false;
						parsed.To = to;
						break;
					case "--output":
						if (!TakeValue(argv, ref i, a, out var outPath, out error)) return false;
						parsed.Output = outPath;
						break;
					case "--dry-run": parsed.DryRun = true; break;
					case "--force": parsed.Force = true; break;
					default:
						if (a.StartsWith("-"))
						{
							error = $"unknown option for retarget: {a}";
							return false;
						}
						positionals.Add(a);
						break;
				}
			}
			if (positionals.Count == 0)
			{
				error = "retarget requires INPUT (.efkefc or .efkproj file)";
				return false;
			}
			if (positionals.Count > 1)
			{
				error = $"retarget accepts exactly one INPUT, got {positionals.Count}";
				return false;
			}
			parsed.Paths = positionals;

			if (string.IsNullOrEmpty(parsed.From))
			{
				error = "retarget requires --from PREFIX (non-empty)";
				return false;
			}
			if (EffectEdit.PathSegments(parsed.From).Count == 0)
			{
				error = $"retarget requires --from PREFIX with at least one path segment, got: '{parsed.From}'";
				return false;
			}
			if (parsed.To == null)
			{
				error = "retarget requires --to PREFIX";
				return false;
			}
			if (parsed.DryRun && parsed.Output != null)
			{
				error = "retarget: --dry-run and --output are mutually exclusive";
				return false;
			}
			if (!parsed.DryRun && parsed.Output == null)
			{
				error = "retarget requires --output OUT.efkefc or --dry-run";
				return false;
			}
			return true;
		}

		static bool ParseExport(string[] argv, Args parsed, out string error)
		{
			error = "";
			var positionals = new List<string>();
			for (int i = 0; i < argv.Length; i++)
			{
				var a = argv[i];
				switch (a)
				{
					case "-h":
					case "--help": parsed.Help = true; return true;
					case "--output":
						if (!TakeValue(argv, ref i, a, out var outPath, out error)) return false;
						parsed.Output = outPath;
						break;
					case "--magnification":
						if (!TakeValue(argv, ref i, a, out var magStr, out error)) return false;
						if (!float.TryParse(magStr, System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out var mag))
						{
							error = $"--magnification expects a number, got: {magStr}";
							return false;
						}
						if (float.IsNaN(mag) || float.IsInfinity(mag) || mag <= 0f)
						{
							error = $"--magnification must be finite and greater than zero, got: {magStr}";
							return false;
						}
						parsed.Magnification = mag;
						break;
					case "--force": parsed.Force = true; break;
					default:
						if (a.StartsWith("-"))
						{
							error = $"unknown option for export: {a}";
							return false;
						}
						positionals.Add(a);
						break;
				}
			}
			if (positionals.Count == 0)
			{
				error = "export requires INPUT (.efkefc or .efkproj file)";
				return false;
			}
			if (positionals.Count > 1)
			{
				error = $"export accepts exactly one INPUT, got {positionals.Count}";
				return false;
			}
			parsed.Paths = positionals;
			if (parsed.Output == null)
			{
				error = "export requires --output OUT.efk";
				return false;
			}
			return true;
		}

		static bool ParseFromSpec(string[] argv, Args parsed, out string error)
		{
			error = "";
			var positionals = new List<string>();
			for (int i = 0; i < argv.Length; i++)
			{
				var a = argv[i];
				switch (a)
				{
					case "-h":
					case "--help": parsed.Help = true; return true;
					case "--output":
					case "-o":
						if (!TakeValue(argv, ref i, a, out var outPath, out error)) return false;
						parsed.Output = outPath;
						break;
					case "--force": parsed.Force = true; break;
					default:
						if (a.StartsWith("-"))
						{
							error = $"unknown option for from-spec: {a}";
							return false;
						}
						positionals.Add(a);
						break;
				}
			}
			if (positionals.Count == 0)
			{
				error = "from-spec requires SPEC.json";
				return false;
			}
			if (positionals.Count > 1)
			{
				error = $"from-spec accepts exactly one SPEC.json, got {positionals.Count}";
				return false;
			}
			parsed.Paths = positionals;
			if (parsed.Output == null)
			{
				error = "from-spec requires --output OUT.efkefc";
				return false;
			}
			return true;
		}

		static bool ParseSelfTest(string[] argv, Args parsed, out string error)
		{
			error = "";
			if (argv.Length == 1 && (argv[0] == "-h" || argv[0] == "--help"))
			{
				parsed.Help = true;
				return true;
			}
			if (argv.Length != 1)
			{
				error = "self-test accepts exactly one DIR argument";
				return false;
			}
			if (argv[0].StartsWith("-"))
			{
				error = $"self-test accepts exactly one DIR argument, got option: {argv[0]}";
				return false;
			}
			parsed.SelfTestDir = argv[0];
			return true;
		}

		// Legacy flag-pile interface, kept for a short compatibility period.
		// Simple shapes translate to named commands; combinations that have no
		// named-command equivalent fail with a migration hint.
		static bool TryParseLegacy(string[] argv, Args parsed, out string error, out string? hint)
		{
			error = "";
			hint = null;
			var validateFlags = new List<string>();
#pragma warning disable CS8632
			string? schemaIn = null, schemaOut = null, selfTestDir = null;
			string? retargetSpec = null, savePath = null, exportEfkPath = null;
#pragma warning restore CS8632
			bool json = false, listResources = false, dryRun = false, hasMagnification = false;
			float magnification = 1.0f;
			var positionals = new List<string>();

			for (int i = 0; i < argv.Length; i++)
			{
				var a = argv[i];
				switch (a)
				{
					case "--json": json = true; break;
					case "--check": validateFlags.Add("--check"); break;
					case "--check-input": validateFlags.Add("--check-input"); break;
					case "--strict": validateFlags.Add("--strict"); break;
					case "-h":
					case "--help": parsed.Help = true; return true;
					case "--schema-in":
						if (!TakeValue(argv, ref i, a, out schemaIn, out error)) return false;
						break;
					case "--schema-out":
						if (!TakeValue(argv, ref i, a, out schemaOut, out error)) return false;
						break;
					case "--self-test":
						if (!TakeValue(argv, ref i, a, out selfTestDir, out error)) return false;
						break;
					case "--list-resources": listResources = true; break;
					case "--dry-run": dryRun = true; break;
					case "--retarget":
						if (!TakeValue(argv, ref i, a, out retargetSpec, out error)) return false;
						if (!retargetSpec.Contains('='))
						{
							error = $"--retarget expects FROM=TO, got: {retargetSpec}";
							return false;
						}
						break;
					case "--save":
						if (!TakeValue(argv, ref i, a, out savePath, out error)) return false;
						break;
					case "--export-efk":
						if (!TakeValue(argv, ref i, a, out exportEfkPath, out error)) return false;
						break;
					case "--magnification":
						if (!TakeValue(argv, ref i, a, out var magStr, out error)) return false;
						if (!float.TryParse(magStr, System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out magnification))
						{
							error = $"--magnification expects a number, got: {magStr}";
							return false;
						}
						if (float.IsNaN(magnification) || float.IsInfinity(magnification) || magnification <= 0f)
						{
							error = $"--magnification must be finite and greater than zero, got: {magStr}";
							return false;
						}
						hasMagnification = true;
						break;
					default:
						if (a.StartsWith("--"))
						{
							error = $"unknown option: {a}";
							return false;
						}
						positionals.Add(a);
						break;
				}
			}

			// --self-test short-circuits positional validation, but still rejects
			// extra positionals (the named command takes exactly one DIR; the
			// dir itself is consumed by the --self-test flag, so any positional
			// left over is an extra argument).
			if (selfTestDir != null)
			{
				if (positionals.Count > 0)
				{
					error = $"unexpected extra arguments with --self-test: {string.Join(" ", positionals)}";
					return false;
				}
				parsed.Command = EfkcCommand.SelfTest;
				parsed.SelfTestDir = selfTestDir;
				return true;
			}

			if (positionals.Count == 0)
			{
				error = "missing <path>";
				return false;
			}
			if (positionals.Count > 1)
			{
				error = $"unexpected extra arguments after <path>: {string.Join(" ", positionals.Skip(1))}";
				return false;
			}
			var path = positionals[0];

			if (listResources)
			{
				if (retargetSpec != null || savePath != null || exportEfkPath != null
					|| dryRun || hasMagnification || validateFlags.Count > 0
					|| schemaIn != null || schemaOut != null)
				{
					hint = "--list-resources now maps to 'efkc resources INPUT [--json]' and cannot be combined with validation or mutation flags";
					error = "--list-resources cannot be combined with other legacy flags";
					return false;
				}
				parsed.Command = EfkcCommand.Resources;
				parsed.Paths.Add(path);
				parsed.Json = json;
				return true;
			}

			if (exportEfkPath != null && retargetSpec == null)
			{
				if (savePath != null || dryRun || validateFlags.Count > 0 || schemaIn != null || schemaOut != null)
				{
					hint = "--export-efk now maps to 'efkc export INPUT --output OUT.efk [--magnification N] [--force]'";
					error = "--export-efk cannot be combined with --save, --dry-run or validation flags";
					return false;
				}
				parsed.Command = EfkcCommand.Export;
				parsed.Paths.Add(path);
				parsed.Output = exportEfkPath;
				parsed.Magnification = magnification;
				parsed.Force = true; // legacy --export-efk overwrote silently
				return true;
			}

			if (retargetSpec != null)
			{
				var separator = retargetSpec.IndexOf('=');
				var from = retargetSpec.Substring(0, separator);
				var to = retargetSpec.Substring(separator + 1);
				if (from.Length == 0 || EffectEdit.PathSegments(from).Count == 0)
				{
					error = "--retarget needs a non-empty FROM prefix with at least one path segment";
					return false;
				}
				if (savePath != null && exportEfkPath != null)
				{
					hint = "run 'efkc retarget INPUT --from .. --to .. --output OUT.efkefc' then 'efkc export OUT.efkefc --output OUT.efk'";
					error = "--save and --export-efk cannot be combined with --retarget in one invocation";
					return false;
				}
				if (dryRun && savePath != null)
				{
					error = "--retarget --dry-run and --save are mutually exclusive";
					return false;
				}
				if (validateFlags.Count > 0 || schemaIn != null || schemaOut != null)
				{
					hint = "retarget takes --from/--to/--output/--dry-run/--force only";
					error = "validation flags are not accepted by retarget";
					return false;
				}
				parsed.Command = EfkcCommand.Retarget;
				parsed.Paths.Add(path);
				parsed.From = from;
				parsed.To = to;
				parsed.DryRun = dryRun;
				if (savePath != null)
				{
					parsed.Output = savePath;
					parsed.Force = true; // legacy --save overwrote silently
				}
				else if (exportEfkPath != null)
				{
					hint = "run 'efkc retarget INPUT --from .. --to .. --output OUT.efkefc' then 'efkc export OUT.efkefc --output OUT.efk'";
					error = "--retarget with --export-efk needs a --save destination first in the new interface";
					return false;
				}
				// (no --save, no --export-efk, no --dry-run): the retarget
				// command itself rejects the missing destination.
				if (json)
				{
					hint = "retarget does not emit JSON; drop --json";
					error = "--json is not supported for retarget";
					return false;
				}
				return true;
			}

			if (savePath != null || dryRun || hasMagnification)
			{
				hint = "mutation flags now map to named commands: 'efkc retarget', 'efkc export', 'efkc resources'";
				error = "validation form cannot be combined with mutation flags";
				return false;
			}

			// Plain single-file validation form.
			parsed.Command = EfkcCommand.Validate;
			parsed.Paths.Add(path);
			parsed.Json = json;
			parsed.Check = validateFlags.Contains("--check");
			parsed.CheckInput = validateFlags.Contains("--check-input");
			parsed.Strict = validateFlags.Contains("--strict");
			parsed.SchemaIn = schemaIn;
			parsed.SchemaOut = schemaOut;
			return true;
		}
	}
}
// [UAA] - END
