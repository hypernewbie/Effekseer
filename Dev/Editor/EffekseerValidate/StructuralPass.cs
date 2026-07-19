using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace EffekseerValidate
{
	// Shallow structural checks over the raw XDocument. Catches the most common
	// AI-induced mistakes that EffekseerCore.LoadFrom would silently mask
	// (missing required fields, unknown root children, StartFrame > EndFrame).
	// Deeper per-type checks live in a future schema-generator tool which
	// walks every property's Value type and range.
	public static class StructuralPass
	{
		// Children unconditionally appended by Core.SaveAsXmlDocument
		// (Core.cs:621-671). Behavior/Culling/LOD/Global/Dynamic/ProceduralModel
		// are omitted when their value equals the default; Recording is gated on
		// a settings flag. ToolVersion is allowed missing (triggers migration
		// chain but file still loads) so it lives as a warning below, not here.
		static readonly string[] RequiredChildren = new[]
		{
			"Root", "Version", "StartFrame", "EndFrame", "IsLoop",
		};

		public static List<Issue> Run(string path, XDocument doc)
		{
			var issues = new List<Issue>();
			var root = doc.Root;
			if (root == null || root.Name.LocalName != "EffekseerProject")
			{
				issues.Add(Issue.Error(path, 0, 0,
					$"root element must be <EffekseerProject>, got <{(root?.Name.LocalName ?? "(null)")}>"));
				return issues;
			}

			var present = new HashSet<string>(root.Elements().Select(e => e.Name.LocalName));
			foreach (var req in RequiredChildren)
			{
				if (!present.Contains(req))
				{
					issues.Add(Issue.Error(path, LineOf(root), 0,
						$"<EffekseerProject> missing required child <{req}>"));
				}
			}

			// ToolVersion: missing triggers the full migration chain
			// (Core.cs:729-831). Without it, IsChanged/StartFrame/EndFrame get
			// migrated through every historical format. Warn the author.
			var toolVersion = root.Element("ToolVersion");
			if (toolVersion == null || string.IsNullOrWhiteSpace(toolVersion.Value))
			{
				issues.Add(Issue.Warning(path, LineOf(root), 0,
					"<ToolVersion> missing - migration chain runs unconditionally. Set to current version."));
			}

			// StartFrame must be <= EndFrame and both must parse as integers.
			// Core.cs:896-898 reads them as int via GetTextAsInt(), which returns
			// 0 on parse failure rather than throwing - so the parser won't catch
			// a non-numeric value. We do.
			var start = ParseInt(root.Element("StartFrame")?.Value);
			var end = ParseInt(root.Element("EndFrame")?.Value);
			if (start == null)
			{
				var el = root.Element("StartFrame");
				if (el != null)
					issues.Add(Issue.Error(path, LineOf(el), 0,
						$"<StartFrame> must be an integer, got '{el.Value}'"));
			}
			if (end == null)
			{
				var el = root.Element("EndFrame");
				if (el != null)
					issues.Add(Issue.Error(path, LineOf(el), 0,
						$"<EndFrame> must be an integer, got '{el.Value}'"));
			}
			if (start.HasValue && end.HasValue && start.Value > end.Value)
			{
				var el = root.Element("EndFrame");
				issues.Add(Issue.Error(path, LineOf(el), 0,
					$"<StartFrame> ({start.Value}) must be <= <EndFrame> ({end.Value})"));
			}

			// IsLoop must parse as bool (Core.cs:898 uses bool.Parse, throws).
			var isLoop = root.Element("IsLoop");
			if (isLoop != null && !bool.TryParse(isLoop.Value, out _))
			{
				issues.Add(Issue.Error(path, LineOf(isLoop), 0,
					$"<IsLoop> must be 'true' or 'false', got '{isLoop.Value}'"));
			}

			return issues;
		}

		static int LineOf(XElement e)
		{
			return ((IXmlLineInfo)e).LineNumber;
		}

		static int? ParseInt(string s)
		{
			if (int.TryParse(s, out var v)) return v;
			return null;
		}
	}
}