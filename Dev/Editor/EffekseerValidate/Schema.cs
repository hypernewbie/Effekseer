using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Effekseer;
using Effekseer.Data;

namespace EffekseerValidate
{
#pragma warning disable CS8632 // nullable annotations not enabled project-wide
	// Reflection-driven schema of EffekseerCore's data model. Walks all
	// non-abstract public classes in Effekseer.Data and Effekseer.Data.Value,
	// records each type's public property names + the kind of each property
	// (value/data/collection). The validator's structural pass uses this to
	// detect unknown XML elements that EffekseerCore silently drops at
	// the `if (property == null) continue` guard in IO.cs.
	public static class Schema
	{
		// Field description. Type is the C# type name; Kind is "value",
		// "data", "collection", or "unknown". ElementType is set for
		// collections (the T in ObjectCollection<T>).
		public class Field
		{
			public string Type { get; set; } = "";
			public string Kind { get; set; } = "";
			public string? ElementType { get; set; }
			// Overrides the default item element name (which is the C#
			// type name for normal collections, or the simple-name
			// portion of ElementType for synthetic ones). IO.cs
			// sometimes hardcodes item names that don't match the C#
			// type name (e.g. Gradient emits <Key> rather than the
			// C# type name).
			public string? ItemName { get; set; }
		}

		public class TypeDef
		{
			public string Kind { get; set; } = "";
			public Dictionary<string, Field> Fields { get; set; } = new();
			public string? Base { get; set; }
			// For collection-shaped types (those implementing
			// IEditableValueCollection like DynamicInputCollection), this
			// records the element type so SchemaCheck can treat each XML
			// child of the collection as an instance of ElementType.
			public string? ElementType { get; set; }
		}

		public class Document
		{
			public string Version { get; set; } = "";
			public Dictionary<string, TypeDef> Types { get; set; } = new();
			// RootElements maps the unconditional children of <EffekseerProject>
			// (per Core.SaveAsXmlDocument) to their C# types. Optional children
			// (Behavior/Culling/LOD/Global/Dynamic/ProceduralModel) may be
			// absent when their value equals the default.
			public Dictionary<string, string> RootElements { get; set; } = new();
		}

