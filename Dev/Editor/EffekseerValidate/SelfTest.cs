using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Effekseer;
using Effekseer.Data;

namespace EffekseerValidate
{
	// Walks a directory for .efkproj/.efkefc files. For each, loads via
	// Core, dumps via Core.SaveAsXmlDocument, and runs SchemaCheck against
	// the dump. Any error means our schema doesn't match what Core's IO
	// produces for the file - i.e. schema/IO.cs drift. Exit 0 only if
	// every file passes.
	//
	// This is the regression check for the hand-transcribed synthetic
	// schema entries (FCurveKeys*, GradientKey, axis fields) which can
	// silently diverge when upstream Effekseer syncs change IO.cs.
	public static class SelfTest
	{
		public static int Run(string dirPath)
		{
			var dir = Path.GetFullPath(dirPath);
			if (!Directory.Exists(dir))
			{
				Console.Error.WriteLine($"efkc: self-test directory not found: {dir}");  // [UAA]
				return 64;
			}

			var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
				.Where(f => f.EndsWith(".efkproj", StringComparison.OrdinalIgnoreCase)
					|| f.EndsWith(".efkefc", StringComparison.OrdinalIgnoreCase))
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.ToList();

			if (files.Count == 0)
			{
				Console.Error.WriteLine($"efkc: self-test found no .efkproj/.efkefc files in: {dir}");  // [UAA]
				return 1;
			}

			var schema = Schema.Generate();
			int pass = 0, fail = 0;
			var failures = new List<string>();

			foreach (var file in files)
			{
				var issues = CheckOne(file, schema);
				if (issues.Count == 0)
				{
					pass++;
					Console.WriteLine($"PASS  {file}");
				}
				else
				{
					fail++;
					failures.Add(file);
					Console.WriteLine($"FAIL  {file}  ({issues.Count} issue(s))");
					foreach (var i in issues.Take(5))
						Console.WriteLine($"      {i.Path}:{i.Line}:{i.Column}: {i.Severity}: {i.Message}");
					if (issues.Count > 5)
						Console.WriteLine($"      ... and {issues.Count - 5} more");
				}
			}

			Console.WriteLine();
			Console.WriteLine($"self-test: {pass} passed, {fail} failed, {files.Count} total");
			return fail == 0 ? 0 : 1;
		}

		static List<Issue> CheckOne(string path, Schema.Document schema)
		{
			// Load via Core without running the structural pass - the
			// self-test cares only about schema/IO drift, not whether
			// the file has all the required-by-modern-schema structural
			// pieces. Ancient-format files should be able to self-test
			// against the current schema.
			var loadIssues = new List<Issue>();
			if (!EffekseerLoad.LoadIntoCore(path, out var root, loadIssues))
				return loadIssues;

			var dump = EffekseerLoad.DumpToXDocument(root);
			if (dump == null)
				return new List<Issue> { Issue.Error(path, 0, 0, "dump failed") };

			return SchemaCheck.Run(path, dump, schema);
		}
	}
}