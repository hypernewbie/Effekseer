using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Effekseer;
using Effekseer.Data;

namespace EffekseerValidate
{
#pragma warning disable CS8632 // nullable annotations not enabled project-wide
	// Orchestrates the validation pipeline: format-aware safe load (which owns
	// the structural pass) + optional fixed-point round-trip check. The
	// structural pass operates on the raw XDocument - either the on-disk XML
	// for .efkproj or the dumped XML for .efkefc - never on the parsed
	// Effekseer object. That's the whole point of the validator (Core silently
	// masks errors).
	public static class Validator
	{
		// [UAA] - START - efkc: prepared-schema validate sessions and --check-resources
		public class Options
		{
			public bool Check { get; set; } = false;
			public bool CheckInput { get; set; } = false;
			public bool Strict { get; set; } = false;
			public bool CheckResources { get; set; } = false;
			public string? SchemaIn { get; set; } = null;   // load schema from this path
			public string? SchemaOut { get; set; } = null;  // write schema to this path (.json or .md)
		}

		// Schema setup shared by the whole validate invocation: load from disk
		// if requested, otherwise generate; optionally write to disk for AI
		// consumption. A schema-prep failure is a run-level error (no files are
		// processed), not a per-file issue.
		public static bool PrepareSchema(Options options, out Schema.Document schema, out List<Issue> issues)
		{
			issues = new List<Issue>();
			schema = null;

			if (options.SchemaIn != null)
			{
				if (!TryLoadSchema(options.SchemaIn, out var loaded, out var schemaError))
				{
					issues.Add(Issue.Error("", 0, 0, $"--schema-in {options.SchemaIn}: {schemaError}"));
					return false;
				}
				schema = loaded!;
			}
			else
			{
				schema = Schema.Generate();
			}

			if (options.SchemaOut != null)
			{
				try
				{
					WriteSchema(schema, options.SchemaOut);
				}
				catch (Exception ex)
				{
					// A user-supplied --schema-out path that cannot be written is a
					// run-level error (exit 1), not an internal exception (exit 3).
					issues.Add(Issue.Error(options.SchemaOut, 0, 0,
						$"--schema-out {options.SchemaOut}: could not write schema: {ex.GetType().Name}: {ex.Message}"));
					return false;
				}
			}

			return true;
		}

		// The detailed per-file result: issues plus the optional resource
		// audit. Audit is null unless the caller requested --check-resources,
		// so JSON stays byte-compatible when the flag is absent.
		public sealed class ValidatorResult
		{
			public List<Issue> Issues = new List<Issue>();
			public ResourceCheck.ResourceAudit Audit = null;
		}

		// Compatibility wrapper: existing callers (e.g. retarget verification)
		// keep `Run(...) -> List<Issue>` unchanged.
		public static List<Issue> Run(string rawPath, Options options, Schema.Document schema)
			=> RunDetailed(rawPath, options, schema).Issues;

