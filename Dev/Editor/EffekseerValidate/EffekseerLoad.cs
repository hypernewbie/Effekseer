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
	// Safe wrapper around Effekseer.Core.LoadFrom. Handles format detection
	// (.efkproj raw XML vs .efkefc binary container), well-formedness, the
	// comment pre-rejection that prevents an IO.cs NRE, and the silent-null /
	// NRE / throw failure-mode taxonomy.
	//
	// Format strategy:
	//   .efkproj  - parse raw XML on disk; Core.LoadFrom is also called so the
	//               NodeRoot is available for --check round-trip, but structural
	//               validation runs on the RAW XML (the whole point: catch the
	//               AI mistakes Core silently masks).
	//   .efkefc   - the on-disk EDIT chunk is a binary length-prefixed tree
	//               (see EfkEfcXml.Decompress, internal to EffekseerCore). Since
	//               the binary is opaque, we round-trip through Core (which
	//               parses the binary to NodeRoot in memory), then dump via
	//               SaveAsXmlDocument to get the canonical XML the editor would
	//               save-as-.efkproj, and validate that. Line numbers in the
	//               dumped XML do not correspond to the original binary, so
	//               issues from this path are labeled "(EDIT chunk)" in the
	//               display path so users know where the coordinates point.
	public static class EffekseerLoad
	{
		public static bool TryLoad(string path, out XDocument doc, out NodeRoot root, out List<Issue> issues)
		{
			doc = null;
			root = null;
			issues = new List<Issue>();

			if (!File.Exists(path))
			{
				issues.Add(Issue.Error(path, 0, 0, $"file not found: {path}"));
				return false;
			}

			byte[] raw;
			try { raw = File.ReadAllBytes(path); }
			catch (Exception ex)
			{
				issues.Add(Issue.Error(path, 0, 0, $"read failed: {ex.GetType().Name}: {ex.Message}"));
				return false;
			}

			if (raw.Length < 20)
			{
				issues.Add(Issue.Error(path, 0, 0, $"file too short ({raw.Length} bytes) to be a valid project"));
				return false;
			}

			bool isEfkEfc = raw[0] == (byte)'E' && raw[1] == (byte)'F' && raw[2] == (byte)'K' && raw[3] == (byte)'E';

			if (isEfkEfc)
				return LoadEfkEfc(path, raw, out doc, out root, issues);
			return LoadEfkProj(path, raw, out doc, out root, issues);
		}

		// Returns the dumped XML (post-migration, post-default-elision)
		// for the file at path, given that the file is already loaded.
		// Returns null if dump fails. Used for schema-driven validation
		// (which needs the modern in-memory representation, not the raw
		// on-disk XML which may be in an older format that Core silently
		// migrates on load).
#pragma warning disable CS8632
		public static XDocument? DumpToXDocument(NodeRoot root)
		{
			XmlDocument dumped;
			try
			{
				dumped = Core.SaveAsXmlDocument(root);
				if (dumped?.DocumentElement == null) return null;
			}
			catch { return null; }
			return ToXDocument(dumped);
		}

		// .efkproj: parse raw XML, run structural validation on it FIRST (so
		// missing required children, bad values, and comments get clean error
		// messages with line numbers instead of NREs from Core.LoadFrom), then
		// call Core.LoadFrom to populate Core.Root (so --check can round-trip).
		// If the structural pass finds errors we skip Core.LoadFrom - the file
		// is already known bad and Core.LoadFrom would only produce a stack trace.
		static bool LoadEfkProj(string path, byte[] raw, out XDocument doc, out NodeRoot root, List<Issue> issues)
		{
			doc = null;
			root = null;

			string xmlText;
			try { xmlText = DecodeUtf8WithBom(raw); }
			catch (Exception ex)
			{
				issues.Add(Issue.Error(path, 0, 0, $"utf-8 decode failed: {ex.Message}"));
				return false;
			}

			try { doc = XDocument.Parse(xmlText, LoadOptions.SetLineInfo); }
			catch (XmlException ex)
			{
				issues.Add(Issue.FromXmlException(path, ex));
				return false;
			}

			if (!RejectXmlComments(path, doc, issues))
				return false;

			issues.AddRange(StructuralPass.Run(path, doc));
			if (issues.Any(i => i.Severity == Severity.Error))
				return false;

			return LoadIntoCore(path, out root, issues);
		}

		// .efkefc: binary EDIT chunk. Load through Core (which parses the binary
		// via its internal EfkEfcXml.Decompress), then dump the in-memory tree
		// back to XML via SaveAsXmlDocument. The dumped XML is what the editor
		// would write if you "Save As .efkproj", so validating it is meaningful.
		// displayPath is annotated so users know line numbers point at the
		// dumped XML, not the original binary.
		static bool LoadEfkEfc(string path, byte[] raw, out XDocument doc, out NodeRoot root, List<Issue> issues)
		{
			doc = null;
			root = null;
			var displayPath = path + " (EDIT chunk)";

			if (!LoadIntoCore(path, out root, issues))
				return false;

			XmlDocument dumped;
			try { dumped = Core.SaveAsXmlDocument(root); }
			catch (Exception ex)
			{
				issues.Add(Issue.Error(path, 0, 0, $"efkefc dump failed: {ex.GetType().Name}: {ex.Message}"));
				return false;
			}
			if (dumped?.DocumentElement == null)
			{
				issues.Add(Issue.Error(path, 0, 0, "efkefc dump returned empty doc"));
				return false;
			}

			doc = ToXDocument(dumped);
			issues.AddRange(StructuralPass.Run(displayPath, doc));
			return true;
		}

		// Core.LoadFrom does Path.GetFullPath + global-static resets (dynamic_,
		// proceduralModels, recording) + LoadFromFile internally. Using it
		// instead of LoadFromFile keeps multi-call state hygienic. Wraps the
		// silent-null / TargetInvocationException-NRE / throw taxonomy that
		// the raw Core surface exposes (e.g. null deref on missing
		// EndFrame/StartFrame/IsLoop, version-throw on too-new tool version).
		// Load the file via Core.LoadFrom with all its global-static
		// resets. Wraps the silent-null / TargetInvocationException-NRE /
		// throw taxonomy that the raw Core surface exposes (e.g. null deref
		// on missing EndFrame/StartFrame/IsLoop, version-throw on too-new
		// tool version). Used by both EffekseerLoad.TryLoad (validator
		// path) and SelfTest (which wants Core's view without the
		// structural pass).
		public static bool LoadIntoCore(string path, out NodeRoot root, List<Issue> issues)
		{
			root = null;
			try
			{
				if (!Core.LoadFrom(path))
				{
					issues.Add(Issue.Error(path, 0, 0,
						"Core.LoadFrom returned false (file not loadable as .efkproj or .efkefc)"));
					return false;
				}
			}
			catch (TargetInvocationException tie)
			{
				var inner = tie.InnerException ?? tie;
				issues.Add(Issue.Error(path, 0, 0,
					$"editor load threw: {inner.GetType().Name}: {inner.Message}"));
				return false;
			}
			catch (Exception ex)
			{
				issues.Add(Issue.Error(path, 0, 0,
					$"editor load threw: {ex.GetType().Name}: {ex.Message}"));
				return false;
			}

			root = Core.Root;
			if (root == null)
			{
				issues.Add(Issue.Error(path, 0, 0,
					"Core.LoadFrom succeeded but Core.Root is null"));
				return false;
			}
			return true;
		}

		// IO.cs:1019-1022 NREs on _ch_node as XmlElement when _ch_node is an
		// XComment. Pre-reject on .efkproj raw XML (where this matters). For
		// .efkefc the dumped XML is generated by SaveAsXmlDocument which
		// never emits XComment nodes, so the equivalent path is in LoadIntoCore.
		static bool RejectXmlComments(string path, XDocument doc, List<Issue> issues)
		{
			var firstComment = doc.DescendantNodes().OfType<XComment>().FirstOrDefault();
			if (firstComment == null) return true;
			var li = (IXmlLineInfo)firstComment;
			issues.Add(Issue.Error(path, li.LineNumber, li.LinePosition,
				"XML comments crash editor load (EffekseerCore.IO.cs NRE). Remove or replace with a <Comment> element."));
			return false;
		}

		// Convert a System.Xml.XmlDocument (used by Core.SaveAsXmlDocument) to
		// a System.Xml.Linq.XDocument so the rest of the validator can use
		// SetLineInfo-aware descendants uniformly.
		static XDocument ToXDocument(XmlDocument src)
		{
			using var ms = new MemoryStream();
			using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false }))
				src.Save(xw);
			ms.Position = 0;
			return XDocument.Load(ms, LoadOptions.SetLineInfo);
		}

		// Decode UTF-8 bytes to string, stripping an optional UTF-8 BOM
		// (EF BB BF). Encoding.UTF8.GetString does NOT consume the BOM and
		// XDocument.Parse rejects a string that starts with U+FEFF as content.
		static string DecodeUtf8WithBom(byte[] raw)
		{
			int offset = 0;
			if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
				offset = 3;
			return Encoding.UTF8.GetString(raw, offset, raw.Length - offset);
		}
	}
}