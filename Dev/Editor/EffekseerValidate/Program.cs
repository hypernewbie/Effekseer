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