		public static ValidatorResult RunDetailed(string rawPath, Options options, Schema.Document schema)
		{
			var issues = new List<Issue>();
			var audit = options.CheckResources ? new ResourceCheck.ResourceAudit { State = "notRun" } : null;
			var path = Path.GetFullPath(rawPath);

			// Warn if the loaded schema is from a different EffekseerCore version
			// - the schema reflects the type set of the version it was generated
			// against, and a stale schema silently validates against the wrong
			// model. Only relevant when the schema came from disk.
			if (options.SchemaIn != null
				&& !string.IsNullOrEmpty(schema.Version)
				&& schema.Version != Core.Version)
			{
				issues.Add(Issue.Warning(path, 0, 0,
					$"schema version {schema.Version} != current EffekseerCore {Core.Version} (may produce false positives/negatives)"));
			}

			if (!EffekseerLoad.TryLoad(path, out var doc, out var root, out var loadIssues))
			{
				issues.AddRange(loadIssues);
				if (audit != null) audit.Reason = "load_failed";
				return new ValidatorResult { Issues = issues, Audit = audit };
			}

			// TryLoad already ran the structural pass (so missing required
			// children, bad values, etc. are caught with line numbers before
			// Core.LoadFrom can NRE). Surface those issues here.
			issues.AddRange(loadIssues);

			// Schema-driven check operates on the RAW on-disk XDocument (the one
			// TryLoad returned), not on Core's re-save dump. The dump is
			// produced by Core from the same C# model the schema was
			// reflected from, so validating the dump would be a self-test
			// of Core's serializer rather than detection of user typos.
			//
			// For .efkproj, gate on ToolVersion: older formats are migrated
			// by Core.LoadFrom and their on-disk XML shape doesn't match the
			// current schema. Skipping the check on those avoids a flood of
			// false positives. AI-edited files are written by modern editors
			// and have current ToolVersion, so this gate doesn't blind us
			// to the failure mode we care about.
			//
			// The skip warning only fires in default (validation) mode; for
			// --check (round-trip) and --check-input the warning would just
			// clutter the fixed-point output.
			if (ShouldRunSchemaCheck(path, doc, schema))
				issues.AddRange(SchemaCheck.Run(path, doc, schema));
			else if (!options.Check && !options.CheckInput)
				issues.Add(Issue.Warning(path, 0, 0,
					"schema check skipped: file uses an older format that Core migrates on load; structural pass still ran"));

			// File-vs-input check catches the other silent-drop mode: the
			// editor's re-save lost elements that were in the original
			// file. Only meaningful for .efkproj (efkefc is binary).
			if (options.CheckInput && !issues.Any(i => i.Severity == Severity.Error))
				issues.AddRange(InputCheck.Run(path, doc, root));

			if (options.Check && !issues.Any(i => i.Severity == Severity.Error))
				issues.AddRange(RoundtripCheck.Run(path, root));

			// Resource gate: missing references and walker blind spots become
			// issues. Only meaningful once the file itself loaded cleanly.
			// The audit is explicit about why the walk did not run: a failed
			// load (load_failed, handled above) or any earlier error in this
			// pipeline (prior_validation_error). When the walk does run, its
			// own snapshot replaces the placeholder.
			if (options.CheckResources)
			{
				if (issues.Any(i => i.Severity == Severity.Error))
				{
					audit.Reason = "prior_validation_error";
				}
				else
				{
					var detailed = ResourceCheck.RunDetailed(path, root);
					issues.AddRange(detailed.Issues);
					audit = detailed.Audit;
				}
			}

			return new ValidatorResult { Issues = issues, Audit = audit };
		}

				// .efkefc has no raw on-disk XML to compare against (the EDIT
		// chunk is binary), so the schema check always runs - the
		// .efkefc document returned by TryLoad IS the in-memory XML
		// the editor would save-as-.efkproj.
		// .efkproj: skip if ToolVersion is missing or older than
		// Core.Version - the on-disk XML uses a historical format
		// that Core migrates on load; the current schema only matches
		// files in or near the current format.
		static bool ShouldRunSchemaCheck(string path, XDocument doc, Schema.Document schema)
		{
			if (path.EndsWith(".efkefc", StringComparison.OrdinalIgnoreCase))
				return true;

			var root = doc.Root;
			if (root == null) return false;
			var toolVersionEl = root.Element("ToolVersion");
			if (toolVersionEl == null || string.IsNullOrWhiteSpace(toolVersionEl.Value))
				return false;

			// Parse "X.Y" (Core.Version is "1.80.6" or "0.40α1" for old).
			// Compare major version. If file's major < Core's major, skip.
			var fileMajor = ParseMajorVersion(toolVersionEl.Value);
			var coreMajor = ParseMajorVersion(Core.Version);
			if (fileMajor == null || coreMajor == null) return false;
			return fileMajor >= coreMajor;
		}

		static int? ParseMajorVersion(string s)
		{
			var dot = s.IndexOf('.');
			var head = dot > 0 ? s.Substring(0, dot) : s;
			// Strip non-digit suffix like "0.40α1" -> "0"
			var digits = "";
			foreach (var c in head)
			{
				if (char.IsDigit(c)) digits += c;
				else break;
			}
			if (digits.Length == 0) return null;
			return int.TryParse(digits, out var v) ? v : null;
		}

		static void WriteSchema(Schema.Document schema, string path)
		{
			if (path.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase))
				Schema.WriteMarkdown(schema, path);
			else
				Schema.WriteJson(schema, path);
		}