		// Reflection entry point. Walks EffekseerCore.dll once.
		public static Document Generate()
		{
			var asm = typeof(Effekseer.Data.CommonValues).Assembly;
			var doc = new Document
			{
				Version = Core.Version,
			};

			// Data classes: Effekseer.Data.* namespace, non-abstract,
			// non-interface, non-generic. Compiler-generated closures (named
			// like <>c__DisplayClass<N>_<N>) and other compiler artifacts are
			// filtered out by IsCompilerGenerated.
			//
			// Nested types are keyed with the parent name as a prefix
			// (ParentType+NestedName) because several parent classes
			// define nested types with the same name (e.g.
			// LocationValues.FixedParamater, RotationValues.FixedParamater,
			// ScaleValues.FixedParamater). Using the simple name would
			// silently overwrite earlier entries.
			foreach (var t in asm.GetTypes())
			{
				if (t.Namespace != "Effekseer.Data") continue;
				if (t.IsAbstract || t.IsInterface || t.IsGenericType) continue;
				if (t.IsEnum) continue;
				if (t.Name.Contains("<")) continue;
				if (IsCompilerGenerated(t)) continue;
				doc.Types[TypeKey(t)] = DescribeType(t, "data");
			}

			// Value types: Effekseer.Data.Value.* namespace.
			foreach (var t in asm.GetTypes())
			{
				if (t.Namespace != "Effekseer.Data.Value") continue;
				if (t.IsAbstract || t.IsInterface || t.IsGenericType) continue;
				if (t.IsEnum) continue;
				if (t.Name.Contains("<")) continue;
				if (IsCompilerGenerated(t)) continue;
				doc.Types[TypeKey(t)] = DescribeType(t, "value");
			}

			// Synthetic IO-injected types (FCurveKeys1D/2D/3D/4D, GradientKey, etc.)
			// that have no C# representation but appear in serialized XML.
			// Define them as data containers with the per-axis element names
			// from IO.cs SaveToElement.
			// FCurveKeys1D/2D/3D/4D are the containers emitted by IO.cs:806
			// etc. Each holds a <Timeline> child plus one axis container
			// per axis (S, X/Y, R/G/B/A). The axis container in turn holds
			// StartType, EndType, OffsetMax, OffsetMin, Sampling as
			// scalars, plus variable-named Key<0..N> keyframe children
			// (the Key<digits> pattern is accepted in SchemaCheck, but only
			// when the parent type is one of the FCurveAxis* types).
			doc.Types["FCurveKeys1D"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>
				{
					["Timeline"] = new Field { Type = "Int", Kind = "value" },
					["S"] = new Field { Type = "FCurveAxis1D", Kind = "data" },
				},
			};
			doc.Types["FCurveKeys2D"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>
				{
					["Timeline"] = new Field { Type = "Int", Kind = "value" },
					["X"] = new Field { Type = "FCurveAxis2D", Kind = "data" },
					["Y"] = new Field { Type = "FCurveAxis2D", Kind = "data" },
				},
			};
			doc.Types["FCurveKeys3D"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>
				{
					["Timeline"] = new Field { Type = "Int", Kind = "value" },
					["X"] = new Field { Type = "FCurveAxis3D", Kind = "data" },
					["Y"] = new Field { Type = "FCurveAxis3D", Kind = "data" },
					["Z"] = new Field { Type = "FCurveAxis3D", Kind = "data" },
				},
			};
			doc.Types["FCurveKeys4D"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>
				{
					["Timeline"] = new Field { Type = "Int", Kind = "value" },
					["R"] = new Field { Type = "FCurveAxis4D", Kind = "data" },
					["G"] = new Field { Type = "FCurveAxis4D", Kind = "data" },
					["B"] = new Field { Type = "FCurveAxis4D", Kind = "data" },
					["A"] = new Field { Type = "FCurveAxis4D", Kind = "data" },
				},
			};
			// FCurveAxis1D/2D/3D/4D represent a single axis inside FCurveKeys*.
			// Holds StartType, EndType, OffsetMax, OffsetMin, Sampling.
			// Key<0..N> keyframe children are accepted by the Key<digits>
			// escape hatch in SchemaCheck (scoped to these axis types).
			var fcurveAxisFields = new Dictionary<string, Field>
			{
				["StartType"] = new Field { Type = "Int", Kind = "value" },
				["EndType"] = new Field { Type = "Int", Kind = "value" },
				["OffsetMax"] = new Field { Type = "Float", Kind = "value" },
				["OffsetMin"] = new Field { Type = "Float", Kind = "value" },
				["Sampling"] = new Field { Type = "Int", Kind = "value" },
			};
			doc.Types["FCurveAxis1D"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>(fcurveAxisFields) };
			doc.Types["FCurveAxis2D"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>(fcurveAxisFields) };
			doc.Types["FCurveAxis3D"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>(fcurveAxisFields) };
			doc.Types["FCurveAxis4D"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>(fcurveAxisFields) };
			// FCurveKey1D represents a single keyframe (Frame, Value, LeftX,
			// LeftY, RightX, RightY, InterpolationType). IO.cs:820.
			doc.Types["FCurveKey1D"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>
				{
					["Frame"] = new Field { Type = "Int", Kind = "value" },
					["Value"] = new Field { Type = "Float", Kind = "value" },
					["LeftX"] = new Field { Type = "Float", Kind = "value" },
					["LeftY"] = new Field { Type = "Float", Kind = "value" },
					["RightX"] = new Field { Type = "Float", Kind = "value" },
					["RightY"] = new Field { Type = "Float", Kind = "value" },
					["InterpolationType"] = new Field { Type = "Int", Kind = "value" },
				},
			};
			// Gradient.Key shape: Position + ColorR/G/B + Intensity.
			doc.Types["GradientKey"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>
				{
					["Position"] = new Field { Type = "Float", Kind = "value" },
					["ColorR"] = new Field { Type = "Float", Kind = "value" },
					["ColorG"] = new Field { Type = "Float", Kind = "value" },
					["ColorB"] = new Field { Type = "Float", Kind = "value" },
					["Intensity"] = new Field { Type = "Float", Kind = "value" },
				},
			};
			// Gradient AlphaKey shape: Position + Alpha.
			doc.Types["GradientAlphaKey"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>
				{
					["Position"] = new Field { Type = "Float", Kind = "value" },
					["Alpha"] = new Field { Type = "Float", Kind = "value" },
				},
			};

			// FCurveKey1D represents a single keyframe (Frame, Value, LeftX,
			// LeftY, RightX, RightY, InterpolationType). IO.cs:820. The
			// keyframe children are variable-named (Key<0..N>) so the
			// shape is documented here for the schema/markdown artifact;
			// SchemaCheck treats Key<N> as an opaque leaf (TODO: recurse
			// into this type when Key<N> children are present).
			doc.Types["FCurveKey1D"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>
				{
					["Frame"] = new Field { Type = "Int", Kind = "value" },
					["Value"] = new Field { Type = "Float", Kind = "value" },
					["LeftX"] = new Field { Type = "Float", Kind = "value" },
					["LeftY"] = new Field { Type = "Float", Kind = "value" },
					["RightX"] = new Field { Type = "Float", Kind = "value" },
					["RightY"] = new Field { Type = "Float", Kind = "value" },
					["InterpolationType"] = new Field { Type = "Int", Kind = "value" },
				},
			};
			// MaterialUniforms1/2/3/4 are the Float1/2/3/4 collections inside
			// <MaterialFile>. IO.cs:205+ emits each as a sequence of
			// <KeyValue> children (each containing <Key> + <Value>).
			// <MaterialTextureParameters> and <MaterialGradientParameters>
			// are siblings with the same structure.
			doc.Types["MaterialUniformsFloat"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>() };
			doc.Types["MaterialUniformsVector2D"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>() };
			doc.Types["MaterialUniformsVector3D"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>() };
			doc.Types["MaterialUniformsVector4D"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>() };
			doc.Types["MaterialUniformsTexture"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>() };
			doc.Types["MaterialUniformsGradient"] = new TypeDef { Kind = "data", Fields = new Dictionary<string, Field>() };

			// KeyValue = <Key> + <Value>. The <Value> type depends on the
			// parent (Float1=Float, Float2=Vector2D, etc.) so we model it
			// generically with a placeholder.
			doc.Types["MaterialKeyValue"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>
				{
					["Key"] = new Field { Type = "String", Kind = "value" },
					["Value"] = new Field { Type = "MaterialValuePlaceholder", Kind = "value" },
				},
			};
			doc.Types["MaterialValuePlaceholder"] = new TypeDef
			{
				Kind = "data",
				Fields = new Dictionary<string, Field>(),
			};

			// Root element mapping from Core.SaveAsXmlDocument (Core.cs:621-671).

			// Root element mapping from Core.SaveAsXmlDocument (Core.cs:621-671).
			// Option and Environment are written by SaveOption (Core.cs:1143-)
			// into config.option.xml but the same shape can appear in .efkproj
			// files (e.g. block.efkproj has an <Option> section).
			// Recording is conditional on the recording storage target
			// (Core.cs:646-651) but it IS a typed data element.
			doc.RootElements = new Dictionary<string, string>
			{
				["Root"] = "NodeRoot",
				["Behavior"] = "EffectBehaviorValues",
				["Culling"] = "EffectCullingValues",
				["LOD"] = "EffectLODValues",
				["Global"] = "GlobalValues",
				["Dynamic"] = "DynamicValues",
				["ProceduralModel"] = "ProceduralModelValues",
				["Recording"] = "RecordingValues",
				["Option"] = "OptionValues",
				["Environment"] = "EnvironmentValues",
			};

			return doc;
		}

