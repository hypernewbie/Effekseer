using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace EffekseerValidate
{
	// Schema-driven structural validation. Uses a Schema.Document (either
	// loaded from disk or generated at startup) to detect unknown XML
	// elements - the failure mode where EffekseerCore silently drops an
	// element at the `if (property == null) continue` guard in IO.cs and
	// the author thinks the edit took effect.
	//
	// Walks recursively: for each element, looks up its expected type via
	// the schema, validates that every child element name matches a known
	// field, and recurses into nested data types, value types, and
	// collections. Recursion depth is bounded to prevent infinite loops on
	// pathological schemas.
	public static class SchemaCheck
	{
		// Deep enough for a modern-format file with FCurve nesting
		// (root=1 + 11 node levels + ~4-5 for FCurve/Keys/axis/scalar
		// chains = ~16-17 in practice). 48 leaves generous headroom and
		// is well below any pathological case. The validator warns (once)
		// if this is hit.
		const int MaxDepth = 48;

		// Type names that may contain variable-named Key<0..N> children
		// (FCurve axis containers). The Key<digits> escape hatch is
		// scoped to these types so an unknown <Key2> in CommonValues
		// is correctly rejected.
		static readonly HashSet<string> FCurveAxisTypeNames = new HashSet<string>
		{
			"FCurveAxis1D", "FCurveAxis2D", "FCurveAxis3D", "FCurveAxis4D",
		};

		public static List<Issue> Run(string path, XDocument doc, Schema.Document schema)
		{
			var issues = new List<Issue>();
			var root = doc.Root;
			if (root == null) return issues;

			// Walk each child of <EffekseerProject>. If it's a known root
			// element (per schema.RootElements), validate its tree by type.
			// Otherwise it's a non-data field (ToolVersion, Version,
			// StartFrame, EndFrame, IsLoop, or any Recording/Option/
			// Environment that we haven't added to RootElements yet).
			foreach (var child in root.Elements())
			{
				var name = child.Name.LocalName;
				if (schema.RootElements.TryGetValue(name, out var typeName))
				{
					ValidateByType(path, child, typeName, schema, issues, depth: 1);
				}
				else if (IsKnownNonData(name))
				{
					// Skipped - non-data fields don't have a typed schema.
				}
				else
				{
					issues.Add(Issue.Error(path, LineOf(child), 0,
						$"<EffekseerProject> unknown child <{name}>"));
				}
			}

			return issues;
		}

		static bool IsKnownNonData(string name)
		{
			return name is "ToolVersion" or "Version" or "StartFrame" or "EndFrame" or "IsLoop";
		}

		// Validate element's children against typeName's field list. For
		// each known field, recurse into the corresponding child element
		// using the field's type (data, value, or collection element type).
		// Unknown children are reported as errors.
		static void ValidateByType(string path, XElement element, string typeName,
			Schema.Document schema, List<Issue> issues, int depth)
		{
			if (depth > MaxDepth)
			{
				// One-time warning when we hit the depth limit. De-dupes
				// by checking for a "truncated" marker in issues list.
				if (!issues.Any(i => i.Message.StartsWith("schema check truncated")))
					issues.Add(Issue.Warning(path, 0, 0,
						$"schema check truncated at depth {MaxDepth} (deeply nested element under <{element.Name.LocalName}> not validated)"));
				return;
			}
			if (!schema.Types.TryGetValue(typeName, out var type)) return;

			// Collection-shape types (IEditableValueCollection):
			// each child of the element is an instance of ElementType,
			// not a field access. The schema marks these with kind="collection"
			// and ElementType set.
			if (type.Kind == "collection" && !string.IsNullOrEmpty(type.ElementType)
				&& schema.Types.ContainsKey(type.ElementType))
			{
				var expectedName = SimpleTypeName(type.ElementType);
				foreach (var item in element.Elements())
				{
					if (item.Name.LocalName != expectedName)
					{
						issues.Add(Issue.Error(path, LineOf(item), 0,
							$"<{element.Name.LocalName}> collection item expected <{expectedName}>, got <{item.Name.LocalName}>"));
						continue;
					}
					ValidateByType(path, item, type.ElementType, schema, issues, depth + 1);
				}
				return;
			}

			foreach (var child in element.Elements())
			{
				var name = child.Name.LocalName;
				// Key<N> variable-named keyframe children: only accept inside
				// FCurve axis types. An unknown <Key2> in CommonValues
				// is rejected (the Key<digits> name pattern is too loose
				// to be global).
				if (FCurveAxisTypeNames.Contains(typeName)
					&& name.StartsWith("Key") && name.Length > 3
					&& int.TryParse(name.Substring(3), out _))
				{
					// Treat as a leaf (no further recursion needed).
					continue;
				}

				if (!type.Fields.TryGetValue(name, out var field))
				{
					issues.Add(Issue.Error(path, LineOf(child), 0,
						$"<{element.Name.LocalName}> unknown child <{name}> (not a {typeName} property)"));
					continue;
				}

				// Leaf text validation. If the child is a leaf (no
				// child elements) and the field type is a recognizable
				// scalar (System primitive like Boolean/Single/Int32,
				// or Effekseer Data.Value scalar), the text must parse
				// as that type. Catches AI typos like <Enabled>maybe</Enabled>
				// or <Time>abc</Time>.
				if (!child.HasElements)
					ValidateLeafText(path, child, field.Type, issues);

				// Recurse based on field kind.
				if (field.Kind == "data" || field.Kind == "value")
				{
					if (!string.IsNullOrEmpty(field.Type) && schema.Types.ContainsKey(field.Type))
						ValidateByType(path, child, field.Type, schema, issues, depth + 1);
				}
				else if (field.Kind == "collection")
				{
					if (!string.IsNullOrEmpty(field.ElementType) && schema.Types.ContainsKey(field.ElementType))
					{
						// ObjectCollection<DynamicEquation>, ChildrenCollection
						// of Node, etc. - each direct child of the
						// collection element is an instance of ElementType.
						// The element name should match the C# type name
						// (per IO.cs:178 `children[i].GetType().Name`) unless
						// the schema overrides with ItemName (for IO-synthetic
						// collections like Gradient's <Key>).
						var expectedName = field.ItemName ?? SimpleTypeName(field.ElementType);
						foreach (var item in child.Elements())
						{
							if (item.Name.LocalName != expectedName)
							{
								issues.Add(Issue.Error(path, LineOf(item), 0,
									$"<{child.Name.LocalName}> collection item expected <{expectedName}>, got <{item.Name.LocalName}>"));
								continue;
							}
							ValidateByType(path, item, field.ElementType, schema, issues, depth + 1);
						}
					}
				}
				// "primitive" and "unknown" kinds are leaves - no recursion.
			}
		}

		static int LineOf(XElement e)
		{
			return ((IXmlLineInfo)e).LineNumber;
		}

		// Extract the simple type name from a schema key. Schema keys are
		// either bare ("Node") or compound for nested types
		// ("LocationValues+FixedParamater"); the XML element name uses
		// only the simple-name portion ("FixedParamater").
		static string SimpleTypeName(string schemaKey)
		{
			var plus = schemaKey.IndexOf('+');
			return plus > 0 ? schemaKey.Substring(plus + 1) : schemaKey;
		}

		// Leaf text validation. A leaf element's text must parse as its
		// field's scalar type. Catches AI typos like <Time>abc</Time> or
		// <Enabled>maybe</Enabled>.
		//
		// The mapping accepts both System type names (Int32, Single, etc.)
		// and the Effekseer wrapper names (Int, Float, Boolean, String)
		// used by Value.* types - these are the most common scalar leaves
		// in the format.
		static void ValidateLeafText(string path, XElement el, string typeName, List<Issue> issues)
		{
			var text = string.Concat(el.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
			if (text.Length == 0) return;  // empty leaf is fine (Core defaults it)

			var scalar = SimpleTypeName(typeName);

			bool ok = scalar switch
			{
				"Single" or "Double" or "Decimal" or "Float" =>
					double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
				"Int32" or "Int64" or "Int16" or "Byte" or "Int" =>
					long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
				"Boolean" or "Bool" => bool.TryParse(text, out _),
				"String" => true,
				_ => true,  // unknown scalar; don't validate
			};
			if (!ok)
			{
				issues.Add(Issue.Error(path, LineOf(el), 0,
					$"<{el.Name.LocalName}> text '{text}' is not a valid {typeName}"));
			}
		}
	}
}