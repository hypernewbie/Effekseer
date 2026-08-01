using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace EffekseerValidate
{
	class Program
	{
		const string Usage =
			"usage: effekseer-validate [<options>] <path>\n" +
			"       effekseer-validate --self-test <dir>\n" +
			"\n" +
			"  <path>              path to .efkproj or .efkefc\n" +
			"  --json              emit machine-readable JSON output\n" +
			"  --check             also run fixed-point round-trip check (load->save->load->save)\n" +
			"  --check-input       also compare original file vs editor's re-save\n" +
			"                       (.efkproj only; catches Core's silent elisions)\n" +
			"  --strict            treat warnings as errors (exit 1 instead of 2)\n" +
			"  --schema-out <path> write generated schema to this file (.json or .md)\n" +
			"  --schema-in <path>  load schema from this file instead of generating\n" +
			"  --self-test <dir>   walk dir for .efkproj/.efkefc, run schema check\n" +
			"                       against Core's own dump of each, require zero\n" +
			"                       errors. Catches schema/IO.cs drift.\n" +
			"  -h, --help          show this help\n" +
			// [UAA] - START - headless effect editing and export
			"\n" +
			"editing and export (effect authoring lives only in this C# core):\n" +
			"  --list-resources    list every image/model/sound/material reference\n" +
			"  --retarget FROM=TO  rewrite resource references whose relative path\n" +
			"                       starts with FROM so it starts with TO instead\n" +
			"  --dry-run           with --retarget, report changes without writing\n" +
			"  --save <out.efkefc> write the (possibly retargeted) effect\n" +
			"  --export-efk <out>  cook the runtime .efk an application loads\n" +
			"  --magnification <f> scale applied by --export-efk (default 1.0)\n" +
			// [UAA] - END
			"\n" +
			"\n" +
			"exit codes:\n" +
			"  0  ok (no issues)\n" +
			"  1  errors found (or warnings with --strict)\n" +
			"  2  warnings only\n" +
			"  3  internal exception\n" +
			"  64 usage error (bad arguments)\n";

		[STAThread]
#pragma warning disable SYSLIB0032 // HandleProcessCorruptedStateExceptions is obsolete in net9.0 but TestCSharp still uses it
		[HandleProcessCorruptedStateExceptions]
#pragma warning restore SYSLIB0032
		static int Main(string[] args)
		{
			if (!Args.TryParse(args, out var parsed, out var parseError))
			{
				Console.Error.WriteLine($"effekseer-validate: {parseError}");
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
				if (parsed.SelfTest != null)
					return SelfTest.Run(parsed.SelfTest);

				var fullPath = Path.GetFullPath(parsed.Path);

				// [UAA] - START - editing and export run instead of validation
				if (parsed.IsEditRequest)
					return EditCommand.Run(fullPath, parsed);
				// [UAA] - END

				var issues = Validator.Run(fullPath, new Validator.Options
				{
					Check = parsed.Check,
					CheckInput = parsed.CheckInput,
					Strict = parsed.Strict,
					SchemaIn = parsed.SchemaIn,
					SchemaOut = parsed.SchemaOut,
				});
				CliOutput.Emit(fullPath, issues, parsed.Json);
				return CliOutput.ExitCode(issues, parsed.Strict);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"effekseer-validate: internal exception: {ex.GetType().Name}: {ex.Message}");
				return 3;
			}
		}
	}

	public class Args
	{
		public string Path { get; set; }
		public bool Json { get; set; }
		public bool Check { get; set; }
		public bool Strict { get; set; }
		public bool Help { get; set; }
		public bool CheckInput { get; set; }
		public string? SelfTest { get; set; }
#pragma warning disable CS8632
		public string? SchemaIn { get; set; }
		public string? SchemaOut { get; set; }
		// [UAA] - START - headless effect editing and export
		public bool ListResources { get; set; }
		public string? Retarget { get; set; }
		public bool DryRun { get; set; }
		public string? Save { get; set; }
		public string? ExportEfk { get; set; }
		public float Magnification { get; set; } = 1.0f;

		public bool IsEditRequest =>
			ListResources || Retarget != null || Save != null || ExportEfk != null;
		// [UAA] - END
#pragma warning restore CS8632

		public static bool TryParse(string[] argv, out Args parsed, out string error)
		{
			parsed = new Args();
			error = null;
			var positional = new List<string>();
			for (int i = 0; i < argv.Length; i++)
			{
				var a = argv[i];
				switch (a)
				{
					case "--json": parsed.Json = true; break;
					case "--check": parsed.Check = true; break;
					case "--check-input": parsed.CheckInput = true; break;
					case "--strict": parsed.Strict = true; break;
					case "-h":
					case "--help": parsed.Help = true; break;
					case "--schema-in":
						if (i + 1 >= argv.Length) { error = $"--schema-in requires <path>"; return false; }
						parsed.SchemaIn = argv[++i]; break;
					case "--schema-out":
						if (i + 1 >= argv.Length) { error = $"--schema-out requires <path>"; return false; }
						parsed.SchemaOut = argv[++i]; break;
					case "--self-test":
						if (i + 1 >= argv.Length) { error = $"--self-test requires <dir>"; return false; }
						parsed.SelfTest = argv[++i]; break;
					// [UAA] - START - headless effect editing and export
					case "--list-resources": parsed.ListResources = true; break;
					case "--dry-run": parsed.DryRun = true; break;
					case "--retarget":
						if (i + 1 >= argv.Length) { error = $"--retarget requires FROM=TO"; return false; }
						parsed.Retarget = argv[++i];
						if (!parsed.Retarget.Contains('='))
						{
							error = $"--retarget expects FROM=TO, got: {parsed.Retarget}";
							return false;
						}
						break;
					case "--save":
						if (i + 1 >= argv.Length) { error = $"--save requires <path>"; return false; }
						parsed.Save = argv[++i]; break;
					case "--export-efk":
						if (i + 1 >= argv.Length) { error = $"--export-efk requires <path>"; return false; }
						parsed.ExportEfk = argv[++i]; break;
					case "--magnification":
						if (i + 1 >= argv.Length) { error = $"--magnification requires <number>"; return false; }
						if (!float.TryParse(argv[++i], System.Globalization.NumberStyles.Float,
										   System.Globalization.CultureInfo.InvariantCulture, out var magnification))
						{
							error = $"--magnification expects a number, got: {argv[i]}";
							return false;
						}
						parsed.Magnification = magnification;
						break;
					// [UAA] - END
					default:
						if (a.StartsWith("--"))
						{
							error = $"unknown option: {a}";
							return false;
						}
						positional.Add(a);
						break;
				}
			}
			// --help and --self-test short-circuit positional validation so
			// users can run them without a <path> argument.
			if (parsed.Help) return true;
			if (parsed.SelfTest != null) return true;
			if (positional.Count == 0)
			{
				error = "missing <path>";
				return false;
			}
			if (positional.Count > 1)
			{
				error = $"unexpected extra arguments after <path>: {string.Join(" ", positional.Skip(1))}";
				return false;
			}
			parsed.Path = positional[0];
			return true;
		}
	}
}