		static TypeDef DescribeType(Type t, string defaultKind)
		{
			var td = new TypeDef { Kind = defaultKind };
			if (t.BaseType != null
				&& t.BaseType != typeof(object)
				&& t.BaseType.Name != "NodeBase"
				&& !t.BaseType.IsGenericType
				&& !t.BaseType.IsGenericTypeDefinition
				&& HasKnownFieldsOrIsEffekseerType(t.BaseType))
				td.Base = TypeKey(t.BaseType);

			foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (p.GetIndexParameters().Length > 0) continue;
				if (p.Name == "Item") continue;
				// Helper refs that aren't serialized
				if (p.Name == "Parent") continue;

				var field = new Field { Type = TypeKey(p.PropertyType) };
				var kind = ClassifyProperty(p.PropertyType, out var elementType);
				field.Kind = kind;
				field.ElementType = elementType != null ? TypeKeyForElementType(p.PropertyType, elementType) : null;
				td.Fields[p.Name] = field;
			}

			// Public fields. Some EffekseerCore types use fields instead of
			// properties (e.g. FCurveVector3D.Timeline is a public field, not
			// a property). GetProperties skips these. GetFields with the
			// same binding flags covers the common cases.
			foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				if (f.IsStatic || f.IsLiteral || f.IsInitOnly) continue;
				if (f.Name == "value__") continue;  // backing field for enums
				if (td.Fields.ContainsKey(f.Name)) continue;  // already from a property
				var fld = new Field { Type = TypeKey(f.FieldType) };
				var kind = ClassifyProperty(f.FieldType, out var elementType);
				fld.Kind = kind;
				fld.ElementType = elementType != null ? TypeKeyForElementType(f.FieldType, elementType) : null;
				td.Fields[f.Name] = fld;
			}

