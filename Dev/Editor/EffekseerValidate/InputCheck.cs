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
	// File-vs-input comparison. Walks the original on-disk XDocument and
	// Core's re-save dump in parallel, reporting top-level and nested
	// elements that exist in the original but are missing from the
	// dump. Catches the silent-elision failure mode where an AI edits
	// a value and Core's load+save drops it without notification (the
	// `if (property == null) continue` guard in IO.cs).
	//
	// Only meaningful for .efkproj (the on-disk XML is the source of
	// truth). For .efkefc the on-disk representation is binary, so the
	// diff would always be "everything is different" - skipped.
	public static class InputCheck
	{
		public static List<Issue> Run(string path, XDocument originalDoc, NodeRoot root)
		{
			var issues = new List<Issue>();

			if (path.EndsWith(".efkefc", StringComparison.OrdinalIgnoreCase))
				return issues;

			XmlDocument dumped;
			try
			{
				dumped = Core.SaveAsXmlDocument(root);
				if (dumped?.DocumentElement == null)
				{
					issues.Add(Issue.Warning(path, 0, 0,
						"input check: dump returned empty doc"));
					return issues;
				}
			}
			catch (Exception ex)
			{
				issues.Add(Issue.Warning(path, 0, 0,
					$"input check: dump threw: {ex.GetType().Name}: {ex.Message}"));
				return issues;
			}

			var dumpDoc = ToXDocument(dumped);
			if (originalDoc.Root == null || dumpDoc.Root == null) return issues;

			WalkPair(originalDoc.Root, dumpDoc.Root, path, issues, depth: 0);
			return issues;
		}

		// Recursive walk: for each child of original, find a matching child
		// in dump (by name + first-match) and recurse. If no match, report
		// the missing element with the line number from original.
		// One-time warning marker so we emit a single truncation notice
		// per file rather than one per truncated subtree. Same shape as
		// SchemaCheck's depth cap - kept in sync manually since the two
		// walks are independent.
		const int MaxDepth = 48;
		static bool _truncationWarned;

		static void WalkPair(XElement original, XElement dump, string path, List<Issue> issues, int depth)
		{
			if (depth > MaxDepth)
			{
				if (!_truncationWarned)
				{
					_truncationWarned = true;
					issues.Add(Issue.Warning(path, 0, 0,
						$"input check truncated at depth {MaxDepth} (deeply nested element under <{original.Name.LocalName}> not compared)"));
				}
				return;
			}

			// Use a name -> indices map for dump children so we can match
			// siblings by name (in case dump's ordering shifted). Mark
			// matched indices so we don't reuse a child.
			var dumpByName = new Dictionary<string, Queue<XElement>>();
			foreach (var c in dump.Elements())
			{
				var n = c.Name.LocalName;
				if (!dumpByName.TryGetValue(n, out var q))
					dumpByName[n] = q = new Queue<XElement>();
				q.Enqueue(c);
			}

			foreach (var origChild in original.Elements())
			{
				var name = origChild.Name.LocalName;
				XElement matchedDump = null;
				if (dumpByName.TryGetValue(name, out var q) && q.Count > 0)
				{
					matchedDump = q.Dequeue();
				}

				if (matchedDump == null)
				{
					issues.Add(Issue.Warning(path, LineOf(origChild), 0,
						$"input check: original <{name}> not in editor's save (silent elision)"));
					continue;
				}

				WalkPair(origChild, matchedDump, path, issues, depth + 1);
			}
		}

		static XDocument ToXDocument(XmlDocument src)
		{
			using var ms = new MemoryStream();
			using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false }))
				src.Save(xw);
			ms.Position = 0;
			return XDocument.Load(ms, LoadOptions.SetLineInfo);
		}

		static int LineOf(XElement e)
		{
			return ((IXmlLineInfo)e).LineNumber;
		}
	}
}