		// Wrap Schema.LoadJson with explicit error reporting and sanity
		// checks. A missing file or empty Types dictionary silently produces
		// a "validator says everything is unknown" failure mode - worse than
		// no schema at all - so reject those cases up front.
		static bool TryLoadSchema(string path, out Schema.Document? doc, out string error)
		{
			doc = null;
			error = "";
			if (!File.Exists(path))
			{
				error = "file not found";
				return false;
			}
			string text;
			try { text = File.ReadAllText(path); }
			catch (Exception ex)
			{
				error = $"read failed: {ex.GetType().Name}: {ex.Message}";
				return false;
			}
			try { doc = System.Text.Json.JsonSerializer.Deserialize<Schema.Document>(text, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
			catch (Exception ex)
			{
				error = $"json parse failed: {ex.GetType().Name}: {ex.Message}";
				return false;
			}
			if (doc == null)
			{
				error = "deserialized to null";
				return false;
			}
			if (doc.Types == null || doc.Types.Count == 0)
			{
				error = "schema has no types (probably wrong file or empty object)";
				return false;
			}
			if (doc.RootElements == null || doc.RootElements.Count == 0)
			{
				error = "schema has no rootElements (probably wrong file or empty object)";
				return false;
			}
			return true;
		}
	}
	// [UAA] - END

	// Fixed-point round-trip check: load -> save -> load -> save -> compare.
	// If SaveAsXmlDocument is non-idempotent the second save must converge.
	// Drift on save#1 vs save#2 is a real bug in IO.cs (defaults being
	// re-elided, attribute reordering, etc).
	public static class RoundtripCheck
	{
		public static List<Issue> Run(string path, NodeRoot root)
		{
			var issues = new List<Issue>();

			XmlDocument doc1;
			try
			{
				doc1 = Core.SaveAsXmlDocument(root);
				if (doc1?.DocumentElement == null)
				{
					issues.Add(Issue.Error(path, 0, 0,
						"roundtrip: SaveAsXmlDocument returned empty doc on first pass"));
					return issues;
				}
			}
			catch (Exception ex)
			{
				issues.Add(Issue.Error(path, 0, 0,
					$"roundtrip: first SaveAsXmlDocument threw: {ex.GetType().Name}: {ex.Message}"));
				return issues;
			}

			XmlDocument doc2;
			try
			{
				var reloaded = Core.LoadFromXml(doc1, path);
				if (reloaded == null)
				{
					issues.Add(Issue.Error(path, 0, 0,
						"roundtrip: LoadFromXml returned null on first-pass dump"));
					return issues;
				}
				doc2 = Core.SaveAsXmlDocument(reloaded);
				if (doc2?.DocumentElement == null)
				{
					issues.Add(Issue.Error(path, 0, 0,
						"roundtrip: SaveAsXmlDocument returned empty doc on second pass"));
					return issues;
				}
			}
			catch (TargetInvocationException tie)
			{
				var inner = tie.InnerException ?? tie;
				issues.Add(Issue.Error(path, 0, 0,
					$"roundtrip: second pass threw: {inner.GetType().Name}: {inner.Message}"));
				return issues;
			}
			catch (Exception ex)
			{
				issues.Add(Issue.Error(path, 0, 0,
					$"roundtrip: second pass threw: {ex.GetType().Name}: {ex.Message}"));
				return issues;
			}

			var c1 = Canonicalize(doc1);
			var c2 = Canonicalize(doc2);
			if (c1 != c2)
			{
				var firstDiff = FirstDiffIndex(c1, c2);
				var ctx = ContextAround(c1, firstDiff, 80);
				issues.Add(Issue.Warning(path, 0, 0,
					$"roundtrip drift: doc1.Length={c1.Length}, doc2.Length={c2.Length}, first diff @ char {firstDiff}: ...{ctx}..."));
			}

			return issues;
		}

		static string Canonicalize(XmlDocument doc)
		{
			var sb = new StringBuilder();
			var settings = new XmlWriterSettings
			{
				Indent = true,
				OmitXmlDeclaration = true,
				NewLineHandling = NewLineHandling.Replace,
			};
			using (var writer = XmlWriter.Create(sb, settings))
				doc.Save(writer);
			return sb.ToString();
		}

		static int FirstDiffIndex(string a, string b)
		{
			var min = Math.Min(a.Length, b.Length);
			for (int i = 0; i < min; i++)
				if (a[i] != b[i]) return i;
			return min;
		}

		static string ContextAround(string s, int idx, int radius)
		{
			var lo = Math.Max(0, idx - radius);
			var hi = Math.Min(s.Length, idx + radius);
			return s.Substring(lo, hi - lo);
		}
	}
}
#pragma warning restore CS8632