			// Augment with IO-injected synthetic fields. The IO.cs
			// SaveToElement methods add <Keys> children to FCurve* types
			// that aren't represented in the C# class. Hardcode the
			// augmentation so the schema-driven recursive check can walk
			// into them.
			AugmentSyntheticFields(t, td);

			// Collection-shape detection. Types implementing
			// IEditableValueCollection (DynamicInputCollection,
			// DynamicEquationCollection, etc.) are represented in XML as
			// a single collection element containing one child per item.
			// Mark them as kind="collection" with ElementType pointing to
			// the type of items (derived from the only List<T> field).
			// This lets SchemaCheck treat <Inputs>...</Inputs> as a
			// collection of <DynamicInput> children rather than a data
			// class with a Values field.
			if (typeof(IEditableValueCollection).IsAssignableFrom(t)
				&& !t.IsInterface
				&& !t.IsAbstract)
			{
				var elementType = FindCollectionItemType(t);
				if (elementType != null)
				{
					td.Kind = "collection";
					td.ElementType = elementType;
					// Drop the single internal Values field - it isn't
					// represented as a child in the XML.
					td.Fields.Remove("Values");
				}
			}

			return td;
		}

		// Find the element type of a collection-shaped class (one
		// implementing IEditableValueCollection). Usually it's the T in
		// the only List<T> public property.
		static string? FindCollectionItemType(Type t)
		{
			foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (p.PropertyType.IsGenericType
					&& p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
				{
					var args = p.PropertyType.GetGenericArguments();
					if (args.Length > 0)
						return TypeKey(args[0]);
				}
			}
			return null;
		}

		// Build a "synthetic collection" Field whose children are all named
		// <KeyValue>. Used for MaterialFileParameter's Float1/2/3/4,
		// Texture, and Gradient collections. The nameSuffix distinguishes
		// the types in the schema (unused by SchemaCheck, but visible in
		// schema.json for downstream consumers).
		static Field MakeUniformsField(string nameSuffix)
		{
			return new Field
			{
				Type = "MaterialUniforms" + nameSuffix,
				Kind = "collection",
				ItemName = "KeyValue",
			};
		}

		// The MaterialUniforms* types are collection containers whose
		// children are <KeyValue> elements. They have no fields of their
		// own - the schema check just walks into the children via the
		// ItemName override on the parent field.

		// IO.cs SaveToElement methods (e.g. line 806 for FCurveVector3D)
		// emit <Keys> as a child containing per-axis elements. The C#
		// class doesn't expose Keys as a property; it's a synthetic
		// collection added at serialization time. Add it to the schema
		// so the recursive walk accepts it.
		static void AugmentSyntheticFields(Type t, TypeDef td)
		{
			if (t.Name == "FCurveVector3D")
			{
				td.Fields["Keys"] = new Field
				{
					Type = "FCurveKeys3D",
					Kind = "data",
				};
			}
			else if (t.Name == "FCurveVector2D")
			{
				td.Fields["Keys"] = new Field
				{
					Type = "FCurveKeys2D",
					Kind = "data",
				};
			}
			else if (t.Name == "FCurveColorRGBA")
			{
				td.Fields["Keys"] = new Field
				{
					Type = "FCurveKeys4D",
					Kind = "data",
				};
			}
			else if (t.Name == "FCurveScalar")
			{
				td.Fields["Keys"] = new Field
				{
					Type = "FCurveKeys1D",
					Kind = "data",
				};
			}
else if (t.Name == "MaterialFileParameter")
			{
				// IO.cs:185 SaveToElement emits <Path> + <Float1/2/3/4>
				// (collections of <KeyValue>) + <Texture> + <Gradient>.
				// MaterialFileParameter is a generic in Effekseer.Data.Value
				// (per arch review) so its own properties aren't useful;
				// hardcode the IO-injected children.
				td.Fields["Path"] = new Field { Type = "PathForImage", Kind = "value" };
				td.Fields["Float1"] = MakeUniformsField("Float");
				td.Fields["Float2"] = MakeUniformsField("Vector2D");
				td.Fields["Float3"] = MakeUniformsField("Vector3D");
				td.Fields["Float4"] = MakeUniformsField("Vector4D");
				td.Fields["Texture"] = MakeUniformsField("Texture");
				td.Fields["Gradient"] = MakeUniformsField("Gradient");
			}
			else if (t.Name == "Gradient")
			{
				// IO.cs:119 SaveToElement adds <ColorMarkers> and
				// <AlphaMarkers> as collections of <Key> elements.
				// Neither is a C# property on Gradient. The item
				// element name is hardcoded "Key" in IO.cs (not the
				// C# type name), so ItemName overrides the default.
				td.Fields["ColorMarkers"] = new Field
				{
					Type = "GradientKey",
					Kind = "collection",
					ElementType = "GradientKey",
					ItemName = "Key",
				};
				td.Fields["AlphaMarkers"] = new Field
				{
					Type = "GradientAlphaKey",
					Kind = "collection",
					ElementType = "GradientAlphaKey",
					ItemName = "Key",
				};
			}
		}

		static string ClassifyProperty(Type t, out string? elementType)
		{
			// Order matters: check collection-ness BEFORE namespace checks,
			// because ObjectCollection<T> lives in Effekseer.Data.Value but
			// is a collection, not a value. Walk the base-type chain to
			// catch subclasses of ObjectCollection<T> like
			// DynamicEquationCollection which themselves live in
			// Effekseer.Data and are non-generic.
			if (FindCollectionElementType(t, out elementType))
				return "collection";

			// List<T> / IList<T> as a property type: extract the element
			// type T. DynamicInputCollection.Values is `List<DynamicInput>`
			// and IO.cs treats it as a collection of DynamicInput elements
			// (each <DynamicInput> child of <Inputs>).
			if (t.IsGenericType)
			{
				var def = t.GetGenericTypeDefinition();
				if (def == typeof(List<>) || def == typeof(IList<>)
					|| def == typeof(ICollection<>) || def == typeof(IEnumerable<>)
					|| def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
				{
					var args = t.GetGenericArguments();
					if (args.Length > 0)
					{
						elementType = args[0].Name;
						return "collection";
					}
				}
			}

			elementType = null;
			if (t.Namespace == "Effekseer.Data.Value") return "value";
			if (t.Namespace == "Effekseer.Data") return "data";
			if (t.IsArray) return "collection";
			if (typeof(IEditableValueCollection).IsAssignableFrom(t))
				return "collection";
			// System value types and string. FloatWithRandom.Center is
			// declared as Single (System.Single) - it's a leaf, not a
			// container, so the validator's recursive walk won't descend
			// into it. Tagging these as "primitive" (instead of "unknown")
			// disambiguates them from genuinely opaque types like Delegate.
			if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal))
				return "primitive";
			if (t.IsValueType) return "primitive";
			return "unknown";
		}

		// Walk the BaseType chain looking for ObjectCollection<T> (or any
		// subclass). Returns true with elementType = T if found. Also
		// recognizes NodeBase.ChildrenCollection as a collection of Node.
		// ElementType is the raw type name; callers should run it through
		// TypeKeyForElementType to get the schema key.
		static bool FindCollectionElementType(Type t, out string? elementType)
		{
			elementType = null;

			// ChildrenCollection (NodeBase.cs:364) is a plain nested class
			// that does NOT implement IEditableValueCollection and does NOT
			// extend ObjectCollection<>. Recognize by name.
			if (t.Name == "ChildrenCollection" && t.Namespace == "Effekseer.Data")
			{
				elementType = "Node";
				return true;
			}

			for (var current = t; current != null; current = current.BaseType)
			{
				if (current.IsGenericType
					&& current.GetGenericTypeDefinition().Name.StartsWith("ObjectCollection"))
				{
					var args = current.GetGenericArguments();
					if (args.Length > 0)
						elementType = args[0].Name;
					return true;
				}
			}
			return false;
		}

		// Convert a raw element type name (from FindCollectionElementType) to
		// the schema key. For top-level types this is a no-op; for nested
		// types we'd need the declaring context, but collection element
		// types in Effekseer.Data are typically top-level (DynamicEquation,
		// ProceduralModelParameter, etc.).
		static string? TypeKeyForElementType(Type collectionType, string elementTypeName)
		{
			for (var current = collectionType; current != null; current = current.BaseType)
			{
				if (current.IsGenericType
					&& current.GetGenericTypeDefinition().Name.StartsWith("ObjectCollection"))
				{
					var args = current.GetGenericArguments();
					if (args.Length > 0)
						return TypeKey(args[0]);
					break;
				}
			}
			// ChildrenCollection case or fallback: name as-is
			return elementTypeName;
		}

		// Compiler-generated types carry the [CompilerGenerated] attribute
		// and have names like <>c__DisplayClass<N>_<N>. The Name check above
		// already filters most of them; this is a belt-and-braces second
		// pass for any that survive the name heuristic.
		static bool IsCompilerGenerated(Type t)
		{
			var attr = t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>(inherit: false);
			return attr != null;
		}

		// True if t lives in Effekseer.Data or Effekseer.Data.Value. Used
		// to decide whether a Base reference points to a known schema type
		// (and is worth recording) or to an external framework type (and
		// should be dropped to avoid dangling refs in the schema).
		static bool HasKnownFieldsOrIsEffekseerType(Type t)
		{
			return t.Namespace == "Effekseer.Data" || t.Namespace == "Effekseer.Data.Value";
		}

		// Schema key for a type. Top-level types use their simple name
		// (CommonValues, Float). Nested types use ParentName+NestedName
		// (LocationValues+FixedParamater) to avoid name collisions when
		// several parents define nested types with the same name.
		internal static string TypeKey(Type t)
		{
			if (t.DeclaringType != null)
				return t.DeclaringType.Name + "+" + t.Name;
			return t.Name;
		}

		public static void WriteJson(Document doc, string path)
		{
			var options = new JsonSerializerOptions
			{
				WriteIndented = true,
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
				IncludeFields = false,
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			};
			File.WriteAllText(path, JsonSerializer.Serialize(doc, options));
		}

		public static Document? LoadJson(string path)
		{
			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true,
			};
			return JsonSerializer.Deserialize<Document>(File.ReadAllText(path), options);
		}

		// Human-readable markdown summary. One section per type, listing
		// each field with its kind and type. Aim: AI agents can read this
		// to know what XML elements are valid where.
		public static void WriteMarkdown(Document doc, string path)
		{
			var sb = new StringBuilder();
			sb.AppendLine($"# EffekseerCore data schema");
			sb.AppendLine();
			sb.AppendLine($"- Version: `{doc.Version}`");
			sb.AppendLine($"- Types: {doc.Types.Count}");
			sb.AppendLine();

			sb.AppendLine("## Root elements");
			sb.AppendLine();
			sb.AppendLine("Unconditional children of `<EffekseerProject>` (per `Core.SaveAsXmlDocument`).");
			sb.AppendLine("Behavior/Culling/LOD/Global/Dynamic/ProceduralModel may be elided when their value equals the default.");
			sb.AppendLine();
			sb.AppendLine("| Element | Type |");
			sb.AppendLine("|---|---|");
			foreach (var kv in doc.RootElements)
				sb.AppendLine($"| `<{kv.Key}>` | `{kv.Value}` |");
			sb.AppendLine();

			sb.AppendLine("## Types");
			sb.AppendLine();

			// Group by kind (data first, then value, then collection)
			var byKind = doc.Types
				.GroupBy(kv => kv.Value.Kind)
				.OrderBy(g => g.Key == "data" ? 0 : g.Key == "value" ? 1 : 2);
			foreach (var group in byKind)
			{
				sb.AppendLine($"### {char.ToUpper(group.Key[0]) + group.Key.Substring(1)} types");
				sb.AppendLine();
				foreach (var (name, td) in group.OrderBy(kv => kv.Key))
				{
					sb.AppendLine($"#### `{name}`");
					if (td.Base != null) sb.AppendLine($"Extends `{td.Base}`.");
					sb.AppendLine();
					if (td.Fields.Count == 0)
					{
						sb.AppendLine("(no public properties)");
						sb.AppendLine();
						continue;
					}
					sb.AppendLine("| Field | Type | Kind |");
					sb.AppendLine("|---|---|---|");
					foreach (var (fname, f) in td.Fields.OrderBy(kv => kv.Key))
					{
						var elem = f.ElementType != null ? $" of `{f.ElementType}`" : "";
						sb.AppendLine($"| `<{fname}>` | `{f.Type}`{elem} | {f.Kind} |");
					}
					sb.AppendLine();
				}
			}

			File.WriteAllText(path, sb.ToString());
		}
	}
}
#pragma warning restore CS8632